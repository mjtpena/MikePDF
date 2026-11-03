using System.Text.Json;
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
/// The fields the commands carry are the feature specs' to define; the ones understood here are those the writer can generate today:
/// <c>AddAnnotation</c> {page, kind, name, rect, color, opacity?, width?, contents?, author?, flags?, quads?, strokes?},
/// <c>DeleteAnnotation</c> {page, name | object, subtype, rect}, and <c>UpdateAnnotation</c>, <c>MoveAnnotation</c>, <c>ResizeAnnotation</c>
/// {page, name | object, subtype, rect, newRect?, contents?}. A page is an ordinal in the document the snapshot was taken against.
/// </para>
/// </summary>
public static class AnnotationEditMapper
{
    public static EditBlob ToBlob(IReadOnlyList<EditCommand> pending, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var edits = new List<Slot>();
        var added = new Dictionary<(int Page, string Name), Slot>();
        var loaded = new Dictionary<(int Page, long Object, string? Name), Slot>();

        foreach (var command in pending)
        {
            var p = command.Payload;
            try
            {
                switch (command.Cmd)
                {
                    case "AddAnnotation":
                        var edit = ReadAdd(p);
                        var slot = new Slot(edit);
                        edits.Add(slot);
                        added[((int)edit.Page, edit.Name!)] = slot;
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
                    case "UpdateAnnotation" or "MoveAnnotation" or "ResizeAnnotation":
                        var updatePage = Int(p, "page");
                        var updateName = Str(p, "name");
                        if (updateName is not null && added.TryGetValue((updatePage, updateName), out var own))
                        {
                            own.Edit = Revise(own.Edit, p);
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
                            loaded[updateKey] = fresh;
                            edits.Add(fresh);
                        }

                        break;
                    default:
                        throw new UnsupportedEditException(command.Cmd, "no stage 4 generator for this command yet");
                }
            }
            catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException && e is not UnsupportedEditException)
            {
                throw new UnsupportedEditException(command.Cmd, "its fields are missing or out of range");
            }
        }

        return new EditBlob { PageCount = (uint)pageCount, Edits = [.. edits.Select(static s => s.Edit)] };
    }

    private sealed class Slot(AnnotationEdit edit)
    {
        public AnnotationEdit Edit { get; set; } = edit;
    }

    private static AnnotationEdit ReadAdd(JsonElement p)
    {
        var kind = Str(p, "kind") ?? throw new KeyNotFoundException("kind");
        return new AnnotationEdit
        {
            Operation = EditOperation.Add,
            Page = (uint)Int(p, "page"),
            Kind = Enum.TryParse<AnnotationKind>(kind, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) ? parsed : throw new FormatException("kind"),
            Rect = Floats(p, "rect") ?? throw new KeyNotFoundException("rect"),
            Color = Floats(p, "color") ?? throw new KeyNotFoundException("color"),
            Opacity = Float(p, "opacity") ?? 1f,
            Width = Float(p, "width") ?? 1f,
            Contents = Str(p, "contents"),
            Author = Str(p, "author"),
            Name = Str(p, "name") ?? throw new KeyNotFoundException("name"),
            Flags = (uint)(Long(p, "flags") ?? 0),
            QuadPoints = Floats(p, "quads"),
            Strokes = p.TryGetProperty("strokes", out var strokes) && strokes.ValueKind == JsonValueKind.Array
                ? [.. strokes.EnumerateArray().Select(static s => s.EnumerateArray().Select(static v => v.GetSingle()).ToArray())]
                : null,
        };
    }

    private static AnnotationEdit ReadTarget(JsonElement p, EditOperation operation, AnnotationEdit? first)
    {
        var current = new AnnotationEdit
        {
            Operation = operation,
            Page = (uint)Int(p, "page"),
            Name = Str(p, "name"),
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
        return new AnnotationEdit
        {
            Operation = operation == EditOperation.Delete ? EditOperation.Delete : first.Operation,
            Page = first.Page,
            Name = first.Name,
            TargetObject = first.TargetObject,
            ExpectSubtype = first.ExpectSubtype,
            ExpectRect = first.ExpectRect,
            Rect = current.Rect ?? first.Rect,
            Contents = current.Contents ?? first.Contents,
        };
    }

    private static AnnotationEdit Merge(AnnotationEdit first, JsonElement p)
    {
        var rect = Floats(p, "newRect") ?? first.Rect;
        var contents = Str(p, "contents") ?? first.Contents;
        return new AnnotationEdit
        {
            Operation = EditOperation.Update,
            Page = first.Page,
            Name = first.Name,
            TargetObject = first.TargetObject,
            ExpectSubtype = first.ExpectSubtype,
            ExpectRect = first.ExpectRect,
            Rect = rect,
            Contents = contents,
        };
    }

    /// <summary>A change to an annotation added in this same snapshot: it is written once, in its final state.</summary>
    private static AnnotationEdit Revise(AnnotationEdit add, JsonElement p) => new()
    {
        Operation = EditOperation.Add,
        Page = add.Page,
        Kind = add.Kind,
        Rect = Floats(p, "newRect") ?? add.Rect,
        Color = add.Color,
        Opacity = add.Opacity,
        Width = add.Width,
        Contents = Str(p, "contents") ?? add.Contents,
        Author = add.Author,
        Name = add.Name,
        Flags = add.Flags,
        QuadPoints = add.QuadPoints,
        Strokes = add.Strokes,
    };

    private static int Int(JsonElement p, string name) => p.GetProperty(name).GetInt32();

    private static long? Long(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    private static float? Float(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;

    private static string? Str(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static float[]? Floats(JsonElement p, string name) =>
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? [.. v.EnumerateArray().Select(static x => x.GetSingle())] : null;
}
