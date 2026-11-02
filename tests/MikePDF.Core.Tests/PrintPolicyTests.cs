using MikePDF.Core.Printing;
using MikePDF.Core.Reading;
using MikePDF.Core.Settings;

namespace MikePDF.Core.Tests;

/// <summary>print.md 3.7 and 7, settings-policies <c>PrintingEnabled</c> and criteria PRINT-09 and PRINT-24: who may print, at what quality, and what a hostile file can ask for.</summary>
public sealed class PrintPolicyTests
{
    private const uint PrintBit = 0x4;
    private const uint HighQualityBit = 0x800;

    private static PdfPermissions Encrypted(uint bits) => new(bits, 3);

    [Fact]
    public void BitThreeClearDisablesPrintWithTheOwnerReason()
    {
        var result = PrintEligibility.Evaluate(true, Encrypted(HighQualityBit));
        Assert.False(result.Allowed);
        Assert.Equal("Print_Disabled_Owner", result.DisabledReasonKey);
    }

    [Fact]
    public void BitThreeSetAndBitTwelveClearIsLowQuality()
    {
        var result = PrintEligibility.Evaluate(true, Encrypted(PrintBit));
        Assert.True(result.Allowed);
        Assert.True(result.LowQualityOnly);
        Assert.Null(result.DisabledReasonKey);
        // ... which caps the render resolution at 150 dpi.
        Assert.Equal(RenderLimits.LowQualityMaxDpi, RenderLimits.For(new PrintOptions(), result.LowQualityOnly).MaxDpi);
    }

    [Fact]
    public void BothBitsSetIsNormal()
    {
        var result = PrintEligibility.Evaluate(true, Encrypted(PrintBit | HighQualityBit));
        Assert.True(result.Allowed);
        Assert.False(result.LowQualityOnly);
        Assert.Equal(RenderLimits.DefaultMaxDpi, RenderLimits.For(new PrintOptions(), result.LowQualityOnly).MaxDpi);
    }

    [Fact]
    public void ADocumentWithNoProtectionIsNormal()
    {
        var result = PrintEligibility.Evaluate(true, PdfPermissions.Unrestricted);
        Assert.True(result.Allowed);
        Assert.False(result.LowQualityOnly);
    }

    [Fact]
    public void ARevisionTwoDocumentHasNoHighQualityBitSoItIsNeverLowQuality()
    {
        // Revision 2 defines print but not "print in high quality"; it is not the file's to deny (PdfPermissions.Defined).
        var result = PrintEligibility.Evaluate(true, new PdfPermissions(PrintBit, 2));
        Assert.True(result.Allowed);
        Assert.False(result.LowQualityOnly);
    }

    [Fact]
    public void PolicyTurningPrintingOffWinsOverEveryPermissionAndShowsItsOwnReason()
    {
        foreach (var permissions in new[] { PdfPermissions.Unrestricted, Encrypted(PrintBit | HighQualityBit), Encrypted(0) })
        {
            var result = PrintEligibility.Evaluate(printingAllowedByPolicy: false, permissions);
            Assert.False(result.Allowed);
            Assert.Equal("Print_Disabled_Policy", result.DisabledReasonKey);
        }
    }

    [Fact]
    public void ThePrintingEnabledPolicyTurnsPrintingOffThroughTheSnapshotTheCommandsUse()
    {
        var source = new FixedPolicySource();
        var provider = new PolicyProvider(source);
        Assert.True(PrintEligibility.Evaluate(provider.Current.PrintingAllowed, PdfPermissions.Unrestricted).Allowed);

        source.Set(PolicyScope.Machine, SettingsFixture.Dword("PrintingEnabled", 0));
        provider.Refresh();
        var off = PrintEligibility.Evaluate(provider.Current.PrintingAllowed, PdfPermissions.Unrestricted);
        Assert.False(off.Allowed);
        Assert.Equal(PrintStrings.DisabledPolicy, off.DisabledReasonKey);
    }

    // ---- the document's own wishes are defaults, never forced (and a hostile file cannot make more than 99 copies) ----

