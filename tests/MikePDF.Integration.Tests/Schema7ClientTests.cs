using MikePDF.Broker;
using MikePDF.Integration.Tests.Support;
using MikePDF.Protocol;

namespace MikePDF.Integration.Tests;

/// <summary>
/// A.9 for the schema 7 messages (text quads, the annotation read side, forms, SaveDocument, NormalizeTail). An <see cref="EngineClient"/> on one end of a real named pipe and a script on
/// the other: a reply the engine must never send ends the session instead of reaching the caller (ReplyChecks), a request that cannot be right is refused before it is sent, and a
/// correct reply arrives intact. Criteria AC-11, TM-01, FM-01 and SP-11 (the broker half).
/// </summary>
public sealed class Schema7ClientTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed record Scenario(string Name, MessageType Target, Func<EngineClient, uint, Task<Status>> Act, Func<RequestEnvelope, byte[]> Forge);

    private static byte[] Reply(RequestEnvelope r, Status status, MessageType type, IReplyMessage? payload = null) => FakeEngine.Response(r.RequestId, status, type, payload);

    // ---- valid building blocks, each changed in exactly one way by a scenario ----------------------------------------------------------

    private static GetTextQuadsReply Quads(uint total, int quads, bool truncated = false) => new()
    {
        TotalQuads = total,
        Truncated = truncated,
        Xs = new float[quads * 4],
        Ys = new float[quads * 4],
    };

    private static ListAnnotationsReply Annots(uint total, int count, int parent = -1) => new()
    {
        Total = total,
        Subtypes = new ushort[count],
        Flags = new uint[count],
        Lefts = new float[count],
        Bottoms = new float[count],
        Rights = new float[count],
        Tops = new float[count],
        Colours = new uint[count],
        Nms = [.. Enumerable.Repeat(string.Empty, count)],
        Authors = [.. Enumerable.Repeat(string.Empty, count)],
        Modified = [.. Enumerable.Repeat(string.Empty, count)],
        Snippets = [.. Enumerable.Repeat(string.Empty, count)],
        Parents = [.. Enumerable.Repeat(parent, count)],
    };

    private static GetAnnotationReply Annot(Action<GetAnnotationReply>? change = null)
    {
        var reply = new GetAnnotationReply { Subtype = 9, Flags = 4, Opacity = 1f, ParentIndex = -1, QuadPoints = [], Vertices = [], LineEndings = [] };
        change?.Invoke(reply);
        return reply;
    }

    private static GetFormFieldsReply Fields(Action<GetFormFieldsReply>? change = null)
    {
        var reply = new GetFormFieldsReply
        {
            Total = 2,
            FieldIds = [0, 1],
            Pages = [0, 1],
            Lefts = [1f, 1f],
            Bottoms = [1f, 1f],
            Rights = [2f, 2f],
            Tops = [2f, 2f],
            Types = [6, 2],
            Flags = [0, 0],
            States = [0, 0],
            MaxLens = [0, 0],
            Names = ["a", "b"],
            AlternateNames = ["", ""],
            Values = ["", ""],
        };
        change?.Invoke(reply);
        return reply;
    }

    private static GetFormFieldDetailReply Detail(Action<GetFormFieldDetailReply>? change = null)
    {
        var reply = new GetFormFieldDetailReply
        {
            Type = 4,
            Name = "n",
            WidgetCount = 1,
            OptionsTotal = 3,
            OptionLabels = ["a", "b", "c"],
            OptionValues = ["a", "b", "c"],
            SelectedIndices = [1],
        };
        change?.Invoke(reply);
        return reply;
    }

    private static NormalizeTailReply Normalized(bool ok, uint[]? codes = null, ulong bytes = 100) => new()
    {
        Ok = ok,
        BytesWritten = bytes,
        IssueCodes = codes ?? [],
        IssueMessages = [.. (codes ?? []).Select(c => "issue " + c)],
    };

    private static readonly Scenario[] AllScenarios =
    [
        new("quads-not-whole-quads", MessageType.GetTextQuads, async (c, d) => (await c.GetTextQuadsAsync(d, 0, 0, 10, 0, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetTextQuads, new GetTextQuadsReply { TotalQuads = 1, Xs = new float[3], Ys = new float[3] })),
        new("quads-window-short-of-what-was-left", MessageType.GetTextQuads, async (c, d) => (await c.GetTextQuadsAsync(d, 0, 0, 10, 0, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetTextQuads, Quads(total: 100, quads: 2))),
        new("quads-past-the-total", MessageType.GetTextQuads, async (c, d) => (await c.GetTextQuadsAsync(d, 0, 0, 10, 0, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetTextQuads, Quads(total: 1, quads: 2))),
        new("quads-truncated-without-the-limit", MessageType.GetTextQuads, async (c, d) => (await c.GetTextQuadsAsync(d, 0, 0, 10, 0, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetTextQuads, Quads(total: 1, quads: 1, truncated: true))),
        new("quads-answered-with-permission-denied", MessageType.GetTextQuads, async (c, d) => (await c.GetTextQuadsAsync(d, 0, 0, 10, 0, Wait)).Status,
            r => Reply(r, Status.PermissionDenied, MessageType.GetTextQuads)),
        new("annotations-window-longer-than-the-list", MessageType.ListAnnotations, async (c, d) => (await c.ListAnnotationsAsync(d, 0, 0, 5, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.ListAnnotations, Annots(total: 2, count: 3))),
        new("annotations-window-shorter-than-the-list", MessageType.ListAnnotations, async (c, d) => (await c.ListAnnotationsAsync(d, 0, 0, 5, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.ListAnnotations, Annots(total: 4, count: 2))),
        new("annotation-replies-to-itself-in-a-list", MessageType.ListAnnotations, async (c, d) => (await c.ListAnnotationsAsync(d, 0, 0, 1, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.ListAnnotations, Annots(total: 2, count: 1, parent: 0))),
        new("annotation-replies-to-an-entry-the-page-lacks", MessageType.ListAnnotations, async (c, d) => (await c.ListAnnotationsAsync(d, 0, 0, 1, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.ListAnnotations, Annots(total: 2, count: 1, parent: 5))),
        new("annotation-quads-on-a-circle", MessageType.GetAnnotation, async (c, d) => (await c.GetAnnotationAsync(d, 0, 3, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetAnnotation, Annot(a => { a.Subtype = 6; a.QuadPoints = new float[8]; }))),
        new("annotation-quads-not-whole", MessageType.GetAnnotation, async (c, d) => (await c.GetAnnotationAsync(d, 0, 3, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetAnnotation, Annot(a => a.QuadPoints = new float[6]))),
        new("annotation-vertices-on-a-highlight", MessageType.GetAnnotation, async (c, d) => (await c.GetAnnotationAsync(d, 0, 3, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetAnnotation, Annot(a => a.Vertices = new float[4]))),
        new("annotation-vertices-odd", MessageType.GetAnnotation, async (c, d) => (await c.GetAnnotationAsync(d, 0, 3, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetAnnotation, Annot(a => { a.Subtype = 7; a.Vertices = new float[3]; }))),
        new("annotation-strokes-on-a-highlight", MessageType.GetAnnotation, async (c, d) => (await c.GetAnnotationAsync(d, 0, 3, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetAnnotation, Annot(a => { a.StrokeCount = 1; a.PointCount = 4; }))),
        new("annotation-points-without-a-stroke", MessageType.GetAnnotation, async (c, d) => (await c.GetAnnotationAsync(d, 0, 3, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetAnnotation, Annot(a => { a.Subtype = 15; a.PointCount = 4; }))),
        new("annotation-replies-to-itself", MessageType.GetAnnotation, async (c, d) => (await c.GetAnnotationAsync(d, 0, 3, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetAnnotation, Annot(a => a.ParentIndex = 3))),
        new("ink-window-longer-than-the-stroke", MessageType.GetInkStroke, async (c, d) => (await c.GetInkStrokeAsync(d, 0, 0, 0, 0, 10, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetInkStroke, new GetInkStrokeReply { PointCount = 2, Xs = new float[3], Ys = new float[3], Widths = new float[3] })),
        new("ink-window-past-the-end-with-points", MessageType.GetInkStroke, async (c, d) => (await c.GetInkStrokeAsync(d, 0, 0, 0, 5, 10, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetInkStroke, new GetInkStrokeReply { PointCount = 2, Xs = new float[1], Ys = new float[1], Widths = new float[1] })),
        new("ink-widths-for-a-stroke-without-any", MessageType.GetInkStroke, async (c, d) => (await c.GetInkStrokeAsync(d, 0, 0, 0, 0, 10, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetInkStroke, new GetInkStrokeReply { PointCount = 1, Xs = new float[1], Ys = new float[1], HasWidths = false, Widths = [3f] })),
        new("counts-for-another-number-of-pages", MessageType.CountAnnotations, async (c, d) => (await c.CountAnnotationsAsync(d, 0, 2, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.CountAnnotations, new CountAnnotationsReply { Counts = [1] })),
        new("hidden-answered-with-unsupported", MessageType.SetAnnotationHidden, async (c, d) => (await c.SetAnnotationHiddenAsync(d, 0, 0, true, Wait)).Status,
            r => Reply(r, Status.Unsupported, MessageType.SetAnnotationHidden)),
        new("form-fields-window-longer-than-the-list", MessageType.GetFormFields, async (c, d) => (await c.GetFormFieldsAsync(d, 0, 5, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetFormFields, Fields(f => f.Total = 1))),
        new("form-field-ids-repeat", MessageType.GetFormFields, async (c, d) => (await c.GetFormFieldsAsync(d, 0, 5, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetFormFields, Fields(f => f.FieldIds = [0, 0]))),
        new("form-field-on-a-page-the-document-lacks", MessageType.GetFormFields, async (c, d) => (await c.GetFormFieldsAsync(d, 0, 5, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetFormFields, Fields(f => f.Pages = [0, FakeEngine.FakePageCount]))),
        new("form-fields-out-of-page-order", MessageType.GetFormFields, async (c, d) => (await c.GetFormFieldsAsync(d, 0, 5, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetFormFields, Fields(f => f.Pages = [2, 1]))),
        new("form-field-rectangle-reversed", MessageType.GetFormFields, async (c, d) => (await c.GetFormFieldsAsync(d, 0, 5, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetFormFields, Fields(f => f.Lefts = [9f, 1f]))),
        new("password-value-sent", MessageType.GetFormFields, async (c, d) => (await c.GetFormFieldsAsync(d, 0, 5, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetFormFields, Fields(f => { f.Flags = [1u << 13, 0]; f.Values = ["hunter2", ""]; }))),
        new("detail-options-on-a-text-field", MessageType.GetFormFieldDetail, async (c, d) => (await c.GetFormFieldDetailAsync(d, 0, 0, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetFormFieldDetail, Detail(f => f.Type = 6))),
        new("detail-check-state-on-a-combo-box", MessageType.GetFormFieldDetail, async (c, d) => (await c.GetFormFieldDetailAsync(d, 0, 0, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetFormFieldDetail, Detail(f => f.Checked = true))),
        new("detail-option-window-wrong", MessageType.GetFormFieldDetail, async (c, d) => (await c.GetFormFieldDetailAsync(d, 0, 1, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetFormFieldDetail, Detail())),
        new("detail-selected-option-outside-the-list", MessageType.GetFormFieldDetail, async (c, d) => (await c.GetFormFieldDetailAsync(d, 0, 0, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetFormFieldDetail, Detail(f => f.SelectedIndices = [3]))),
        new("detail-selected-options-descending", MessageType.GetFormFieldDetail, async (c, d) => (await c.GetFormFieldDetailAsync(d, 0, 0, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.GetFormFieldDetail, Detail(f => f.SelectedIndices = [2, 1]))),
        new("set-value-page-the-document-lacks", MessageType.SetFormFieldValue, async (c, d) => (await c.SetFormFieldValueAsync(d, 0, 0, "x", false, null, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.SetFormFieldValue, new SetFormFieldValueReply { Accepted = true, Value = "x", AffectedPages = [FakeEngine.FakePageCount] })),
        new("set-value-page-twice", MessageType.SetFormFieldValue, async (c, d) => (await c.SetFormFieldValueAsync(d, 0, 0, "x", false, null, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.SetFormFieldValue, new SetFormFieldValueReply { Accepted = true, Value = "x", AffectedPages = [1, 1] })),
        new("set-value-pages-not-ascending", MessageType.SetFormFieldValue, async (c, d) => (await c.SetFormFieldValueAsync(d, 0, 0, "x", false, null, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.SetFormFieldValue, new SetFormFieldValueReply { Accepted = true, Value = "x", AffectedPages = [0, 2, 1] })),
        new("set-value-truncated-list-not-full", MessageType.SetFormFieldValue, async (c, d) => (await c.SetFormFieldValueAsync(d, 0, 0, "x", false, null, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.SetFormFieldValue, new SetFormFieldValueReply { Accepted = true, Value = "x", AffectedPages = [0], Truncated = true })),
        new("set-value-answered-with-cancelled", MessageType.SetFormFieldValue, async (c, d) => (await c.SetFormFieldValueAsync(d, 0, 0, "x", false, null, Wait)).Status,
            r => Reply(r, Status.Cancelled, MessageType.SetFormFieldValue)),
        new("form-action-reset-that-changed-no-page", MessageType.FormAction, async (c, d) => (await c.FormActionAsync(d, 0, 0, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.FormAction, new FormActionReply { AffectedPages = [], Performed = 1 })),
        new("form-action-nothing-with-pages", MessageType.FormAction, async (c, d) => (await c.FormActionAsync(d, 0, 0, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.FormAction, new FormActionReply { AffectedPages = [0], Performed = 0 })),
        new("form-action-refusal-for-a-reset", MessageType.FormAction, async (c, d) => (await c.FormActionAsync(d, 0, 0, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.FormAction, new FormActionReply { AffectedPages = [], Performed = 2 })),
        new("form-action-page-the-document-lacks", MessageType.FormAction, async (c, d) => (await c.FormActionAsync(d, 0, 1, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.FormAction, new FormActionReply { AffectedPages = [7], Performed = 1 })),
        new("save-another-page-count", MessageType.SaveDocument, async (c, d) => (await c.SaveDocumentAsync(d, 0, 0, 1, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.SaveDocument, new SaveDocumentReply { BytesWritten = 10, PageCount = FakeEngine.FakePageCount + 1 })),
        new("save-that-wrote-nothing", MessageType.SaveDocument, async (c, d) => (await c.SaveDocumentAsync(d, 0, 0, 1, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.SaveDocument, new SaveDocumentReply { BytesWritten = 0, PageCount = FakeEngine.FakePageCount })),
        new("save-answered-with-engine-crashed", MessageType.SaveDocument, async (c, d) => (await c.SaveDocumentAsync(d, 0, 0, 1, Wait)).Status,
            r => Reply(r, Status.EngineCrashed, MessageType.SaveDocument)),
        new("normalize-tail-ok-with-issues", MessageType.NormalizeTail, async (c, _) => (await c.NormalizeTailAsync(1, 2, 3, 3, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.NormalizeTail, Normalized(ok: true, codes: [3]))),
        new("normalize-tail-ok-that-wrote-nothing", MessageType.NormalizeTail, async (c, _) => (await c.NormalizeTailAsync(1, 2, 3, 3, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.NormalizeTail, Normalized(ok: true, bytes: 0))),
        new("normalize-tail-refusal-without-a-reason", MessageType.NormalizeTail, async (c, _) => (await c.NormalizeTailAsync(1, 2, 3, 3, Wait)).Status,
            r => Reply(r, Status.Ok, MessageType.NormalizeTail, Normalized(ok: false))),
        new("normalize-tail-answered-with-damaged", MessageType.NormalizeTail, async (c, _) => (await c.NormalizeTailAsync(1, 2, 3, 3, Wait)).Status,
            r => Reply(r, Status.Damaged, MessageType.NormalizeTail)),
    ];

    private static readonly Dictionary<string, Scenario> ByName = AllScenarios.ToDictionary(s => s.Name);

    public static TheoryData<string> ScenarioNames => [.. AllScenarios.Select(s => s.Name)];

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task AForgedReplyEndsTheSessionAndNeverReachesTheCaller(string name)
    {
        var scenario = ByName[name];
        await using var fake = await FakeEngine.StartAsync(script: (_, r) => r.MessageType == scenario.Target ? [scenario.Forge(r)] : null);
        await using var client = new EngineClient(fake.ClientStream, fake.Ring);
        var faulted = new TaskCompletionSource<EngineFault>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Faulted += fault => faulted.TrySetResult(fault);
        var opened = await client.OpenDocumentAsync(1, null, Wait);
        Assert.Equal(Status.Ok, opened.Status);

        var status = await scenario.Act(client, opened.Value!.DocumentId).WaitAsync(Wait);

        Assert.Equal(EngineFaultKind.ProtocolViolation, (await faulted.Task.WaitAsync(Wait)).Kind);
        Assert.Equal(Status.EngineCrashed, status);
        Assert.True(client.IsFaulted);
    }

    [Fact]
    public async Task CorrectRepliesArriveIntact()
    {
        await using var fake = await FakeEngine.StartAsync(script: (_, r) => r.Payload switch
        {
            GetTextQuads => [Reply(r, Status.Ok, MessageType.GetTextQuads, Quads(total: 70, quads: 6))],   // the second window of 70 quads is 6
            ListAnnotations => [Reply(r, Status.Ok, MessageType.ListAnnotations, Annots(total: 3, count: 3))],
            GetAnnotation => [Reply(r, Status.Ok, MessageType.GetAnnotation, Annot(a => { a.Subtype = 15; a.StrokeCount = 2; a.PointCount = 5; }))],
            GetInkStroke => [Reply(r, Status.Ok, MessageType.GetInkStroke, new GetInkStrokeReply { PointCount = 3, Xs = [1f, 2f], Ys = [3f, 4f], Widths = [0f, 0f] })],
            CountAnnotations => [Reply(r, Status.Ok, MessageType.CountAnnotations, new CountAnnotationsReply { Counts = [1, 2] })],
            SetAnnotationHidden => [Reply(r, Status.Ok, MessageType.SetAnnotationHidden, new SetAnnotationHiddenReply())],
            GetFormFields => [Reply(r, Status.Ok, MessageType.GetFormFields, Fields())],
            GetFormFieldDetail => [Reply(r, Status.Ok, MessageType.GetFormFieldDetail, Detail())],
            SetFormFieldValue => [Reply(r, Status.Ok, MessageType.SetFormFieldValue, new SetFormFieldValueReply { Accepted = true, Value = "x", AffectedPages = [2, 0, 1] })],
            FormAction => [Reply(r, Status.Ok, MessageType.FormAction, new FormActionReply { AffectedPages = [0, 1], Performed = 1 })],
            SaveDocument => [Reply(r, Status.Ok, MessageType.SaveDocument, new SaveDocumentReply { BytesWritten = 1234, PageCount = FakeEngine.FakePageCount })],
            NormalizeTail => [Reply(r, Status.Ok, MessageType.NormalizeTail, Normalized(ok: true))],
            _ => null,
        });
        await using var client = new EngineClient(fake.ClientStream, fake.Ring);
        var fault = "no fault";
        client.Faulted += f => fault = f.Detail;
        async Task<string> Why() { await Task.Delay(200); return fault; }
        var document = (await client.OpenDocumentAsync(1, null, Wait)).Value!.DocumentId;

        var quads = await client.GetTextQuadsAsync(document, 0, 0, 100, 64, Wait);
        Assert.True(quads.Status == Status.Ok, "quads: " + quads.Status + " " + (quads.Status == Status.Ok ? "" : await Why()));
        Assert.Equal(6, quads.Value!.Xs.Length / 4);
        var annotations = await client.ListAnnotationsAsync(document, 0, 0, 64, Wait);
        Assert.True(annotations.Status == Status.Ok, "annotations: " + annotations.Status + " " + (annotations.Status == Status.Ok ? "" : await Why()));
        Assert.Equal(3, annotations.Value!.Subtypes.Length);
        var annotation = await client.GetAnnotationAsync(document, 0, 1, Wait);
        Assert.True(annotation.Status == Status.Ok, "annotation: " + annotation.Status + " " + (annotation.Status == Status.Ok ? "" : await Why()));
        Assert.Equal(2u, annotation.Value!.StrokeCount);
        var ink = await client.GetInkStrokeAsync(document, 0, 1, 0, 0, 2, Wait);
        Assert.True(ink.Status == Status.Ok, "ink: " + ink.Status + " " + (ink.Status == Status.Ok ? "" : await Why()));
        Assert.Equal([1f, 2f], ink.Value!.Xs);
        var counts = await client.CountAnnotationsAsync(document, 0, 2, Wait);
        Assert.True(counts.Status == Status.Ok, "counts: " + counts.Status + " " + (counts.Status == Status.Ok ? "" : await Why()));
        Assert.Equal([(ushort)1, (ushort)2], counts.Value!.Counts);
        Assert.Equal(Status.Ok, (await client.SetAnnotationHiddenAsync(document, 0, 1, true, Wait)).Status);
        var fields = await client.GetFormFieldsAsync(document, 0, 64, Wait);
        Assert.True(fields.Status == Status.Ok, "fields: " + fields.Status + " " + (fields.Status == Status.Ok ? "" : await Why()));
        Assert.Equal(2u, fields.Value!.Total);
        var detail = await client.GetFormFieldDetailAsync(document, 0, 0, Wait);
        Assert.True(detail.Status == Status.Ok, "detail: " + detail.Status + " " + (detail.Status == Status.Ok ? "" : await Why()));
        Assert.Equal("n", detail.Value!.Name);
        var set = await client.SetFormFieldValueAsync(document, 0, 0, "x", false, null, Wait);
        Assert.True(set.Status == Status.Ok, "set: " + set.Status + " " + (set.Status == Status.Ok ? "" : await Why()));
        Assert.Equal([2u, 0u, 1u], set.Value!.AffectedPages);
        var action = await client.FormActionAsync(document, 0, 1, Wait);
        Assert.True(action.Status == Status.Ok, "action: " + action.Status + " " + (action.Status == Status.Ok ? "" : await Why()));
        Assert.Equal(1, action.Value!.Performed);
        var saved = await client.SaveDocumentAsync(document, 0, 0, 7, Wait);
        Assert.True(saved.Status == Status.Ok, "saved: " + saved.Status + " " + (saved.Status == Status.Ok ? "" : await Why()));
        Assert.Equal(1234ul, saved.Value!.BytesWritten);
        var normalized = await client.NormalizeTailAsync(1, 2, 3, 3, Wait);
        Assert.True(normalized.Status == Status.Ok, "normalized: " + normalized.Status + " " + (normalized.Status == Status.Ok ? "" : await Why()));
        Assert.True(normalized.Value!.Ok);
        Assert.False(client.IsFaulted);
    }

    [Fact]
    public async Task RequestsThatCannotBeRightAreRefusedBeforeTheyAreSent()
    {
        await using var fake = await FakeEngine.StartAsync();
        await using var client = new EngineClient(fake.ClientStream, fake.Ring);
        var document = (await client.OpenDocumentAsync(1, null, Wait)).Value!.DocumentId; // three pages
        var sent = fake.Received.Count;

        // an unknown document
        Assert.Equal(Status.InvalidArgument, (await client.GetTextQuadsAsync(document + 1, 0, 0, 1, 0, Wait)).Status);
        Assert.Equal(Status.InvalidArgument, (await client.GetFormFieldsAsync(document + 1, 0, 1, Wait)).Status);
        Assert.Equal(Status.InvalidArgument, (await client.SaveDocumentAsync(document + 1, 0, 0, 1, Wait)).Status);
        // a page the document does not have
        Assert.Equal(Status.InvalidArgument, (await client.GetTextQuadsAsync(document, 3, 0, 1, 0, Wait)).Status);
        Assert.Equal(Status.InvalidArgument, (await client.ListAnnotationsAsync(document, 3, 0, 1, Wait)).Status);
        Assert.Equal(Status.InvalidArgument, (await client.GetAnnotationAsync(document, 3, 0, Wait)).Status);
        Assert.Equal(Status.InvalidArgument, (await client.GetInkStrokeAsync(document, 3, 0, 0, 0, 1, Wait)).Status);
        Assert.Equal(Status.InvalidArgument, (await client.SetAnnotationHiddenAsync(document, 3, 0, true, Wait)).Status);
        // a character range beyond the 200,000 the engine reads, or empty
        Assert.Equal(Status.InvalidArgument, (await client.GetTextQuadsAsync(document, 0, 199_999, 2, 0, Wait)).Status);
        Assert.Equal(Status.InvalidArgument, (await client.GetTextQuadsAsync(document, 0, 0, 0, 0, Wait)).Status);
        // pages counted beyond the end
        Assert.Equal(Status.InvalidArgument, (await client.CountAnnotationsAsync(document, 2, 2, Wait)).Status);
        Assert.Equal(Status.InvalidArgument, (await client.CountAnnotationsAsync(document, 0, 0, Wait)).Status);
        // limits of the schema: the encoder refuses what the decoder would
        Assert.Equal(Status.InvalidArgument, (await client.ListAnnotationsAsync(document, 0, 0, 65, Wait)).Status);
        Assert.Equal(Status.InvalidArgument, (await client.GetFormFieldsAsync(document, 100_000, 1, Wait)).Status);
        Assert.Equal(Status.InvalidArgument, (await client.SetFormFieldValueAsync(document, 0, 3, "x", false, null, Wait)).Status);
        Assert.Equal(Status.InvalidArgument, (await client.SetFormFieldValueAsync(document, 0, 0, new string('x', 8193), false, null, Wait)).Status);
        Assert.Equal(Status.InvalidArgument, (await client.FormActionAsync(document, 0, 3, Wait)).Status);
        Assert.Equal(Status.InvalidArgument, (await client.SaveDocumentAsync(document, 3, 0, 1, Wait)).Status);
        Assert.Equal(Status.InvalidArgument, (await client.SaveDocumentAsync(document, 0, 0, 0, Wait)).Status);          // no handle
        Assert.Equal(Status.InvalidArgument, (await client.NormalizeTailAsync(0, 1, 1, 0, Wait)).Status);                // no handle
        Assert.Equal(Status.InvalidArgument, (await client.NormalizeTailAsync(1, 1, 1, 4, Wait)).Status);                // a flag that does not exist
        Assert.Equal(sent, fake.Received.Count);
        Assert.False(client.IsFaulted);
    }
}
