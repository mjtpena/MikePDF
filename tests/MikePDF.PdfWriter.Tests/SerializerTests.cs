using System.Text;

namespace MikePDF.PdfWriter.Tests;

public sealed class SerializerTests
{
    private static string Text(PdfObject o) => Encoding.Latin1.GetString(PdfSerializer.Serialize(o).Value);

    private static PdfObject Parse(string text)
    {
        var ctx = new ParseContext(ParserLimits.Default, strict: true);
        return new ObjectParser(Encoding.Latin1.GetBytes(text), ctx).ParseObject();
    }

    [Fact]
    public void Scalars()
    {
        Assert.Equal("null", Text(PdfNull.Instance));
        Assert.Equal("true", Text(PdfBoolean.True));
        Assert.Equal("false", Text(PdfBoolean.False));
        Assert.Equal("-42", Text(new PdfInteger(-42)));
        Assert.Equal("12 3 R", Text(new PdfReference(12, 3)));
    }

    [Theory]
    [InlineData(0.0, "0.0")]
    [InlineData(5.0, "5.0")]
    [InlineData(-5.0, "-5.0")]
    [InlineData(0.1, "0.1")]
    [InlineData(-0.25, "-0.25")]
    [InlineData(123.456, "123.456")]
    [InlineData(1e-20, "0.00000000000000000001")]
    [InlineData(-1.5e-7, "-0.00000015")]
    [InlineData(841.8897637795277, "841.8897637795277")]
    [InlineData(0.30000000000000004, "0.30000000000000004")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(2.5e20, "250000000000000000000.0")]
    [InlineData(0.000001, "0.000001")]
    public void RealsNeverUseExponents(double value, string expected)
    {
        string text = Text(new PdfReal(value));
        Assert.Equal(expected, text);
        Assert.DoesNotContain("E", text, StringComparison.OrdinalIgnoreCase);
    }

