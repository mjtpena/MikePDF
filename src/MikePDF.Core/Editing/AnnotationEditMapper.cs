using System.Text.Json;
using MikePDF.Ink;
using MikePDF.Protocol.Edits;

namespace MikePDF.Core.Editing;

/// <summary>A pending command the save pipeline of this build cannot write (an annotation command no stage 4 generator exists for, or fields that do not fit).</summary>
public sealed class UnsupportedEditException(string command, string reason) : InvalidOperationException($"{command}: {reason}")
{
    public string Command { get; } = command;
}

/// <summary>
/// Turns the pending commands of a save snapshot into the edit blob stage 4 reads (save-pipeline 11.3). The net effect is written, not the history:
/// an annotation added and then deleted in the same snapshot is not written at all, an annotation added and then moved is written once at its final
/// place, and several changes to one loaded annotation become one update that still carries the expectation from before the first.
/// <para>
/// The fields the commands carry are flat JSON (annotation-core 9 and the kind specs, as built by <c>MikePDF.Markup</c>'s <c>AnnotationFields</c>):
/// <c>AddAnnotation</c> {page, kind, name, rect, color, opacity?, width?, contents?, author?, flags?, subj?, created?, modified?, ic?, dash?, quads?,
/// strokes?, widths?, vertices?, le?, icon?, stamp?, turns?, typewriter?, highlighter?, family?, bold?, italic?, size?, tcolor?, align?, popup?},
/// <c>AddReply</c> and <c>SetReviewState</c> (the same fields plus {parent, state?}), <c>AddInkStroke</c> (a stroke of a batch, folded into the batch's
/// annotation), <c>DeleteAnnotation</c> {page, name | object, subtype, rect}, and <c>UpdateAnnotation</c>, <c>MoveAnnotation</c>,
/// <c>ResizeAnnotation</c>, <c>EraseInk</c> {page, name | object, subtype, rect, newRect?, contents?, replace?} where <c>replace</c> holds the
/// annotation's complete new fields (a restyle, a vertex edit, an erase): for a loaded annotation it becomes a Replace, for one added in the same
/// snapshot it rewrites the Add. <c>CompositeAnnotation</c> {commands: [{c, f}]} groups several as one command. A page is an ordinal in the document the
/// snapshot was taken against.
/// </para>
/// </summary>
public static class AnnotationEditMapper
{
    private sealed class Slot(AnnotationEdit edit)
    {
        public AnnotationEdit Edit { get; set; } = edit;

        /// <summary>The strokes of an ink batch assembled from <c>AddInkStroke</c> commands, with the style they were drawn in.</summary>
        public InkBatchBuild? Ink { get; set; }
    }

    private sealed class InkBatchBuild(InkStyle style)
    {
        public InkStyle Style { get; } = style;

        public List<InkSample[]> Strokes { get; } = [];
    }

    public static EditBlob ToBlob(IReadOnlyList<EditCommand> pending, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var edits = new List<Slot>();
        var added = new Dictionary<(int Page, string Name), Slot>();
        var loaded = new Dictionary<(int Page, long Object, string? Name), Slot>();

        foreach (var (cmd, p) in Flatten(pending))
        {
            try
            {
                switch (cmd)
                {
                    case "AddAnnotation" or "AddReply" or "SetReviewState":
                        var edit = ReadAdd(p, cmd);
                        var slot = new Slot(edit);
                        edits.Add(slot);
                        added[((int)edit.Page, edit.Name!)] = slot;
                        break;
                    case "AddInkStroke":
                        AddInkStroke(p, edits, added);
                        break;
                    case "DeleteAnnotation":
                        var page = Int(p, "page");
                        var name = Str(p, "name");
                        if (name is not null && added.Remove((page, name), out var cancelled))
                        {
                            edits.Remove(cancelled);
                            break;
                        }

                        var deleteKey = (page, Long(p, "object") ?? 0, name);
                        if (loaded.TryGetValue(deleteKey, out var existing))
                        {
                            existing.Edit = ReadTarget(p, EditOperation.Delete, existing.Edit);
                        }
                        else
                        {
                            var fresh = new Slot(ReadTarget(p, EditOperation.Delete, null));
                            loaded[deleteKey] = fresh;
                            edits.Add(fresh);
                        }

                        break;
                    case "UpdateAnnotation" or "MoveAnnotation" or "ResizeAnnotation" or "EraseInk":
                        var updatePage = Int(p, "page");
                        var updateName = Str(p, "name") ?? Str(p, "annotation");
                        if (updateName is not null && added.TryGetValue((updatePage, updateName), out var own))
                        {
                            own.Edit = Revise(own.Edit, p, cmd);
                            own.Ink = null;
                            break;
                        }

                        var updateKey = (updatePage, Long(p, "object") ?? 0, updateName);
                        if (loaded.TryGetValue(updateKey, out var again))
                        {
                            again.Edit = again.Edit.Operation == EditOperation.Delete ? again.Edit : Merge(again.Edit, p);
                        }
                        else
                        {
                            var fresh = new Slot(ReadTarget(p, EditOperation.Update, null));
                            if (p.TryGetProperty("replace", out var replacement) && replacement.ValueKind == JsonValueKind.Object)
                            {
                                fresh.Edit = Replaced(fresh.Edit, replacement);
                            }

                            loaded[updateKey] = fresh;
                            edits.Add(fresh);
                        }

                        break;
                    default:
                        throw new UnsupportedEditException(cmd, "no stage 4 generator for this command yet");
                }
            }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException or InkLimitException && e is not UnsupportedEditException)
            {
                throw new UnsupportedEditException(cmd, "its fields are missing or out of range");
            }
        }

