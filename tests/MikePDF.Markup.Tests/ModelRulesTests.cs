#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using System.Text.Json;
using MikePDF.Core.Editing;
using MikePDF.Core.Geometry;
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

    // ---- AC-02 ----

    [Theory]
    [InlineData("Highlight", "MikePDF:abc", AnnotationClass.Owned)]
    [InlineData("Ink", "MikePDF:abc", AnnotationClass.Owned)]
    [InlineData("Highlight", "AcrobatNM", AnnotationClass.ForeignSupported)]
    [InlineData("Text", null, AnnotationClass.ForeignSupported)]
    [InlineData("Stamp", "", AnnotationClass.ForeignSupported)]
    [InlineData("Link", "MikePDF:abc", AnnotationClass.Unsupported)] // a hostile claim on something that is not ours to draw
    [InlineData("Widget", null, AnnotationClass.Unsupported)]
    [InlineData("Popup", null, AnnotationClass.Unsupported)]
    [InlineData("FileAttachment", null, AnnotationClass.Unsupported)]
    [InlineData("Redact", null, AnnotationClass.Unsupported)]
    [InlineData(null, null, AnnotationClass.Unsupported)]
    public void AC02_AnAnnotationIsOwnedForeignOrUnsupported(string? subtype, string? nm, AnnotationClass expected)
    {
        Assert.Equal(expected, AnnotationRules.Classify(subtype, nm));
    }

    [Fact]
    public void AC02_TheAllowedOperationsOfEachClassAreEnforcedWithTheRightReason()
    {
        var samples = new Samples();
        var owned = samples.Rectangle();
        var foreign = samples.Rectangle() with { Id = "someone-else", Author = "Alex" };
        var foreignMarkup = samples.Highlight() with { Id = "someone-else-2", Author = "Alex" };
        var unsupported = samples.Rectangle() with { Id = "x" }; // stands in for a loaded kind; the matrix is keyed on the class below

        foreach (var operation in Enum.GetValues<AnnotationOperation>())
        {
            Assert.True(AnnotationRules.Check(owned, operation, "Mike").Allowed || operation == AnnotationOperation.Move && owned.IsTextAnchored, operation.ToString());
        }

        Assert.True(AnnotationRules.Check(foreign, AnnotationOperation.Select, "Mike").Allowed);
        Assert.True(AnnotationRules.Check(foreign, AnnotationOperation.Delete, "Mike").Allowed);
        Assert.True(AnnotationRules.Check(foreign, AnnotationOperation.Reply, "Mike").Allowed);
        Assert.True(AnnotationRules.Check(foreign, AnnotationOperation.SetReviewState, "Mike").Allowed);
        Assert.True(AnnotationRules.Check(foreign, AnnotationOperation.Move, "Mike").Allowed);
        Assert.Equal(new OperationCheck(false, "Annot_Disabled_Foreign"), AnnotationRules.Check(foreign, AnnotationOperation.Restyle, "Mike"));
        Assert.Equal(new OperationCheck(false, "Annot_Disabled_Foreign"), AnnotationRules.Check(foreign, AnnotationOperation.Resize, "Mike"));
        Assert.Equal(new OperationCheck(false, "Note_Cannot_Edit_Others"), AnnotationRules.Check(foreign, AnnotationOperation.EditContents, "Mike"));
        Assert.True(AnnotationRules.Check(foreign, AnnotationOperation.EditContents, "Alex").Allowed); // the same author may edit the comment
        Assert.False(AnnotationRules.Check(foreign, AnnotationOperation.EditContents, "").Allowed);
        Assert.Equal("Annot_Disabled_TextAnchored", AnnotationRules.Check(foreignMarkup, AnnotationOperation.Move, "Alex").ReasonKey); // text markup is anchored
        Assert.Equal("Annot_Disabled_TextAnchored", AnnotationRules.Check(samples.Highlight(), AnnotationOperation.Move, "Mike").ReasonKey);
        Assert.Equal("Annot_Disabled_NotResizable", AnnotationRules.Check(samples.Note(), AnnotationOperation.Resize, "Mike").ReasonKey);
        Assert.NotNull(unsupported);
    }

    [Fact]
    public void AC02_AnUnsupportedKindCanOnlyBeSelected()
    {
        // Only the class decides: build the check by classifying a link-like annotation.
        Assert.Equal(AnnotationClass.Unsupported, AnnotationRules.Classify("Link", null));
        Assert.Null(AnnotationRules.DeleteConfirmKey(0));
        Assert.Equal("Note_Delete_Thread_Confirm_One", AnnotationRules.DeleteConfirmKey(1));
        Assert.Equal("Note_Delete_Thread_Confirm_Other", AnnotationRules.DeleteConfirmKey(7));
    }

    // ---- AC-09 ----

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
        Assert.True(MikePDF.Protocol.Edits.EditBlobCodec.IsPdfDate(text)); // the blob accepts what the model writes
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

    // ---- SN-02 and SN-03 ----

    [Fact]
    public void SN02_TheCurrentStateIsTheNewestStateAnnotationAndNoneResets()
    {
        var samples = new Samples();
        var note = samples.Session.Add(samples.Note());
        Assert.Equal(ReviewState.None, samples.Session.CurrentState(note.Id));

        samples.Time.Advance(TimeSpan.FromMinutes(1));
        samples.Session.SetReviewState(note.Id, ReviewState.Accepted);
        Assert.Equal(ReviewState.Accepted, samples.Session.CurrentState(note.Id));
        samples.Time.Advance(TimeSpan.FromMinutes(1));
        samples.Session.SetReviewState(note.Id, ReviewState.Rejected);
        Assert.Equal(ReviewState.Rejected, samples.Session.CurrentState(note.Id));
        samples.Time.Advance(TimeSpan.FromMinutes(1));
        samples.Session.SetReviewState(note.Id, ReviewState.None);
        Assert.Equal(ReviewState.None, samples.Session.CurrentState(note.Id)); // None is a state too
        Assert.Equal(3, samples.Session.Store.RepliesTo(note.Id).Count(static a => a.ReplyKind == ReplyKind.State)); // history is kept
    }

    [Fact]
    public void SN02_TwoStatesAtTheSameInstantBreakTiesByOrderInThePage()
    {
        var samples = new Samples();
        var note = samples.Session.Add(samples.Note());
        samples.Session.SetReviewState(note.Id, ReviewState.Completed);
        samples.Session.SetReviewState(note.Id, ReviewState.Cancelled); // same clock reading, later in /Annots

        Assert.Equal(ReviewState.Cancelled, samples.Session.CurrentState(note.Id));
    }

    [Fact]
    public void SN03_RepliesAreRefusedToAnnotationsWithNoNameAndTheThreadLimitIsKept()
    {
        var samples = new Samples();
        var loaded = samples.Note() with { Id = "session:0:3", Origin = AnnotationOrigin.Loaded };
        samples.Session.Load([loaded]);

        Assert.Null(samples.Session.Reply(loaded.Id, "hello"));
        Assert.Equal(AnnotationRules.ReasonUnsupported, samples.Session.LastReason);
        var note = samples.Session.Add(samples.Note(contents: "root"));
        Assert.NotNull(samples.Session.Reply(note.Id, "fine"));
        Assert.Null(samples.Session.Reply("no such id", "x"));
        Assert.Equal(["root"], new[] { note.Contents });
    }

    // ---- the fields of a command: the journal's view of an annotation ----

    [Fact]
    public void EveryKindSurvivesTheFlatFieldsOfACommandUnchanged()
    {
        var samples = new Samples();
        var everything = samples.OfEveryKind().Append(samples.Ink(highlighter: true)).Append(samples.Stamp(name: "NotForPublicRelease"))
            .Append(samples.Factory.Reply(samples.Note(), "reply")).Append(samples.Factory.ReviewStateChange(samples.Note(), ReviewState.Completed)).ToList();

        foreach (var a in everything)
        {
            var json = JsonSerializer.SerializeToElement(AnnotationFields.ToFields(a));
            var back = AnnotationFields.FromFields(json);

            Assert.True(AnnotationFields.Same(a, back), $"{a.Kind}: {json}");
            Assert.Equal(a.Kind, back.Kind);
            Assert.Equal(a.Rect, back.Rect);
            Assert.Equal(a.Created, back.Created);
        }
    }

    [Fact]
    public void ACommandWithMissingOrWrongFieldsIsRefusedNotGuessed()
    {
        var samples = new Samples();
        var fields = AnnotationFields.ToFields(samples.Rectangle());
        foreach (var missing in new[] { "kind", "rect", "color", "name", "page" })
        {
            var copy = new Dictionary<string, object?>(fields);
            copy.Remove(missing);
            Assert.Throws<FormatException>(() => AnnotationFields.FromFields(JsonSerializer.SerializeToElement(copy)));
        }

        var wrong = new Dictionary<string, object?>(fields) { ["kind"] = "Hologram" };
        Assert.Throws<FormatException>(() => AnnotationFields.FromFields(JsonSerializer.SerializeToElement(wrong)));
        var badStamp = new Dictionary<string, object?>(AnnotationFields.ToFields(samples.Stamp())) { ["stamp"] = null };
        Assert.Throws<FormatException>(() => AnnotationFields.FromFields(JsonSerializer.SerializeToElement(badStamp)));
    }

    [Fact]
    public void TheMapperReadsEveryFieldTheModelWritesAndTheBlobIsOneTheCodecAccepts()
    {
        var samples = new Samples();
        foreach (var a in samples.OfEveryKind())
        {
            samples.Session.Add(a);
        }

        var blob = AnnotationEditMapper.ToBlob(samples.Model.PendingCommands, 1);

        Assert.Equal(15, blob.Edits.Count);
        Assert.All(blob.Edits, static e => Assert.Equal(MikePDF.Protocol.Edits.EditOperation.Add, e.Operation));
        Assert.True(MikePDF.Protocol.Edits.EditBlobCodec.TryDecode(MikePDF.Protocol.Edits.EditBlobCodec.Encode(blob), out var decoded, out var error), error);
        var byName = decoded.Edits.ToDictionary(static e => e.Name!);
        foreach (var a in samples.Session.Store.OnPage(0))
        {
            var edit = byName[a.Id];
            Assert.Equal(new[] { (float)a.Rect.Left, (float)a.Rect.Bottom, (float)a.Rect.Right, (float)a.Rect.Top }, edit.Rect);
            Assert.Equal((float)a.Opacity, edit.Opacity);
            Assert.Equal(a.Author, edit.Author);
            Assert.Equal(PdfDates.Format(a.Created), edit.Created);
        }

        Assert.Equal(MikePDF.Protocol.Edits.AnnotationKind.Squiggly, byName.Values.Single(static e => e.Kind == MikePDF.Protocol.Edits.AnnotationKind.Squiggly).Kind);
        Assert.Equal(2, byName.Values.Count(static e => e.Kind == MikePDF.Protocol.Edits.AnnotationKind.FreeText));
        Assert.Equal(1, byName.Values.Count(static e => (e.Traits & MikePDF.Protocol.Edits.EditTraits.Typewriter) != 0));
        Assert.Equal(2, byName.Values.Count(static e => e.Kind == MikePDF.Protocol.Edits.AnnotationKind.Line));
        Assert.Equal("Approved", byName.Values.Single(static e => e.Kind == MikePDF.Protocol.Edits.AnnotationKind.Stamp).StandardName);
    }

    [Fact]
    public void ThePageFrameKeepsTheUserSpaceRectOfARotatedPageNormalised()
    {
        var samples = new Samples();
        var frame = Samples.Frame(90, [50, 30, 562, 762]);
        var a = samples.Factory.Box(MarkupKind.Rectangle, 0, frame.ToUser(new RectD(10, 20, 100, 40)));

        Assert.True(a.Rect.Left < a.Rect.Right && a.Rect.Bottom < a.Rect.Top);
        Assert.Equal((40, 100), (Math.Round(a.Rect.Width, 6), Math.Round(a.Rect.Height, 6))); // turned a quarter: the sides swap
    }
}
