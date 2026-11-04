using System.Globalization;
using System.Text.RegularExpressions;
using MikePDF.Ink.Tests.Support;

namespace MikePDF.Ink.Tests;

/// <summary>Content stream numbers, path bounds and the appearance builder (ink-pen.md 3.8, annotation-core.md 3.9; supports INK-10).</summary>
public sealed partial class InkPathAndAppearanceTests
{
    private static readonly string[] Operators = ["m", "l", "c", "h"];

    [GeneratedRegex(@"^(-?\d+(\.\d{1,3})?)$")]
    private static partial Regex PlainNumber();

    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(1.0, "1")]
    [InlineData(1.5, "1.5")]
    [InlineData(-2.25, "-2.25")]
    [InlineData(1.23456, "1.235")]
    [InlineData(-1.0004, "-1")]
    [InlineData(-0.0004, "0")]
    [InlineData(0.0005, "0.001")]
    [InlineData(1e-7, "0")]
    [InlineData(12345.6789, "12345.679")]
    [InlineData(1e15, "1000000000000000")]
    [InlineData(-1e20, "-100000000000000000000")]
    [InlineData(double.NaN, "0")]
    [InlineData(double.PositiveInfinity, "0")]
    [InlineData(double.NegativeInfinity, "0")]
    public void Content_NumbersAreInvariantTrimmedAndNeverUseExponents(double value, string expected)
    {
        Assert.Equal(expected, InkPath.FormatNumber(value));
    }

    [Fact]
    public void Content_NegativeZeroIsWrittenAsZero()
    {
        Assert.Equal("0", InkPath.FormatNumber(BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000UL))));
        Assert.Equal("0", InkPath.FormatNumber(-0.0001));
    }

    [Fact]
    public void Content_NumbersIgnoreTheCurrentCulture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1.5", InkPath.FormatNumber(1.5));
            Assert.Equal("-1234.567", InkPath.FormatNumber(-1234.5671));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Content_APathWritesOneOperatorPerLine()
    {
        var path = new InkPath(
        [
            new PathSegment(PathSegmentKind.MoveTo, 1, 2, 0, 0, 0, 0),
            new PathSegment(PathSegmentKind.LineTo, 3.5, -0.0, 0, 0, 0, 0),
            new PathSegment(PathSegmentKind.CubicTo, 4, 5, 6.1234, 7, 8, 9),
            new PathSegment(PathSegmentKind.Close, 0, 0, 0, 0, 0, 0),
        ]);
        Assert.Equal("1 2 m\n3.5 0 l\n4 5 6.123 7 8 9 c\nh\n", path.ToContentStream());
    }

    [Fact]
    public void Content_EveryNumberOfAFittedPathIsPlainDecimalText()
    {
        foreach (var trace in Traces.Build(30))
        {
            var simplified = Simplifier.Simplify(trace.Samples, InkStyle.DefaultPen);
            var content = StrokeOutline.Fit(simplified.Xy, simplified.Widths).ToContentStream();
            Assert.DoesNotContain('E', content);
            foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var tokens = line.Split(' ');
                var op = tokens[^1];
                Assert.Contains(op, Operators);
                Assert.Equal(op switch { "m" => 3, "l" => 3, "c" => 7, _ => 1 }, tokens.Length);
                foreach (var number in tokens.Take(tokens.Length - 1))
                {
                    Assert.Matches(PlainNumber(), number);
                    Assert.NotEqual("-0", number);
                }
            }
        }
    }

    [Fact]
    public void Content_BoundsUseTheCurveExtremaNotTheControlPoints()
    {
        var path = new InkPath(
        [
            new PathSegment(PathSegmentKind.MoveTo, 0, 0, 0, 0, 0, 0),
            new PathSegment(PathSegmentKind.CubicTo, 0, 10, 10, 10, 10, 0),
            new PathSegment(PathSegmentKind.Close, 0, 0, 0, 0, 0, 0),
        ]);
        Assert.Equal(0, path.Bounds.Left, 9);
        Assert.Equal(10, path.Bounds.Right, 9);
        Assert.Equal(0, path.Bounds.Bottom, 9);
        Assert.Equal(7.5, path.Bounds.Top, 9);
    }

    [Fact]
    public void Content_BoundsOfFittedPathsEncloseAndTightlyMatchTheFlattenedCurve()
    {
        foreach (var trace in Traces.Build(60))
        {
            var simplified = Simplifier.Simplify(trace.Samples, InkStyle.DefaultPen);
            var path = StrokeOutline.Fit(simplified.Xy, simplified.Widths);
            var curve = Geometry.Flatten(path, 200).Single();
            Assert.True(path.Bounds.Left <= curve.Min(p => p.X) + 1e-9);
            Assert.True(path.Bounds.Right >= curve.Max(p => p.X) - 1e-9);
            Assert.True(path.Bounds.Bottom <= curve.Min(p => p.Y) + 1e-9);
            Assert.True(path.Bounds.Top >= curve.Max(p => p.Y) - 1e-9);
            Assert.True(path.Bounds.Left >= curve.Min(p => p.X) - 0.01);
            Assert.True(path.Bounds.Right <= curve.Max(p => p.X) + 0.01);
            Assert.True(path.Bounds.Bottom >= curve.Min(p => p.Y) - 0.01);
            Assert.True(path.Bounds.Top <= curve.Max(p => p.Y) + 0.01);
        }
    }

    [Fact]
    public void Content_AnEmptyPathHasZeroBoundsAndNonFiniteSegmentsAreRejected()
    {
        Assert.Equal((0.0, 0.0, 0.0, 0.0), new InkPath([]).Bounds);
        Assert.Throws<ArgumentException>(() => new InkPath([new PathSegment(PathSegmentKind.MoveTo, double.NaN, 0, 0, 0, 0, 0)]));
    }

    // ---- InkAppearance ---------------------------------------------------------------------------------

    private static SimplifiedStroke Line(float x0, float y0, float x1, float y1, float w) => new([x0, y0, x1, y1], [w, w]);

    [Fact]
    public void Appearance_ContentHasPathOperatorsThenAFillPerStrokeSeparatedByNewlines()
    {
        var result = InkAppearance.Build([Line(0, 0, 50, 0, 4), new SimplifiedStroke([100f, 100f], [6f])]);
        var strokes = result.Content.Split('\n');
        Assert.Equal("f", strokes.First(l => l == "f"));
        Assert.Equal(2, result.Content.Split('\n').Count(l => l == "f"));
        Assert.EndsWith("f", result.Content, StringComparison.Ordinal);
        Assert.Equal(2, result.Paths.Count);
        Assert.Contains("h\nf\n", result.Content, StringComparison.Ordinal);
        Assert.StartsWith(result.Paths[0].ToContentStream() + "f\n", result.Content, StringComparison.Ordinal);
        Assert.Equal(result.Paths[0].ToContentStream() + "f\n" + result.Paths[1].ToContentStream() + "f", result.Content);
    }

    [Fact]
    public void Appearance_BoundsAreTheExactUnionOfTheOutlinesWithNoPadding()
    {
        var result = InkAppearance.Build([Line(10, 20, 50, 20, 4), new SimplifiedStroke([100f, 100f], [6f])]);
        // The fitted outline may sit up to the 0.1 fit tolerance inside the ideal outline, never outside it.
        Assert.InRange(result.Left, 8.0, 8.1); // 10 - half width 2
        Assert.InRange(result.Bottom, 18.0, 18.1);
        Assert.InRange(result.Right, 102.9, 103.0); // the dot at 100 with radius 3 is exact
        Assert.InRange(result.Top, 102.9, 103.0);
        foreach (var path in result.Paths)
        {
            Assert.True(path.Bounds.Left >= result.Left && path.Bounds.Right <= result.Right);
            Assert.True(path.Bounds.Bottom >= result.Bottom && path.Bounds.Top <= result.Top);
        }
    }

    [Fact]
    public void Appearance_ADotIsAFilledCircleOfItsWidth()
    {
        var result = InkAppearance.Build([new SimplifiedStroke([30f, 40f], [10f])]);
        Assert.Equal(25, result.Left, 3);
        Assert.Equal(35, result.Right, 3);
        Assert.Equal(35, result.Bottom, 3);
        Assert.Equal(45, result.Top, 3);
        Assert.Equal(4, result.Content.Split('\n').Count(l => l.EndsWith(" c", StringComparison.Ordinal)));
    }

    [Fact]
    public void Appearance_BuildsFromTheRealPipelineForManyTraces()
    {
        foreach (var chunk in Traces.Build(40).Chunk(4))
        {
            var strokes = chunk.Select(t => Simplifier.Simplify(t.Samples, InkStyle.DefaultPen)).ToList();
            var result = InkAppearance.Build(strokes);
            Assert.True(result.Right > result.Left && result.Top > result.Bottom);
            Assert.Equal(chunk.Length, result.Content.Split('\n').Count(l => l == "f"));
            Assert.True(result.Content.Length < StrokeLimits.MaxAppearanceBytes);
        }
    }

    [Fact]
    public void Appearance_InvalidStrokeListsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => InkAppearance.Build([]));
        Assert.Throws<ArgumentNullException>(() => InkAppearance.Build(null!));
        Assert.Throws<ArgumentException>(() => InkAppearance.Build([new SimplifiedStroke([], [])]));
        Assert.Throws<ArgumentException>(() => InkAppearance.Build([new SimplifiedStroke([1f, 2f, 3f], [1f])]));
        Assert.Throws<ArgumentException>(() => InkAppearance.Build([new SimplifiedStroke([float.NaN, 0f], [1f])]));
        Assert.Throws<ArgumentException>(() => InkAppearance.Build([new SimplifiedStroke([0f, 0f], [float.PositiveInfinity])]));
        Assert.Throws<ArgumentException>(() => InkAppearance.Build([new SimplifiedStroke(new float[(StrokeLimits.MaxSamplesPerStroke + 1) * 2], new float[StrokeLimits.MaxSamplesPerStroke + 1])]));
        Assert.Throws<ArgumentException>(() => InkAppearance.Build([null!]));
    }

    [Fact]
    public void Appearance_ContentAboveTwoMebibytesThrowsALimitException()
    {
        var rng = new Random(8);
        var strokes = new List<SimplifiedStroke>();
        for (var s = 0; s < 6; s++)
        {
            var xy = new float[40_000];
            var widths = new float[20_000];
            for (var i = 0; i < 20_000; i++)
            {
                xy[2 * i] = (float)(rng.NextDouble() * 400);
                xy[(2 * i) + 1] = (float)(rng.NextDouble() * 400);
                widths[i] = 2f;
            }

            strokes.Add(new SimplifiedStroke(xy, widths));
        }

        var ex = Assert.Throws<InkLimitException>(() => InkAppearance.Build(strokes));
        Assert.Contains("2097152", ex.Message, StringComparison.Ordinal);
    }
}