    // Found by the append fuzz target: a real with more than 15 fractional digits was cut, so the object did not read back as written
    // and the writer's own self-check refused the whole update.
    [Theory]
    [InlineData(841.8897637795277)]
    [InlineData(595.2755905511812)]
    [InlineData(0.1 + 0.2)]
    [InlineData(1e-19)]
    [InlineData(-3.3e-100)]
    [InlineData(123456789.12345679)]
    [InlineData(3.4028234663852886e38)]
    public void RealsReadBackAsExactlyTheSameDouble(double value)
    {
        string text = Text(new PdfReal(value));
        Assert.DoesNotContain("E", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(value, Assert.IsType<PdfReal>(Parse(text)).Value);
    }

    [Fact]
    public void RealsRoundTripExactlyAcrossTheRange()
    {
        var rng = new Random(2026);
        for (int i = 0; i < 20_000; i++)
        {
            // Mantissa bits from the generator, exponent spread over the float range down to 1e-60 (about 61 characters when written).
            double v = (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(-60, 38));
            if (v == 0) continue;
            Assert.Equal(v, Assert.IsType<PdfReal>(Parse(Text(new PdfReal(v)))).Value);
        }
    }

    [Fact]
    public void NegativeZeroIsWrittenAsZero() => Assert.Equal("0.0", Text(new PdfReal(double.NegativeZero)));

    [Fact]
    public void RealsRoundTrip()
    {
        foreach (double v in new[] { 0.5, -3.14159, 100.0, 1234.5678, 0.000123, 612.0, 792.25 })
        {
            var back = Assert.IsType<PdfReal>(Parse(Text(new PdfReal(v))));
            Assert.Equal(v, back.Value, 12);
        }
    }

    [Theory]
    [InlineData("A", "/A")]
    [InlineData("A B", "/A#20B")]
    [InlineData("a#b", "/a#23b")]
    [InlineData("(x)", "/#28x#29")]
    [InlineData("<>[]{}/%", "/#3C#3E#5B#5D#7B#7D#2F#25")]
    [InlineData("", "/")]
    [InlineData("é", "/#E9")]
    [InlineData("~", "/~")]
    [InlineData("\u007F", "/#7F")]
    public void NamesAreEscaped(string name, string expected) => Assert.Equal(expected, Text(new PdfName(name)));

    [Fact]
    public void StringsAreEscapedAndKeepTheirFormat()
    {
        Assert.Equal("(abc)", Text(PdfString.FromAscii("abc")));
        Assert.Equal(@"(a\(b\)c\\d)", Text(PdfString.FromAscii(@"a(b)c\d")));
        Assert.Equal(@"(\n\r)", Text(new PdfString("\n\r"u8)));
        Assert.Equal(@"(\000\001\177\377)", Text(new PdfString(new byte[] { 0, 1, 127, 255 })));
        Assert.Equal("<00FF10>", Text(new PdfString(new byte[] { 0, 255, 16 }, PdfStringFormat.Hex)));
        Assert.Equal("<>", Text(new PdfString([], PdfStringFormat.Hex)));
        Assert.Equal("()", Text(new PdfString([])));
    }

    [Fact]
    public void ContainersKeepOrderAndRoundTrip()
    {
        var dict = new PdfDictionary(
        [
            new(new PdfName("Zed"), new PdfInteger(1)),
            new(new PdfName("Alpha"), new PdfArray([new PdfReal(1.5), PdfString.FromAscii("x"), new PdfName("N"), PdfNull.Instance, new PdfReference(4, 0)])),
            new(new PdfName("Mid"), new PdfDictionary([new(new PdfName("In"), PdfBoolean.True)])),
            new(new PdfName("Empty"), PdfDictionary.Empty),
            new(new PdfName("EA"), PdfArray.Empty),
        ]);
        string text = Text(dict);
        Assert.Equal("<< /Zed 1 /Alpha [1.5 (x) /N null 4 0 R] /Mid << /In true >> /Empty << >> /EA [] >>", text);
        Assert.True(PdfObjects.DeepEquals(dict, Parse(text)));
        Assert.Equal(text, Text(Parse(text))); // canonical: serialising twice is stable
    }

    [Fact]
    public void RandomTreesRoundTripAndAreStable()
    {
        var rng = new Random(20260428);
        for (int i = 0; i < 300; i++)
        {
            var obj = Random(rng, 0);
            string text = Text(obj);
            var back = Parse(text);
            Assert.True(PdfObjects.DeepEquals(obj, back), text);
            Assert.Equal(text, Text(back));
        }
    }

    private static PdfObject Random(Random rng, int depth)
    {
        int kind = rng.Next(depth > 4 ? 9 : 11);
        switch (kind)
        {
            case 0: return PdfNull.Instance;
            case 1: return PdfBoolean.From(rng.Next(2) == 0);
            case 2: return new PdfInteger(rng.Next(int.MinValue, int.MaxValue));
            case 3: return new PdfReal(Math.Round((rng.NextDouble() - 0.5) * 100000, 4));
            case 4:
            {
                var b = new byte[rng.Next(12)];
                rng.NextBytes(b);
                return new PdfString(b, rng.Next(2) == 0 ? PdfStringFormat.Hex : PdfStringFormat.Literal);
            }

            case 5:
            {
                var chars = new char[rng.Next(10)];
                for (int c = 0; c < chars.Length; c++)
                    chars[c] = (char)rng.Next(1, 256); // NUL has no legal name encoding
                return new PdfName(new string(chars));
            }

            case 6: return new PdfReference(rng.Next(0, 5000), rng.Next(0, 3));
            case 7: return new PdfInteger(rng.Next(-5, 5));
            case 8: return new PdfReal(rng.Next(-1000, 1000));
            case 9:
                return new PdfArray(Enumerable.Range(0, rng.Next(5)).Select(_ => Random(rng, depth + 1)));
            default:
            {
                var entries = new List<KeyValuePair<PdfName, PdfObject>>();
                int n = rng.Next(5);
                for (int k = 0; k < n; k++)
                    entries.Add(new(new PdfName("K" + k), Random(rng, depth + 1)));
                return new PdfDictionary(entries);
            }
        }
    }

    [Fact]
    public void DepthLimitAndStreamsAreErrors()
    {
        PdfObject deep = PdfNull.Instance;
        for (int i = 0; i < 100; i++)
            deep = new PdfArray([deep]);
        Assert.Equal(PdfLimit.NestingDepth, PdfSerializer.Serialize(deep).Error.Limit);
        Assert.True(PdfSerializer.Serialize(deep, maxDepth: 200).IsSuccess);

        PdfObject deepDict = PdfNull.Instance;
        for (int i = 0; i < 100; i++)
            deepDict = new PdfDictionary([new(new PdfName("A"), deepDict)]);
        Assert.Equal(PdfLimit.NestingDepth, PdfSerializer.Serialize(deepDict).Error.Limit);

        Assert.Equal(PdfErrorCode.InvalidUpdate, PdfSerializer.Serialize(new PdfStream(PdfDictionary.Empty, ReadOnlyMemory<byte>.Empty)).Error.Code);
    }

    [Fact]
    public void SinkEnforcesItsCeilingAndGrows()
    {
        var sink = new ByteSink(maxLength: 8);
        sink.WriteAscii("12345678");
        Assert.Equal(PdfLimit.OutputSize, Assert.Throws<PdfParseException>(() => sink.WriteByte(1)).Error.Limit);

        var big = new ByteSink();
        for (int i = 0; i < 10_000; i++)
            big.WriteByte((byte)i);
        big.Write(new byte[5000]);
        Assert.Equal(15_000, big.Length);
        Assert.Equal(15_000, big.ToArray().Length);
    }
}
