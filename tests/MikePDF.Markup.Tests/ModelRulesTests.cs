#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using MikePDF.Markup.Tests.Support;

namespace MikePDF.Markup.Tests;

/// <summary>AC-01, AC-02, AC-09, SN-02, SN-03, SN-06 and the field round trip of the journal (annotation-core 9).</summary>
public sealed class ModelRulesTests
{
    [Fact]
    public void AC01_OwnedIdsAreThePrefixAndThirtyTwoHexDigitsAndUniqueAcrossAHundredThousand()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 100_000; i++)
        {
            var id = AnnotationIds.New();
            Assert.True(AnnotationIds.IsWellFormedOwned(id), id);
            Assert.True(seen.Add(id));
        }

        Assert.StartsWith("MikePDF:", AnnotationIds.New(), StringComparison.Ordinal);
        Assert.False(AnnotationIds.IsWellFormedOwned("MikePDF:ABC"));
        Assert.False(AnnotationIds.IsWellFormedOwned("MikePDF:" + new string('G', 32)));
        Assert.False(AnnotationIds.IsWellFormedOwned("other:" + new string('a', 34)));
    }

    [Fact]
    public void AC01_LoadedAnnotationsKeepTheirNameOrGetASessionIdThatCollidesWithNothing()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var named = AnnotationIds.ForLoaded("abc", 3, 0, seen);
        var unnamed = AnnotationIds.ForLoaded(null, 3, 1, seen);
        var empty = AnnotationIds.ForLoaded(string.Empty, 3, 2, seen);
        var duplicate = AnnotationIds.ForLoaded("abc", 3, 3, seen); // the later one of two with the same /NM
        var again = AnnotationIds.ForLoaded(null, 3, 1, seen); // the same position asked for twice in one session

        Assert.Equal("abc", named);
        Assert.Equal(5, new[] { named, unnamed, empty, duplicate, again }.Distinct().Count());
        Assert.All(new[] { unnamed, empty, duplicate, again }, static id => Assert.False(AnnotationIds.IsOwned(id)));
        Assert.StartsWith("session:", unnamed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Mike", "Policy", "Display Name", "mjtpe", "Mike")]
    [InlineData(null, "Policy", "Display Name", "mjtpe", "Policy")]
    [InlineData("  ", "", "Display Name", "mjtpe", "Display Name")]
    [InlineData("", null, null, "mjtpe", "mjtpe")]
    [InlineData(null, null, null, null, "")]
    public void AC09_TheAuthorIsTheSettingThenThePolicyThenTheWindowsNameThenTheUserName(string? setting, string? policy, string? windows, string? user, string expected)
    {
        Assert.Equal(expected, AuthorResolver.Resolve(setting, policy, windows, user));
    }

    [Theory]
    [InlineData(2026, 11, 4, 9, 30, 15, 1, 0, "D:20261104093015+01'00'")]
    [InlineData(2026, 1, 2, 3, 4, 5, 0, 0, "D:20260102030405+00'00'")]
    [InlineData(2026, 11, 4, 9, 30, 15, -5, 0, "D:20261104093015-05'00'")]
    [InlineData(2026, 11, 4, 9, 30, 15, 5, 30, "D:20261104093015+05'30'")]
    [InlineData(2026, 11, 4, 9, 30, 15, -9, -30, "D:20261104093015-09'30'")]
    [InlineData(2026, 11, 4, 23, 59, 59, 5, 45, "D:20261104235959+05'45'")]
    public void AC09_DatesAreWrittenInLocalTimeWithTheirOffsetIncludingNegativeAndHalfHourZones(int y, int mo, int d, int h, int mi, int s, int offsetHours, int offsetMinutes, string expected)
    {
        var value = new DateTimeOffset(y, mo, d, h, mi, s, new TimeSpan(offsetHours, offsetMinutes, 0));

        var text = PdfDates.Format(value);

        Assert.Equal(expected, text);
        Assert.True(PdfDates.TryParse(text, out var back));
        Assert.Equal(value, back);
        Assert.Equal(value.Offset, back.Offset);
    }

    [Theory]
    [InlineData("D:20261104093015Z", 2026, 11, 4, 9, 30, 15, 0)]
    [InlineData("D:20261104093015Z00'00'", 2026, 11, 4, 9, 30, 15, 0)]
    [InlineData("D:20261104093015+0100", 2026, 11, 4, 9, 30, 15, 60)]
    [InlineData("D:20261104093015-05'30", 2026, 11, 4, 9, 30, 15, -330)]
    [InlineData("D:20261104093015", 2026, 11, 4, 9, 30, 15, 0)]
    [InlineData("D:202611040930", 2026, 11, 4, 9, 30, 0, 0)]
    [InlineData("D:2026", 2026, 1, 1, 0, 0, 0, 0)]
    [InlineData("20261104093015", 2026, 11, 4, 9, 30, 15, 0)]
    public void AC09_DatesFromOtherReadersAreParsedLeniently(string text, int y, int mo, int d, int h, int mi, int s, int offsetMinutes)
    {
        Assert.True(PdfDates.TryParse(text, out var value));
        Assert.Equal(new DateTimeOffset(y, mo, d, h, mi, s, TimeSpan.FromMinutes(offsetMinutes)), value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("D:20261304093015")]
    [InlineData("D:20260230093015")]
    [InlineData("D:202")]
    [InlineData("D:20261104253015")]
    [InlineData("D:20261104093015+99'00'")]
    public void AC09_ADateThatIsNotOneIsRefused(string text)
    {
        Assert.False(PdfDates.TryParse(text, out _));
    }

    [Fact]
    public void AC09_FlagsAreTheKindsDefaultAndNoteIconsKeepTheirSizeAndOrientation()
    {
        Assert.Equal(4u, MarkupDefaults.DefaultFlags(MarkupKind.Highlight));
        Assert.Equal(4u, MarkupDefaults.DefaultFlags(MarkupKind.Stamp));
        Assert.Equal(28u, MarkupDefaults.DefaultFlags(MarkupKind.Note)); // Print, NoZoom, NoRotate
        var samples = new Samples();
        Assert.Equal(28u, MarkupDefaults.EffectiveFlags(samples.Note()));
        Assert.Equal(4u, MarkupDefaults.EffectiveFlags(samples.Rectangle()));
        Assert.Equal(6u, MarkupDefaults.EffectiveFlags(samples.Rectangle() with { Flags = 6 })); // a foreign flag is kept as it is
        // Hidden (2), NoView (32) and Locked (128) are never set by default.
        foreach (var kind in Enum.GetValues<MarkupKind>())
        {
            Assert.Equal(0u, MarkupDefaults.DefaultFlags(kind) & (2 | 32 | 128));
        }
    }

    [Fact]
    public void AC09_ASubjectNamesTheTool()
    {
        var samples = new Samples();
        Assert.Equal("Highlight", samples.Highlight().Subject);
        Assert.Equal("Sticky Note", samples.Note().Subject);
        Assert.Equal("Text Box", samples.FreeText().Subject);
        Assert.Equal("Typewriter", samples.Typewriter().Subject);
        Assert.Equal("Drawing", samples.Ink().Subject);
        Assert.Equal("Approved", samples.Stamp().Subject);
        Assert.All(Enum.GetValues<MarkupKind>(), static kind => Assert.NotEmpty(MarkupDefaults.SubjectFor(kind)));
    }

    [Theory]
    [InlineData("é", "é")]
    [InlineData("a\u0000b", "ab")]
    [InlineData("a\u0007b\u007Fc", "abc")]
    [InlineData("tab\there\nnew\r\nline", "tab\there\nnew\r\nline")]
    [InlineData("emoji \U0001F600 and ‮ bidi", "emoji \U0001F600 and ‮ bidi")]
    [InlineData("", "")]
    public void AC09_CommentsAreNfcWithoutControlCharactersExceptTabsAndLineBreaks(string typed, string expected)
    {
        Assert.Equal(expected, CommentText.Normalise(typed));
    }

    [Fact]
    public void SN06_ALimitCountsCodePointsAndATruncationNeverSplitsOne()
    {
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 10_001)); // 20,002 UTF-16 units, 10,001 code points

        Assert.Equal(10_001, CommentText.CodePoints(emoji));
        var cut = CommentText.Truncate(emoji);
        Assert.Equal(10_000, CommentText.CodePoints(cut));
        Assert.Equal(20_000, cut.Length);
        Assert.Equal("ab…", CommentText.Excerpt("abcdef", 2));
        Assert.Equal("abc", CommentText.Excerpt("abc", 80));
        Assert.Equal(1, CommentText.CodePoints("é".Normalize(System.Text.NormalizationForm.FormC)));
    }
}
