using System.Text.Json;
using MikePDF.Core.Editing;
using MikePDF.Core.Geometry;
using MikePDF.Ink;

namespace MikePDF.Markup;

/// <summary>
/// The annotation edits of one document (annotation-core 3.6, 8): commands that go into the save core's <see cref="EditModel"/> (so they are
/// journaled, saved and undone with everything else) and, at the same time, change an <see cref="AnnotationStore"/> that the overlay, the panel and
/// hit-testing read. Every command has an inverse command, so an undo after a save is a new change relative to the saved file (AC-07). A change
/// to the store and its reversal both go through <see cref="MarkupReplay"/>, the same interpretation recovery uses.
/// <para>
/// Granularity (annotation-core 8): a drag is one command, nudges within 750 ms are one, a text edit is one, a restyle of a selection is one, an
/// ink stroke is one, an erase gesture is one, and a multi-page mark or a multi-select delete is one composite.
/// </para>
/// </summary>
public sealed class MarkupSession
{
    /// <summary>Nudges of one annotation within this time are one undo step (annotation-core 3.4).</summary>
    public static readonly TimeSpan NudgeWindow = TimeSpan.FromMilliseconds(750);

    private sealed record Change(long Sequence, EditCommand Forward, EditCommand? Inverse, Action<AnnotationStore>? ManualBackward, string Label);

    private readonly List<Change> _done = [];
    private readonly Stack<Change> _undone = new();
    private readonly TimeProvider _time;
    private readonly BatchPolicy _batch;
    private string? _inkBatchId;
    private (string Id, long Sequence, DateTimeOffset At, PdfRect Origin)? _nudge;

