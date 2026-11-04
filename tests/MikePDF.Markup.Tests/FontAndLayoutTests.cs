#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using System.Security.Cryptography;
using System.Text;
using MikePDF.Core.Geometry;
using MikePDF.Markup.Fonts;
using MikePDF.Markup.Tests.Support;

namespace MikePDF.Markup.Tests;

/// <summary>FT-01 to FT-04, FT-17 and FT-18: the font metrics, the WinAnsi filter, the one text layout, and box growth.</summary>
public sealed class FontAndLayoutTests
{
    private const int Helvetica = 0;
    private const int HelveticaBold = 1;
    private const int TimesRoman = 4;
    private const int Courier = 8;

    // Adobe's published Helvetica widths for the printable ASCII characters 32 to 126, from the AFM file, written out here independently of the generated table.
    private static readonly int[] HelveticaAscii =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
    ];

    [Fact]
    public void FT01_HelveticaAsciiWidthsEqualAdobesPublishedValues()
    {
        for (var code = 32; code <= 126; code++)
        {
            Assert.Equal(HelveticaAscii[code - 32], StandardFonts.Width(Helvetica, (byte)code));
        }
    }

    [Fact]
    public void FT01_TheWidthsOfAllTwelveFontsAreTheCommittedTable()
    {
        // A checksum of every width of every font for codes 32 to 255: a change to the table, however small, fails here and has to be looked at.
        var bytes = new List<byte>();
        for (var font = 0; font < StandardFonts.Count; font++)
        {
            for (var code = 32; code <= 255; code++)
            {
                var width = StandardFonts.Width(font, (byte)code);
                bytes.Add((byte)(width & 0xFF));
                bytes.Add((byte)(width >> 8));
            }
        }

        var actual = Convert.ToHexString(SHA256.HashData([.. bytes]));
        Assert.Equal("8B8FA6274825958B081A43C27138668DBBCB5677662AEC8B2C72E8016B0BBC33", actual);
    }

    [Fact]
    public void FT01_TheTableCoversEveryWinAnsiCodeAndTheFamiliesAreWhatTheyShouldBe()
    {
        for (var font = 0; font < StandardFonts.Count; font++)
        {
            for (var code = 32; code <= 255; code++)
            {
                Assert.True(StandardFonts.Width(font, (byte)code) > 0, $"font {font}, code {code}");
            }
        }

        // Courier is monospaced, Helvetica's oblique has its upright's widths, Times differs from Helvetica.
        Assert.All(Enumerable.Range(32, 224), code => Assert.Equal(600, StandardFonts.Width(Courier, (byte)code)));
        Assert.All(Enumerable.Range(32, 224), code => Assert.Equal(StandardFonts.Width(Helvetica, (byte)code), StandardFonts.Width(2, (byte)code)));
        Assert.Equal(722, StandardFonts.Width(TimesRoman, (byte)'A'));
        Assert.Equal(444, StandardFonts.Width(TimesRoman, (byte)'a'));
        Assert.Equal(250, StandardFonts.Width(TimesRoman, (byte)' '));
        Assert.Equal(722, StandardFonts.Width(HelveticaBold, (byte)'A'));
        Assert.Equal(556, StandardFonts.Width(HelveticaBold, (byte)'a'));
        Assert.Equal(500, StandardFonts.Width(TimesRoman, 0x80)); // the euro sign
        Assert.Equal(556, StandardFonts.Width(Helvetica, 0x80));
        Assert.Equal(600, StandardFonts.Width(Courier, 0x80));
    }

    [Fact]
    public void FT01_FamilyAndStyleMapToTheTwelveFonts()
    {
        var names = new List<string>();
        foreach (var family in Enum.GetValues<FontFamily>())
        {
            foreach (var (bold, italic) in new[] { (false, false), (true, false), (false, true), (true, true) })
            {
                var id = StandardFonts.FontId(family, bold, italic);
                names.Add(StandardFonts.BaseFont(id));
                Assert.Equal((family, bold, italic), (StandardFonts.FamilyOf(id), StandardFonts.IsBold(id), StandardFonts.IsItalic(id)));
            }
        }

        Assert.Equal(["Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique", "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic",
            "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique"], names);
        Assert.Equal(12, Enumerable.Range(0, 12).Select(StandardFonts.ResourceName).Distinct().Count());
        Assert.Equal(718, StandardFonts.Ascender(Helvetica));
    }

    // ---- FT-03: the character set ----

    [Fact]
    public void FT03_ExactlyTheWinAnsiCodePointsAreSupported()
    {
        var supported = Enumerable.Range(0, 0x3000).Where(c => !char.IsSurrogate((char)c) && WinAnsi.TryGetCode(new Rune(c), out _)).ToList();

        Assert.Equal(95 + 96 + 27, supported.Count); // 0x20-0x7E, 0xA0-0xFF, and the 27 defined codes of 0x80-0x9F
        Assert.True(WinAnsi.TryGetCode(new Rune('€'), out var euro) && euro == 0x80);
        Assert.True(WinAnsi.TryGetCode(new Rune('’'), out var quote) && quote == 0x92);
        Assert.True(WinAnsi.TryGetCode(new Rune('é'), out var e) && e == 0xE9);
        Assert.False(WinAnsi.TryGetCode(new Rune(0x7F), out _));
        Assert.False(WinAnsi.TryGetCode(new Rune('Ж'), out _)); // Cyrillic
        Assert.False(WinAnsi.TryGetCode(new Rune(0x1F600), out _)); // emoji
    }

    [Theory]
    [InlineData("plain text", "plain text", 0)]
    [InlineData("café – naïve €", "café – naïve €", 0)]
    [InlineData("é", "é", 0)] // typed as two code points, kept as one
    [InlineData("Привет", "", 6)]
    [InlineData("日本語 text", " text", 3)]
    [InlineData("smile \U0001F600!", "smile !", 1)] // a surrogate pair is one character
    [InlineData("a\tb\nc\r\nd", "a\tb\nc\r\nd", 0)]
    [InlineData("bell\u0007", "bell", 1)]
    public void FT03_UnsupportedCharactersAreRemovedAndCounted(string typed, string kept, int removed)
    {
        var (text, count) = WinAnsi.Filter(typed);

        Assert.Equal((kept, removed), (text, count));
    }

    [Fact]
    public void FT03_AStringIsWrittenAsAnAsciiLiteralWithEscapes()
    {
        Assert.Equal("(Hello)", WinAnsi.ToLiteral("Hello"));
        Assert.Equal("(a\\(b\\)c\\\\d)", WinAnsi.ToLiteral("a(b)c\\d"));
        Assert.Equal("(caf\\351 \\200)", WinAnsi.ToLiteral("café €"));
        Assert.Equal("(\\225)", WinAnsi.ToLiteral("Ж")); // an unsupported character is the bullet the encoding gives its unused codes
        Assert.All(WinAnsi.ToLiteral("résumé – “quoted”"), static c => Assert.True(c is >= ' ' and <= '~'));
    }

    // ---- FT-02: the layout ----

    private static string[] Lines(string text, int font, double size, double width, TextAlign align = TextAlign.Left) =>
        [.. TextLayout.Layout(text, font, size, width, align).Lines.Select(static l => l.Text)];

    // Courier at 10 points is 6 points a character, so a width of 60 holds ten characters and a line is easy to count.
    [Theory]
    [InlineData("hello", 60, "hello")]
    [InlineData("hello world", 60, "hello|world")]
    [InlineData("0123456789", 60, "0123456789")]
    [InlineData("01234567890", 60, "0123456789|0")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", 60, "abcdefghij|klmnopqrst|uvwxyz")]
    [InlineData("aa bb cc dd ee", 60, "aa bb cc|dd ee")]
    [InlineData("aa bb cc dd", 66, "aa bb cc dd")] // exactly 11 characters fit in 66
    [InlineData("a\nb", 60, "a|b")]
    [InlineData("a\r\nb", 60, "a|b")]
    [InlineData("a\rb", 60, "a|b")]
    [InlineData("a\n\nb", 60, "a||b")]
    [InlineData("a\n", 60, "a|")]
    [InlineData("", 60, "")]
    [InlineData("a\tb", 60, "a    b")]
    [InlineData("a\tb\tc", 60, "a    b|c")] // 6 + 4 spaces would be 11 characters
    [InlineData("  indented", 120, "  indented")]
    [InlineData("hello     world", 60, "hello|world")] // the spaces at the wrap go
    [InlineData("one two three four five", 90, "one two three|four five")]
    [InlineData("aaaaaaaaaaaaaaaa bb", 60, "aaaaaaaaaa|aaaaaa bb")] // a long word breaks between characters, then the line goes on
    [InlineData("ab", 0, "a|b")]
    [InlineData("abc", 6, "a|b|c")]
    [InlineData("ab cd", 12, "ab|cd")]
    [InlineData("ab cd", 30, "ab cd")]
    [InlineData("x", -5, "x")]
    public void FT02_TheLayoutBreaksAtSpacesWithTheFontsWidths(string text, double width, string expected)
    {
        Assert.Equal(expected.Split('|'), Lines(text, Courier, 10, width));
    }

    [Theory]
    [InlineData(TextAlign.Left, 0)]
    [InlineData(TextAlign.Center, 24)]
    [InlineData(TextAlign.Right, 48)]
    public void FT02_AlignmentPlacesALineInsideTheInnerWidth(TextAlign align, double x)
    {
        var line = TextLayout.Layout("ab", Courier, 10, 60, align).Lines.Single();

        Assert.Equal(x, line.X, 9);
        Assert.Equal(12, line.Width, 9);
    }

    [Fact]
    public void FT02_TheFirstBaselineSitsAtTheAscenderAndLinesAreOnePointTwoEmApart()
    {
        var layout = TextLayout.Layout("a\nb\nc", Courier, 10, 100, TextAlign.Left);

        Assert.Equal([6.29, 18.29, 30.29], layout.Lines.Select(static l => Math.Round(l.Baseline, 6)));
        Assert.Equal(36, layout.Height, 9);
        var big = TextLayout.Layout("a", Helvetica, 144, 1000, TextAlign.Left);
        Assert.Equal(718 * 144 / 1000.0, big.Lines[0].Baseline, 9);
        Assert.Equal(172.8, big.Height, 9);
        Assert.Equal(3.6, TextLayout.Layout("a", Courier, 6, 100, TextAlign.Left).Lines[0].Width, 9);
    }

    [Fact]
    public void FT02_BoldAndItalicFontsUseTheirOwnWidths()
    {
        // "iii" is 222 units each in Helvetica; the bold face is 278.
        Assert.Equal(66.6, TextLayout.Layout("iii", Helvetica, 100, 1000, TextAlign.Left).Lines[0].Width, 6);
        Assert.Equal(83.4, TextLayout.Layout("iii", HelveticaBold, 100, 1000, TextAlign.Left).Lines[0].Width, 6);
        Assert.Equal(66.6, TextLayout.Layout("iii", 2, 100, 1000, TextAlign.Left).Lines[0].Width, 6); // oblique = upright
        Assert.Equal(StandardFonts.Measure("Hello", TimesRoman, 12), TextLayout.Layout("Hello", TimesRoman, 12, 1000, TextAlign.Left).Lines[0].Width, 9);
    }

    [Fact]
    public void FT02_ForAnyTextNoLineIsWiderThanTheBoxUnlessItIsOneCharacterAndNothingIsLost()
    {
        var random = new Random(11);
        var alphabet = "abc def  ghij klmnopqrstuvwxyz \n";
        for (var round = 0; round < 300; round++)
        {
            var text = new string([.. Enumerable.Range(0, random.Next(0, 120)).Select(_ => alphabet[random.Next(alphabet.Length)])]);
            var font = random.Next(0, 12);
            var size = 6 + random.Next(0, 40);
            var width = random.Next(0, 300);

            var layout = TextLayout.Layout(text, font, size, width, TextAlign.Left);

            foreach (var line in layout.Lines)
            {
                Assert.True(line.Width <= width + 1e-6 || line.Text.Length <= 1, $"'{line.Text}' is {line.Width} wide in {width}");
                Assert.DoesNotContain('\n', line.Text);
            }

            var kept = string.Concat(layout.Lines.Select(static l => l.Text)).Replace(" ", string.Empty, StringComparison.Ordinal);
            Assert.Equal(text.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal), kept);
            Assert.True(layout.Lines.Count <= TextLayout.MaxLines);
        }
    }

    // ---- FT-04, FT-17, FT-18: boxes ----

    [Fact]
    public void FT04_ABoxGrowsToItsTextAndNeverShrinks()
    {
        var samples = new Samples();
        var small = samples.Factory.FreeText(0, new PdfRect(100, 500, 200, 520), "line one\nline two\nline three\nline four", size: 12);
        var roomy = samples.Factory.FreeText(0, new PdfRect(100, 400, 300, 600), "short", size: 12);

        var layout = TextLayout.Layout(small.Contents, 0, 12, TextLayout.InnerWidth(100, small.Width), TextAlign.Left);
        Assert.True(small.Rect.Height >= layout.Height + (2 * TextLayout.Padding));
        Assert.Equal(520, small.Rect.Top); // it grows down
        Assert.Equal(new PdfRect(100, 400, 300, 600), roomy.Rect); // a box with room is left as the user made it
        Assert.True(MarkupFactory.GrowToFit(roomy with { Contents = "now a much longer text that has to wrap onto several lines of the box " + new string('x', 40) }).Rect.Height >= roomy.Rect.Height);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void FT04_ABoxGrowsDownAsItIsDisplayedWhateverThePageRotation(int rotation)
    {
        var samples = new Samples();
        var frame = Samples.Frame(rotation);
        var shown = new RectD(100, 100, 100, 20);
        var box = samples.Factory.FreeTextOnPage(0, frame, shown, "aaa bbb ccc ddd eee fff ggg hhh iii jjj", size: 12);

        var after = frame.ToDisplay(box.Rect);

        Assert.Equal(100, after.X, 6); // the top-left corner stays where it was displayed
        Assert.Equal(100, after.Y, 6);
        Assert.Equal(100, after.Width, 6);
        Assert.True(after.Height > 20);
    }

    [Fact]
    public void FT04_ATypewriterIsAsWideAsItsWidestLineUpToThePageEdge()
    {
        var samples = new Samples();
        var box = samples.Factory.FreeText(0, new PdfRect(100, 500, 120, 512), "short\na longer line of typed text", FontFamily.Mono, size: 10, typewriter: true);

        var natural = FreeTextRules.TypewriterWidth(box, 1000);
        var cramped = FreeTextRules.TypewriterWidth(box, 80);

        Assert.Equal((27 * 6) + 4, natural, 9); // the second line is 27 characters of 6 points, plus the padding
        Assert.Equal(80, cramped, 9); // then it wraps: the width is what the page leaves
    }

    [Fact]
    public void FT17_LowContrastIsBelowThreeToOneAndNotAbove()
    {
        Assert.True(FreeTextRules.IsLowContrast(new Rgb(1, 1, 0.9), null)); // pale on white paper
        Assert.True(FreeTextRules.IsLowContrast(Rgb.Black, new Rgb(0.05, 0.05, 0.05)));
        Assert.False(FreeTextRules.IsLowContrast(Rgb.Black, null));
        Assert.False(FreeTextRules.IsLowContrast(Rgb.Black, new Rgb(1, 1, 0.8)));
        // The boundary: grey 0.46 on white is just over 3:1, 0.5 just under.
        Assert.False(FreeTextRules.IsLowContrast(new Rgb(0.45, 0.45, 0.45), null));
        Assert.True(FreeTextRules.IsLowContrast(new Rgb(0.6, 0.6, 0.6), null));
        Assert.Equal(21, Rgb.Contrast(Rgb.Black, Rgb.White), 9);
    }

    [Fact]
    public void FT18_TextPastThePageBottomIsReportedAndNotClipped()
    {
        var samples = new Samples();
        var page = new PdfRect(0, 0, 612, 792);
        var inside = samples.Factory.FreeText(0, new PdfRect(100, 100, 300, 200), "fine");
        var below = samples.Factory.FreeText(0, new PdfRect(100, -5, 300, 40), "hangs below");

        Assert.False(FreeTextRules.IsOutsidePage(inside, page));
        Assert.True(FreeTextRules.IsOutsidePage(below, page));
        Assert.Equal(-5, below.Rect.Bottom); // saved as it is
        Assert.True(FreeTextRules.WithinLimits(new string('x', 10_000)));
        Assert.False(FreeTextRules.WithinLimits(new string('x', 10_001)));
        Assert.False(FreeTextRules.WithinLimits(string.Join('\n', Enumerable.Repeat("x", 201))));
    }
}