        return new EditBlob { PageCount = (uint)pageCount, Edits = [.. edits.Select(static s => s.Edit)] };
    }

    /// <summary>The commands in order, with a composite replaced by what it holds (one level: a composite inside a composite is refused).</summary>
    private static IEnumerable<(string Cmd, JsonElement Fields)> Flatten(IReadOnlyList<EditCommand> pending)
    {
        foreach (var command in pending)
        {
            if (command.Cmd != "CompositeAnnotation")
            {
                yield return (command.Cmd, command.Payload);
                continue;
            }

            if (!command.Payload.TryGetProperty("commands", out var inner) || inner.ValueKind != JsonValueKind.Array)
            {
                throw new UnsupportedEditException(command.Cmd, "its fields are missing or out of range");
            }

            foreach (var item in inner.EnumerateArray())
            {
                var name = item.TryGetProperty("c", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                if (name is null || name == "CompositeAnnotation" || !item.TryGetProperty("f", out var fields) || fields.ValueKind != JsonValueKind.Object)
                {
                    throw new UnsupportedEditException(command.Cmd, "its fields are missing or out of range");
                }

                yield return (name, fields);
            }
        }
    }

    private static AnnotationEdit ReadAdd(JsonElement p, string cmd)
    {
        var kindName = Str(p, "kind") ?? throw new KeyNotFoundException("kind");
        var kind = Enum.TryParse<AnnotationKind>(kindName, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) ? parsed : throw new FormatException("kind");
        var reply = (ushort)(Long(p, "reply") ?? (cmd == "AddReply" ? 1 : cmd == "SetReviewState" ? 2 : 0));
        var typewriter = Bool(p, "typewriter");
        var traits = (typewriter ? EditTraits.Typewriter : EditTraits.None)
            | (Bool(p, "highlighter") ? EditTraits.Highlighter : EditTraits.None)
            | (EditTraits)(((uint)(Long(p, "turns") ?? 0) & 3u) << EditTraitBits.TurnsShift);
        var le = Strings(p, "le");
        return new AnnotationEdit
        {
            Operation = EditOperation.Add,
            Page = (uint)Int(p, "page"),
            Kind = kind,
            Rect = Floats(p, "rect") ?? throw new KeyNotFoundException("rect"),
            Color = Floats(p, "color") ?? throw new KeyNotFoundException("color"),
            Opacity = Float(p, "opacity") ?? 1f,
            Width = Float(p, "width") ?? 1f,
            Contents = Str(p, "contents"),
            Author = Str(p, "author"),
            Name = Str(p, "name") ?? throw new KeyNotFoundException("name"),
            Flags = (uint)(Long(p, "flags") ?? 0),
            QuadPoints = Floats(p, "quads"),
            Strokes = FloatLists(p, "strokes"),
            InteriorColor = Floats(p, "ic"),
            Subject = Str(p, "subj"),
            Created = Str(p, "created"),
            Modified = Str(p, "modified"),
            Dash = Str(p, "dash") switch { "dashed" => DashKind.Dashed, "dotted" => DashKind.Dotted, _ => DashKind.Solid },
            Vertices = Floats(p, "vertices"),
            LineStart = le is { Length: 2 } ? Ending(le[0]) : LineEndKind.None,
            LineEnd = le is { Length: 2 } ? Ending(le[1]) : LineEndKind.None,
            StandardName = kind == AnnotationKind.Stamp ? Str(p, "stamp") : kind == AnnotationKind.Text ? Str(p, "icon") : null,
            Traits = traits,
            FontId = kind == AnnotationKind.FreeText ? FontId(Str(p, "family"), Bool(p, "bold"), Bool(p, "italic")) : (ushort)0,
            FontSize = kind == AnnotationKind.FreeText ? Float(p, "size") ?? 12f : 0f,
            TextColor = Floats(p, "tcolor"),
            Align = Str(p, "align") switch { "center" => (ushort)1, "right" => (ushort)2, _ => (ushort)0 },
            InkWidths = kind == AnnotationKind.Ink ? FloatLists(p, "widths") : null,
            ParentName = Str(p, "parent"),
            ReplyKind = reply,
            State = reply == 2 ? State(Str(p, "state")) : (ushort)0,
            PopupRect = Floats(p, "popup"),
        };
    }

    // ---- ink: strokes arrive one command at a time and are folded into the batch's annotation ----

    private static void AddInkStroke(JsonElement p, List<Slot> edits, Dictionary<(int Page, string Name), Slot> added)
    {
        var page = Int(p, "page");
        var batch = Str(p, "annotation") ?? throw new KeyNotFoundException("annotation");
        var stroke = p.GetProperty("stroke");
        var data = stroke.GetProperty("data").GetString() ?? throw new KeyNotFoundException("data");
        if (!StrokeCodec.TryFromBase64(data, out var samples, out _))
        {
            throw new FormatException("stroke data");
        }

        if (!added.TryGetValue((page, batch), out var slot))
        {
            if (!Bool(p, "newBatch"))
            {
                throw new UnsupportedEditException("AddInkStroke", "a stroke added to a batch the file already holds");
            }

            var style = new InkStyle(
                Str(p, "tool") == "highlighter" ? InkTool.Highlighter : InkTool.Pen,
                Float(p, "width") ?? InkConstants.DefaultPenWidth,
                Str(p, "curve") switch { "soft" => PressureCurve.Soft, "firm" => PressureCurve.Firm, _ => PressureCurve.Linear },
                Float(p, "minRatio") ?? InkConstants.DefaultMinWidthRatio).Clamped();
            slot = new Slot(new AnnotationEdit
            {
                Operation = EditOperation.Add,
                Page = (uint)page,
                Kind = AnnotationKind.Ink,
                Name = batch,
                Color = Floats(p, "color") ?? throw new KeyNotFoundException("color"),
                Opacity = Float(p, "opacity") ?? 1f,
                Width = style.BaseWidth,
                Author = Str(p, "author"),
                Subject = Str(p, "subj"),
                Created = Str(p, "created"),
                Modified = Str(p, "modified"),
                Traits = style.Tool == InkTool.Highlighter ? EditTraits.Highlighter : EditTraits.None,
                Rect = [0, 0, 0, 0],
            })
            {
                Ink = new InkBatchBuild(style),
            };
            edits.Add(slot);
            added[(page, batch)] = slot;
        }

        if (slot.Ink is not { } build)
        {
            throw new UnsupportedEditException("AddInkStroke", "the batch was changed by another command first");
        }

        build.Strokes.Add(samples);
        slot.Edit = BuildInk(slot.Edit, build);
    }

    private static AnnotationEdit BuildInk(AnnotationEdit edit, InkBatchBuild build)
    {
        var simplified = build.Strokes.Select(s => Simplifier.Simplify(s, build.Style)).ToArray();
        var appearance = InkAppearance.Build(simplified);
        var pad = 0.5;
        return edit with
        {
            Rect = [(float)(appearance.Left - pad), (float)(appearance.Bottom - pad), (float)(appearance.Right + pad), (float)(appearance.Top + pad)],
            Strokes = [.. simplified.Select(static s => s.Xy)],
            InkWidths = [.. simplified.Select(static s => s.Widths)],
        };
    }

    private static AnnotationEdit ReadTarget(JsonElement p, EditOperation operation, AnnotationEdit? first)
    {
        var current = new AnnotationEdit
        {
            Operation = operation,
            Page = (uint)Int(p, "page"),
            Name = Str(p, "name") ?? Str(p, "annotation"),
            TargetObject = (uint)(Long(p, "object") ?? 0),
            ExpectSubtype = Str(p, "subtype") ?? throw new KeyNotFoundException("subtype"),
            ExpectRect = Floats(p, "rect") ?? throw new KeyNotFoundException("rect"),
            Rect = Floats(p, "newRect"),
            Contents = Str(p, "contents"),
        };
        if (first is null)
        {
            return current;
        }

        // A later command on a target that already has an edit keeps what the file was expected to contain, and the newest values.
        return first with
        {
            Operation = operation == EditOperation.Delete ? EditOperation.Delete : first.Operation,
            Rect = current.Rect ?? first.Rect,
            Contents = current.Contents ?? first.Contents,
        };
    }

    /// <summary>A change to a loaded annotation: a restyle is a Replace that carries the target's expectation and the new fields.</summary>
    private static AnnotationEdit Replaced(AnnotationEdit target, JsonElement replacement)
    {
        var full = ReadAdd(replacement, "AddAnnotation");
        return full with
        {
            Operation = EditOperation.Replace,
            Page = target.Page,
            Name = target.Name ?? full.Name,
            TargetObject = target.TargetObject,
            ExpectSubtype = target.ExpectSubtype,
            ExpectRect = target.ExpectRect,
        };
    }

    private static AnnotationEdit Merge(AnnotationEdit first, JsonElement p)
    {
        if (p.TryGetProperty("replace", out var replacement) && replacement.ValueKind == JsonValueKind.Object)
        {
            return Replaced(first, replacement);
        }

        return first with
        {
            Operation = first.Operation == EditOperation.Replace ? EditOperation.Replace : EditOperation.Update,
            Rect = Floats(p, "newRect") ?? first.Rect,
            Contents = Str(p, "contents") ?? first.Contents,
        };
    }

    /// <summary>A change to an annotation added in this same snapshot: it is written once, in its final state.</summary>
    private static AnnotationEdit Revise(AnnotationEdit add, JsonElement p, string cmd)
    {
        if (p.TryGetProperty("replace", out var replacement) && replacement.ValueKind == JsonValueKind.Object)
        {
            return ReadAdd(replacement, add.ReplyKind == 1 ? "AddReply" : add.ReplyKind == 2 ? "SetReviewState" : "AddAnnotation") with { Page = add.Page, Name = add.Name };
        }

        if (cmd == "EraseInk")
        {
            throw new UnsupportedEditException(cmd, "an erase carries the annotation's new fields");
        }

        return add with
        {
            Rect = Floats(p, "newRect") ?? add.Rect,
            Contents = Str(p, "contents") ?? add.Contents,
        };
    }

    private static LineEndKind Ending(string name) => Enum.TryParse<LineEndKind>(name, ignoreCase: false, out var kind) && Enum.IsDefined(kind) ? kind : throw new FormatException("line ending");

    private static ushort State(string? name) => name switch
    {
        null or "None" => 0,
        "Accepted" => 1,
        "Rejected" => 2,
        "Cancelled" => 3,
        "Completed" => 4,
        _ => throw new FormatException("state"),
    };

    private static ushort FontId(string? family, bool bold, bool italic)
    {
        var f = family switch { "Serif" => 1, "Mono" => 2, _ => 0 };
        return (ushort)((f * 4) + (bold ? 1 : 0) + (italic ? 2 : 0));
    }

    private static int Int(JsonElement p, string name) => p.GetProperty(name).GetInt32();

    private static long? Long(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    private static float? Float(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;

    private static bool Bool(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Str(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string[]? Strings(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? [.. v.EnumerateArray().Select(static x => x.GetString() ?? string.Empty)] : null;

    private static float[]? Floats(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? [.. v.EnumerateArray().Select(static x => x.GetSingle())] : null;

    private static float[][]? FloatLists(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Select(static s => s.EnumerateArray().Select(static x => x.GetSingle()).ToArray())]
            : null;
}