    [Fact]
    public void ADocumentThatSaysNoScalingStartsAtActualSizeAndOtherwiseShrink()
    {
        Assert.Equal(PrintScaling.Actual, PrintDefaults.FromViewerPreferences(1, 0, 0, [], [], 10).Scaling);
        Assert.Equal(PrintScaling.Shrink, PrintDefaults.FromViewerPreferences(2, 0, 0, [], [], 10).Scaling);
        Assert.Equal(PrintScaling.Shrink, PrintDefaults.FromViewerPreferences(0, 0, 0, [], [], 10).Scaling);
        Assert.Equal(PrintScaling.Shrink, PrintDefaults.FromViewerPreferences(77, 0, 0, [], [], 10).Scaling);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 2)]
    [InlineData(99, 99)]
    [InlineData(2147483647, 99)]
    [InlineData(-5, 1)]
    public void NumCopiesIsClampedToOneToNinetyNine(int fromFile, int expected) => Assert.Equal(expected, PrintDefaults.FromViewerPreferences(0, fromFile, 0, [], [], 10).Copies);

    [Theory]
    [InlineData(0, PrintDuplex.None)]
    [InlineData(1, PrintDuplex.None)]
    [InlineData(2, PrintDuplex.ShortEdge)]
    [InlineData(3, PrintDuplex.LongEdge)]
    [InlineData(9, PrintDuplex.None)]
    public void DuplexIsADefaultMappedFromThePdfNames(int fromFile, PrintDuplex expected) => Assert.Equal(expected, PrintDefaults.FromViewerPreferences(0, 0, fromFile, [], [], 10).Duplex);

    [Fact]
    public void APrintPageRangeBecomesTheRangeBoxText()
    {
        // The engine reply counts from 0 (document.cpp): pairs (0,1) and (4,4) are pages 1-2 and 5.
        var defaults = PrintDefaults.FromViewerPreferences(0, 0, 0, [0u, 4u], [1u, 4u], 10);
        Assert.Equal("1-2, 5", defaults.RangeText);
        Assert.True(PageRangeParser.Parse(defaults.RangeText, 10).IsValid);
    }

    [Theory]
    [InlineData(new[] { 0u, 4u }, new[] { 1u }, 10)] // different lengths
    [InlineData(new[] { 5u }, new[] { 2u }, 10)] // backwards
    [InlineData(new[] { 0u }, new[] { 10u }, 10)] // past the end
    [InlineData(new[] { 4u, 0u }, new[] { 5u, 1u }, 10)] // not in order
    [InlineData(new[] { 0u }, new[] { 4294967295u }, 10)]
    public void AJunkPrintPageRangeIsIgnored(uint[] firsts, uint[] lasts, int pageCount) => Assert.Null(PrintDefaults.FromViewerPreferences(0, 0, 0, firsts, lasts, pageCount).RangeText);

    [Fact]
    public void OptionsFromAStaleOrHostileSettingsFileAreClamped()
    {
        var options = new PrintOptions
        {
            Copies = int.MaxValue,
            CustomPercent = 1e9,
            PagesPerSheet = 7,
            PaperSizeId = -4,
            Scaling = (PrintScaling)99,
            Orientation = (PaperOrientation)(-1),
            Duplex = (PrintDuplex)12,
            SheetOrder = (SheetOrder)5,
        }.Normalize();
        Assert.Equal(99, options.Copies);
        Assert.Equal(400, options.CustomPercent);
        Assert.Equal(1, options.PagesPerSheet);
        Assert.Equal(0, options.PaperSizeId);
        Assert.Equal(PrintScaling.Shrink, options.Scaling);
        Assert.Equal(PaperOrientation.Auto, options.Orientation);
        Assert.Equal(PrintDuplex.None, options.Duplex);
        Assert.Equal(SheetOrder.Horizontal, options.SheetOrder);
        Assert.Equal(100, new PrintOptions { CustomPercent = double.NaN }.Normalize().CustomPercent);
        Assert.Equal(10, new PrintOptions { CustomPercent = 0 }.Normalize().CustomPercent);
        Assert.All(PrintOptions.PagesPerSheetChoices, n => Assert.Equal(n, new PrintOptions { PagesPerSheet = n }.Normalize().PagesPerSheet));
    }

    // ---- the options become the engine's flags ----

    [Fact]
    public void TheBandFlagsFollowTheOptionsAndPrintingModeIsAlwaysOn()
    {
        Assert.Equal(7u, new PrintOptions().BandFlags()); // annotations, form fields, printing mode
        Assert.Equal(4u, new PrintOptions { Annotations = false, FormFields = false }.BandFlags());
        Assert.Equal(5u, new PrintOptions { Annotations = true, FormFields = false }.BandFlags());
        Assert.Equal(6u, new PrintOptions { Annotations = false, FormFields = true }.BandFlags());
        Assert.Equal(5u, new PrintOptions().BandFlags(includeFormLayer: false)); // the engine refuses bit 1 until it has forms: the job asks again without it
        Assert.True(new PrintOptions().BandFlags() <= 7); // bit 3 is reserved and refused by the schema
    }

    // ---- grey scale ----

    [Fact]
    public void GrayscaleUsesTheLuminanceWeightsAndKeepsWhiteAndBlack()
    {
        byte[] pixels =
        [
            0, 0, 0, 255, // black
            255, 255, 255, 255, // white
            0, 0, 255, 255, // red (BGRA)
            0, 255, 0, 255, // green
            255, 0, 0, 255, // blue
        ];
        var grey = new byte[pixels.Length];
        Grayscale.Convert(pixels, grey, 5, 1, 20);
        Assert.Equal([0, 0, 0, 255], grey[0..4]);
        Assert.Equal([255, 255, 255, 255], grey[4..8]);
        Assert.Equal(77, grey[10]); // 0.299 * 255 = 76.2, in 1/256ths
        Assert.Equal(149, grey[13]); // 150/256 * 255 = 149.4, plus the rounding half, floored
        Assert.Equal(29, grey[16]); // 0.114 * 255
        for (var i = 0; i < 5; i++)
        {
            Assert.True(grey[i * 4] == grey[(i * 4) + 1] && grey[i * 4] == grey[(i * 4) + 2]);
        }
    }

    [Fact]
    public void GrayscaleWorksInPlaceAndOnlyTouchesTheWidthOfEachRow()
    {
        var pixels = new byte[(2 * 12) + 8]; // two rows of 2 pixels with a 4 byte padding each
        Array.Fill(pixels, (byte)200);
        for (var row = 0; row < 2; row++)
        {
            Array.Fill(pixels, (byte)7, (row * 12) + 8, 4);
        }

        Grayscale.Convert(pixels, pixels, 2, 2, 12);
        Assert.Equal(7, pixels[8]); // the padding is not written
        Assert.Equal(7, pixels[20]);
        Assert.Equal(200, pixels[0]); // grey 200 stays 200: 200 * 256 / 256
        Assert.Equal(255, pixels[3]);
        Assert.Throws<ArgumentException>(() => Grayscale.Convert(new byte[7], new byte[7], 2, 1, 8));
    }
}