    public MarkupSession(EditModel model, MarkupFactory factory, TimeProvider? time = null)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        Factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = time ?? TimeProvider.System;
        _batch = new BatchPolicy(new TimeClock(_time));
    }

    public EditModel Model { get; }

    public AnnotationStore Store { get; } = new();

    public MarkupFactory Factory { get; }

    /// <summary>The resource key of why the last refused command was refused (null after a command that worked).</summary>
    public string? LastReason { get; private set; }

    /// <summary>A short label of the command an undo would undo, for the announcement "Undo: {0}" (annotation-core 5).</summary>
    public string? UndoLabel => _done.Count == 0 ? null : _done[^1].Label;

    public string? RedoLabel => _undone.Count == 0 ? null : _undone.Peek().Label;

    /// <summary>Puts annotations read from the file into the store. No command, no journal entry.</summary>
    public void Load(IEnumerable<Annotation> loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        foreach (var a in loaded)
        {
            Store.Add(a with { Origin = AnnotationOrigin.Loaded });
        }
    }

    // ---- adding ----

    /// <summary>Adds an annotation (a reply or a review state if it is one) as one undoable command.</summary>
    public Annotation Add(Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        var prepared = annotation with { Created = annotation.Created == default ? Factory.Now() : annotation.Created, Modified = Factory.Now() };
        Run(AddCommand(prepared), DeleteCommand(prepared), LabelOf(prepared, "added"));
        return prepared;
    }

    /// <summary>Adds several as one step: a mark that spans pages (text-markup 2.2, TM-07), or pasted shapes.</summary>
    public IReadOnlyList<Annotation> AddMany(IReadOnlyList<Annotation> annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        if (annotations.Count == 0)
        {
            return [];
        }

        var now = Factory.Now();
        var prepared = annotations.Select(a => a with { Created = a.Created == default ? now : a.Created, Modified = now }).ToList();
        Run(Composite(prepared.Select(static a => AddCommand(a))), Composite(Enumerable.Reverse(prepared).Select(DeleteCommand)), LabelOf(prepared[0], "added"));
        return prepared;
    }

    /// <summary>A reply to an annotation. Refused when the parent has no name another reader could find it by.</summary>
    public Annotation? Reply(string parentId, string text)
    {
        if (Store.Get(parentId) is not { } parent || !CanBeParent(parent))
        {
            return Refuse(AnnotationRules.ReasonUnsupported);
        }

        var thread = Store.Thread(parentId).Count(static a => a.ReplyKind == ReplyKind.Reply);
        if (thread >= 1_000)
        {
            return Refuse(AnnotationRules.ReasonThreadLimit);
        }

        return Add(Factory.Reply(parent, text));
    }

    /// <summary>Sets the review state of an annotation by adding a state annotation (history is kept, sticky-notes 2.3).</summary>
    public Annotation? SetReviewState(string parentId, ReviewState state)
    {
        if (Store.Get(parentId) is not { } parent || !CanBeParent(parent))
        {
            return Refuse(AnnotationRules.ReasonUnsupported);
        }

        return Add(Factory.ReviewStateChange(parent, state));
    }

    /// <summary>The current state of an annotation: the newest state annotation that answers it (SN-02), by <c>Modified</c>, ties broken by page order.</summary>
    public ReviewState CurrentState(string id) =>
        Store.RepliesTo(id).Select((a, i) => (a, i)).Where(static x => x.a.ReplyKind == ReplyKind.State)
            .OrderBy(static x => x.a.Modified).ThenBy(static x => x.i).Select(static x => x.a.State).LastOrDefault();

    // ---- deleting ----

    /// <summary>Deletes an annotation and, for a thread root, everything that answers it, as one step. Returns false (with <see cref="LastReason"/>) when it is not allowed.</summary>
    public bool Delete(string id)
    {
        if (Store.Get(id) is not { } a || !Allowed(a, AnnotationOperation.Delete))
        {
            return false;
        }

        return DeleteAll(Store.Thread(id));
    }

    /// <summary>Deletes several annotations, with their threads, as one step (multi-select delete).</summary>
    public bool DeleteMany(IEnumerable<string> ids)
    {
        var all = new List<Annotation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (Store.Get(id) is not { } a || !Allowed(a, AnnotationOperation.Delete))
            {
                return false;
            }

            foreach (var member in Store.Thread(id))
            {
                if (seen.Add(member.Id))
                {
                    all.Add(member);
                }
            }
        }

        return all.Count > 0 && DeleteAll(all);
    }

    private bool DeleteAll(IReadOnlyList<Annotation> members)
    {
        if (members.Any(static m => !Targetable(m)))
        {
            LastReason = AnnotationRules.ReasonUnsupported;
            return false;
        }

        if (_inkBatchId is not null && members.Any(m => m.Id == _inkBatchId))
        {
            CloseInkBatch(BatchCloseReason.SelectionChange);
        }

        // Children before parents when writing, parents before children when putting back; indexes are kept so the stacking order returns.
        var ordered = members.OrderByDescending(m => Store.IndexOf(m.Id)).ToList();
        var restore = members.Select(m => (Annotation: m, Index: Store.IndexOf(m.Id))).OrderBy(static x => x.Index).ToList();
        var forward = members.Count == 1 ? DeleteCommand(members[0]) : Composite(ordered.Select(DeleteCommand));
        var owned = members.All(static m => m.IsOwned);
        var inverse = owned ? members.Count == 1 ? AddCommand(members[0], restore[0].Index) : Composite(restore.Select(x => AddCommand(x.Annotation, x.Index))) : null;
        Run(forward, inverse, LabelOf(members[0], members.Count > 1 ? "deleted with replies" : "deleted"), backward: store =>
        {
            foreach (var (annotation, index) in restore)
            {
                store.Add(annotation, index);
            }
        });
        return true;
    }

    // ---- moving, resizing, restyling ----

    /// <summary>Moves an annotation by a vector, clamped so that at least 8 x 8 points stay on the page. One step.</summary>
    public bool Move(string id, double dx, double dy, PdfRect? pageBox = null)
    {
        _nudge = null;
        return MoveCore(id, dx, dy, pageBox, label: "moved");
    }

    /// <summary>Moves by a keyboard nudge; nudges of one annotation within 750 ms are a single undo step (AC-08).</summary>
    public bool Nudge(string id, double dx, double dy, PdfRect? pageBox = null)
    {
        if (Store.Get(id) is not { } before || !Allowed(before, AnnotationOperation.Move))
        {
            return false;
        }

        var now = _time.GetUtcNow();
        var continues = _nudge is { } last && last.Id == id && Model.TopSequence == last.Sequence && Model.PendingCount > 0 && !Model.IsSaving && now - last.At <= NudgeWindow;
        var origin = before.Rect;
        double carriedX = 0, carriedY = 0;
        if (continues)
        {
            // Take the previous nudge back and make one move from where the run started, so the run is one step in the stack and in the journal.
            origin = _nudge!.Value.Origin;
            carriedX = before.Rect.Left - origin.Left;
            carriedY = before.Rect.Bottom - origin.Bottom;
            UndoCore();
        }

        var depth = _done.Count;
        var moved = MoveCore(id, carriedX + dx, carriedY + dy, pageBox, label: "moved");
        _nudge = moved && _done.Count > depth ? (id, Model.TopSequence, now, origin) : null;
        return moved;
    }

    private bool MoveCore(string id, double dx, double dy, PdfRect? pageBox, string label, (double X, double Y) extra = default)
    {
        if (Store.Get(id) is not { } a || !Allowed(a, AnnotationOperation.Move))
        {
            return false;
        }

        var (mx, my) = pageBox is { } box ? MoveResize.ClampMove(a.Rect, dx, dy, box) : (dx, dy);
        if (Math.Abs(mx) < 1e-9 && Math.Abs(my) < 1e-9)
        {
            return true;
        }

        var moved = AnnotationTransform.Translate(a, mx, my) with { Modified = Factory.Now() };
        return Restate(a, moved, label);
    }

    /// <summary>Resizes an annotation onto a rectangle (the caller has already applied the handle rules of <see cref="MoveResize"/>). One step.</summary>
    public bool ResizeTo(string id, PdfRect rect)
    {
        if (Store.Get(id) is not { } a || !Allowed(a, AnnotationOperation.Resize))
        {
            return false;
        }

        var resized = a.Payload is PolyPayload ? VertexEdit.Scale(a, rect) : AnnotationTransform.ResizeTo(a, Geo.Normalised(rect));
        return Restate(a, MarkupFactory.GrowToFit(resized with { Modified = Factory.Now() }), "resized");
    }

    /// <summary>Changes an annotation by a function (colour, width, opacity, text, icon, vertices...). Foreign annotations cannot be restyled. One step.</summary>
    public bool Update(string id, Func<Annotation, Annotation> change, string label = "changed")
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Store.Get(id) is not { } a || !Allowed(a, AnnotationOperation.Restyle))
        {
            return false;
        }

        var changed = change(a);
        if (changed.Id != a.Id || changed.Page != a.Page)
        {
            throw new InvalidOperationException("A change keeps the id and the page.");
        }

        var refit = MarkupGeometry.Refit(MarkupFactory.GrowToFit(changed with { Modified = Factory.Now() }));
        return Restate(a, refit, label);
    }

    /// <summary>Applies one restyle to several annotations as one step (restyle of a selection).</summary>
    public bool UpdateMany(IEnumerable<string> ids, Func<Annotation, Annotation> change, string label = "changed")
    {
        var pairs = new List<(Annotation Old, Annotation New)>();
        foreach (var id in ids)
        {
            if (Store.Get(id) is not { } a)
            {
                continue;
            }

            if (!Allowed(a, AnnotationOperation.Restyle))
            {
                continue; // restyle applies to those that accept it (annotation-core 3.4)
            }

            pairs.Add((a, MarkupGeometry.Refit(MarkupFactory.GrowToFit(change(a) with { Modified = Factory.Now() }))));
        }

        if (pairs.Count == 0)
        {
            return false;
        }

        Run(Composite(pairs.Select(p => ReplaceCommand(p.Old, p.New))), Composite(pairs.Select(p => ReplaceCommand(p.New, p.Old))), pairs.Count == 1 ? LabelOf(pairs[0].New, label) : $"{pairs.Count} annotations {label}");
        return true;
    }

    /// <summary>Changes the comment of an annotation. Owned ones and foreign ones by the same author. One step.</summary>
    public bool SetContents(string id, string text, PageFrame? frame = null)
    {
        if (Store.Get(id) is not { } a || !Allowed(a, AnnotationOperation.EditContents))
        {
            return false;
        }

        var normal = CommentText.Truncate(CommentText.Normalise(text));
        if (a.IsOwned)
        {
            // A mark with a comment has a popup for other readers to open it by; without one it has none. A note always has one (sticky-notes 2.4).
            var popup = a.Kind == MarkupKind.Note || a.ReplyKind != ReplyKind.None
                ? a.PopupRect
                : normal.Length == 0 ? null : a.PopupRect ?? (frame is null ? null : NoteGeometry.PopupFor(frame, a.Rect));
            return Restate(a, MarkupFactory.GrowToFit(a with { Contents = normal, PopupRect = popup, Modified = Factory.Now() }), "comment changed");
        }

        // A foreign annotation keeps its appearance: only /Contents changes.
        var changed = a with { Contents = normal, Modified = Factory.Now() };
        Run(ContentsCommand(a, normal), ContentsCommand(changed, a.Contents), LabelOf(a, "comment changed"));
        return true;
    }

    private bool Restate(Annotation old, Annotation changed, string label)
    {
        if (!Targetable(old))
        {
            LastReason = AnnotationRules.ReasonUnsupported;
            return false;
        }

        if (old.IsOwned)
        {
            Run(ReplaceCommand(old, changed), ReplaceCommand(changed, old), LabelOf(changed, label));
        }
        else
        {
            // Foreign: only the rectangle moves, and the file's appearance stays as it is (annotation-core 3.2).
            Run(RectCommand(old, changed.Rect), RectCommand(changed, old.Rect), LabelOf(changed, label));
        }

        return true;
    }

    // ---- ink ----
    /// <summary>
    /// Adds a finished stroke. It joins the open batch (an ink annotation) or starts a new one by the rules of ink-pen 3.7. One command per stroke
    /// (the journal's <c>AddInkStroke</c>); the batch is an object-level idea only, undo is per stroke.
    /// </summary>
    public Annotation AddInkStroke(int page, InkStroke stroke, InkStyle style, Rgb color, double opacity = 1, string? author = null)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        var s = style.Clamped();
        if (_inkBatchId is not null && (Store.Get(_inkBatchId) is null || !BatchIsPending(_inkBatchId)))
        {
            // The batch was deleted, or a save wrote it: strokes after that start a new one (ink-pen 3.7: a save closes a batch).
            CloseInkBatch(BatchCloseReason.Save);
        }

        var key = new BatchKey(page, s.Tool, ((uint)Math.Round(color.R * 255) << 16) | ((uint)Math.Round(color.G * 255) << 8) | (uint)Math.Round(color.B * 255), s.BaseWidth, (float)opacity);
        var startNew = _batch.AddStroke(key, stroke.Samples.Count) == BatchDecision.StartNew || _inkBatchId is null;
        if (startNew)
        {
            _inkBatchId = AnnotationIds.New();
        }

        var id = _inkBatchId!;
        var before = Store.Get(id);
        var now = Factory.Now();
        var forward = EditCommand.Create("AddInkStroke", new Dictionary<string, object?>
        {
            ["annotation"] = id,
            ["page"] = page,
            ["tool"] = s.Tool == InkTool.Highlighter ? "highlighter" : "pen",
            ["color"] = new[] { color.R, color.G, color.B },
            ["opacity"] = opacity,
            ["width"] = s.BaseWidth,
            ["curve"] = s.Curve.ToString().ToLowerInvariant(),
            ["minRatio"] = s.MinWidthRatio,
            ["author"] = author ?? Factory.Author,
            ["subj"] = MarkupDefaults.SubjectFor(MarkupKind.Ink),
            ["created"] = PdfDates.Format(now),
            ["modified"] = PdfDates.Format(now),
            ["stroke"] = new Dictionary<string, object?> { ["id"] = stroke.Id, ["n"] = stroke.Samples.Count, ["data"] = StrokeCodec.ToBase64(stroke.Samples) },
            ["newBatch"] = startNew,
        });

        // The inverse needs the rectangle the batch has once the stroke is in, so the stroke goes into the store first and is taken out again if the
        // edit model refuses the command (a save is running, or the journal is strict and cannot write).
        MarkupReplay.Apply(Store, forward);
        var result = Store.Get(id)!;
        var inverse = startNew
            ? EditCommand.Create("DeleteAnnotation", new { page, name = id, subtype = "Ink", rect = Rect(result.Rect) })
            : ReplaceCommand(result, before!);
        try
        {
            RecordApplied(forward, inverse, "Ink stroke added");
        }
        catch
        {
            if (before is null)
            {
                Store.Remove(id);
            }
            else
            {
                Store.Replace(before);
            }

            throw;
        }

        return result;
    }

    private bool BatchIsPending(string id) =>
        Model.PendingCommands.Any(c => c.Cmd == "AddInkStroke" && c.Payload.TryGetProperty("annotation", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() == id);
    /// <summary>Ends the open batch (a page change, a tool change, a selection change, a save).</summary>
    public void CloseInkBatch(BatchCloseReason reason)
    {
        _batch.CloseBatch(reason);
        _inkBatchId = null;
    }

    /// <summary>
    /// One erase gesture over an ink annotation: stroke mode removes the strokes the circle touches, point mode splits them (ink-pen 3.6). One step.
    /// The annotation is deleted when no stroke is left. Returns false when nothing was touched.
    /// </summary>
    public bool EraseInk(string id, EraserMode mode, IReadOnlyList<(double X, double Y)> path, double radius)
    {
        if (Store.Get(id) is not { Payload: InkPayload ink } a || !a.IsOwned || ink.Strokes.Count == 0)
        {
            LastReason = Store.Get(id) is { IsOwned: false } ? AnnotationRules.ReasonForeign : AnnotationRules.ReasonUnsupported;
            return false;
        }

        var style = new InkStyle(ink.Highlighter ? InkTool.Highlighter : InkTool.Pen, (float)a.Width, PressureCurve.Linear, InkConstants.DefaultMinWidthRatio).Clamped();
        var strokes = ink.Strokes.Select((s, i) => ToInkStroke(s, i, style)).ToList();
        var counter = 0;
        var result = StrokeEraser.EraseGesture(strokes, style, mode, path, radius, () => $"e{counter++}");
        if (result.RemovedIds.Count == 0 && result.Added.Count == 0)
        {
            return false;
        }

        var kept = strokes.Where(s => !result.RemovedIds.Contains(s.Id)).Concat(result.Added).ToList();
        var removedSet = result.RemovedIds.ToHashSet();
        var survivors = ink.Strokes.Where((_, i) => !removedSet.Contains($"s{i}")).Concat(result.Added.Select(p =>
        {
            var simplified = Simplifier.Simplify(p.Samples, style);
            return new InkStrokeData(simplified.Xy, simplified.Widths);
        })).ToList();
        if (kept.Count == 0 || survivors.Count == 0)
        {
            return DeleteAll([a]);
        }

        var changed = MarkupGeometry.Refit(a with { Payload = ink with { Strokes = survivors }, Modified = Factory.Now() });
        var fields = ReplaceFields(a, changed);
        fields["annotation"] = id;
        fields["removed"] = result.RemovedIds;
        fields["added"] = result.Added.Select(p => new Dictionary<string, object?> { ["id"] = p.Id, ["n"] = p.Samples.Count, ["data"] = StrokeCodec.ToBase64(p.Samples) }).ToArray();
        Run(EditCommand.Create("EraseInk", fields), ReplaceCommand(changed, a), LabelOf(changed, "erased"));
        return true;
    }

    private static InkStroke ToInkStroke(InkStrokeData s, int index, InkStyle style)
    {
        var samples = new InkSample[s.PointCount];
        for (var i = 0; i < samples.Length; i++)
        {
            var ratio = style.BaseWidth > 0 ? Math.Clamp(s.Widths[i] / style.BaseWidth, 0, 1) : 1;
            var normal = style.MinWidthRatio >= 1 ? 1 : Math.Clamp((ratio - style.MinWidthRatio) / (1 - style.MinWidthRatio), 0, 1);
            samples[i] = new InkSample(s.Xy[2 * i], s.Xy[(2 * i) + 1], (float)normal);
        }

        return new InkStroke($"s{index}", samples);
    }

    // ---- undo and redo ----

    /// <summary>Undoes the latest command in force. After a save this is a change relative to the saved file.</summary>
    public UndoOutcome Undo()
    {
        _nudge = null;
        _batch.CloseBatch(BatchCloseReason.AfterUndo);
        _inkBatchId = null;
        return UndoCore();
    }

    private UndoOutcome UndoCore()
    {
        var outcome = Model.Undo();
        if (outcome != UndoOutcome.Done)
        {
            return outcome;
        }

        var change = _done[^1];
        _done.RemoveAt(_done.Count - 1);
        if (change.ManualBackward is { } manual)
        {
            manual(Store);
        }
        else
        {
            MarkupReplay.Apply(Store, change.Inverse!);
        }

        _undone.Push(change);
        return outcome;
    }

    public UndoOutcome Redo()
    {
        _nudge = null;
        var outcome = Model.Redo();
        if (outcome != UndoOutcome.Done)
        {
            return outcome;
        }

        var change = _undone.Pop();
        MarkupReplay.Apply(Store, change.Forward);
        _done.Add(change);
        return outcome;
    }

    /// <summary>Brings the store up to date with commands read back from a journal after a crash (they are already in the edit model).</summary>
    public void Replay(IEnumerable<EditCommand> commands)
    {
        foreach (var command in commands)
        {
            MarkupReplay.Apply(Store, command);
        }
    }

    // ---- plumbing ----

    private void Run(EditCommand forward, EditCommand? inverse, string label, Action<AnnotationStore>? backward = null)
    {
        LastReason = null;
        var seq = Model.Apply(forward, inverse);
        MarkupReplay.Apply(Store, forward);
        _undone.Clear();
        _done.Add(new Change(seq, forward, inverse, backward, label));
    }

    // For a command whose effect on the store came first (an ink stroke needs its result to know its inverse).
    private void RecordApplied(EditCommand forward, EditCommand inverse, string label)
    {
        LastReason = null;
        var seq = Model.Apply(forward, inverse);
        _undone.Clear();
        _done.Add(new Change(seq, forward, inverse, null, label));
    }

    private bool Allowed(Annotation a, AnnotationOperation operation)
    {
        var check = AnnotationRules.Check(a, operation, Factory.Author);
        LastReason = check.Allowed ? null : check.ReasonKey;
        return check.Allowed;
    }

    private Annotation? Refuse(string key)
    {
        LastReason = key;
        return null;
    }

    private static bool CanBeParent(Annotation parent) => parent.IsOwned || !parent.Id.StartsWith("session:", StringComparison.Ordinal);

    private static bool Targetable(Annotation a) => a.ObjectNumber != 0 || !a.Id.StartsWith("session:", StringComparison.Ordinal);

    private static string LabelOf(Annotation a, string what) => $"{a.Kind} {what}";

    // ---- the commands ----

    private static double[] Rect(PdfRect r) => [r.Left, r.Bottom, r.Right, r.Top];

    private static Dictionary<string, object?> Target(Annotation a)
    {
        var fields = new Dictionary<string, object?>
        {
            ["page"] = a.Page,
            ["name"] = a.Id,
            ["subtype"] = a.Subtype,
            ["rect"] = Rect(a.FileRect),
        };
        if (a.ObjectNumber != 0)
        {
            fields["object"] = a.ObjectNumber;
        }

        return fields;
    }

    private static Dictionary<string, object?> ReplaceFields(Annotation expected, Annotation replacement)
    {
        var fields = Target(expected);
        fields["newRect"] = Rect(replacement.FileRect);
        fields["replace"] = AnnotationFields.ToFields(replacement);
        return fields;
    }

    private static EditCommand AddCommand(Annotation a, int? at = null)
    {
        var fields = AnnotationFields.ToFields(a);
        if (at is { } index)
        {
            fields["at"] = index;
        }

        var cmd = a.ReplyKind switch { ReplyKind.Reply => "AddReply", ReplyKind.State => "SetReviewState", _ => "AddAnnotation" };
        return EditCommand.Create(cmd, fields);
    }

    private static EditCommand DeleteCommand(Annotation a) => EditCommand.Create("DeleteAnnotation", Target(a));

    private static EditCommand ReplaceCommand(Annotation expected, Annotation replacement) =>
        EditCommand.Create("UpdateAnnotation", ReplaceFields(expected, replacement));

    private static EditCommand RectCommand(Annotation expected, PdfRect newRect)
    {
        var fields = Target(expected);
        fields["newRect"] = Rect(newRect);
        return EditCommand.Create("MoveAnnotation", fields);
    }

    private static EditCommand ContentsCommand(Annotation expected, string contents)
    {
        var fields = Target(expected);
        fields["contents"] = contents;
        return EditCommand.Create("UpdateAnnotation", fields);
    }

    private static EditCommand Composite(IEnumerable<EditCommand> commands) =>
        EditCommand.Create("CompositeAnnotation", new Dictionary<string, object?>
        {
            ["commands"] = commands.Select(static c => new Dictionary<string, object?> { ["c"] = c.Cmd, ["f"] = c.Payload }).ToArray(),
        });

    private sealed class TimeClock(TimeProvider time) : IInkClock
    {
        public long NowMs => time.GetTimestamp() * 1000 / time.TimestampFrequency;
    }
}
