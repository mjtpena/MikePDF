using System.Text.Json;
using MikePDF.Core.Editing;
using MikePDF.Ink;

namespace MikePDF.Markup.Editing;

/// <summary>
/// What an annotation command does to the model's store. The session applies its own commands and their inverses through this, and recovery applies
/// the commands it reads back from a journal through it, so a replayed document is the document that was being edited (annotation-core 6 "Engine
/// restarts", FT-13, INK-19). It understands the commands <c>AnnotationEditMapper</c> does; anything else is ignored (the forms and pages commands
/// are other owners').
/// </summary>
public static class MarkupReplay
{
    public static void Apply(AnnotationStore store, EditCommand command)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(command);
        Apply(store, command.Cmd, command.Payload);
    }

    public static void Apply(AnnotationStore store, string cmd, JsonElement p)
    {
        switch (cmd)
        {
            case "AddAnnotation" or "AddReply" or "SetReviewState":
            {
                var a = AnnotationFields.FromFields(p);
                a = cmd switch
                {
                    "AddReply" when a.ReplyKind == ReplyKind.None => a with { ReplyKind = ReplyKind.Reply },
                    "SetReviewState" when a.ReplyKind == ReplyKind.None => a with { ReplyKind = ReplyKind.State },
                    _ => a,
                };
                if (!store.Contains(a.Id))
                {
                    store.Add(a, p.TryGetProperty("at", out var at) && at.ValueKind == JsonValueKind.Number ? at.GetInt32() : null);
                }

                break;
            }

            case "DeleteAnnotation":
                if (Name(p) is { } deleted)
                {
                    store.Remove(deleted);
                }

                break;
            case "UpdateAnnotation" or "MoveAnnotation" or "ResizeAnnotation" or "EraseInk":
            {
                if (Name(p) is not { } id || store.Get(id) is not { } old)
                {
                    break;
                }

                if (p.TryGetProperty("replace", out var replacement) && replacement.ValueKind == JsonValueKind.Object)
                {
                    store.Replace(AnnotationFields.FromFields(replacement) with { Origin = old.Origin, ObjectNumber = old.ObjectNumber });
                    break;
                }

                var rect = p.TryGetProperty("newRect", out var r) && r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().Select(static x => x.GetDouble()).ToArray() : null;
                var contents = p.TryGetProperty("contents", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                store.Replace(old with
                {
                    Rect = rect is { Length: 4 } ? new PdfRect(rect[0], rect[1], rect[2], rect[3]) : old.Rect,
                    Contents = contents ?? old.Contents,
                });
                break;
            }

            case "AddInkStroke":
                AddInkStroke(store, p);
                break;
            case "CompositeAnnotation":
                if (p.TryGetProperty("commands", out var commands) && commands.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in commands.EnumerateArray())
                    {
                        Apply(store, item.GetProperty("c").GetString()!, item.GetProperty("f"));
                    }
                }

                break;
        }
    }

    private static string? Name(JsonElement p) =>
        p.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()
        : p.TryGetProperty("annotation", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()
        : null;

    private static void AddInkStroke(AnnotationStore store, JsonElement p)
    {
        var batch = p.GetProperty("annotation").GetString()!;
        if (!StrokeCodec.TryFromBase64(p.GetProperty("stroke").GetProperty("data").GetString() ?? string.Empty, out var samples, out _))
        {
            throw new FormatException("stroke data");
        }

        var style = InkStyleFrom(p);
        var simplified = Simplifier.Simplify(samples, style);
        var stroke = new InkStrokeData(simplified.Xy, simplified.Widths);
        if (store.Get(batch) is { Payload: InkPayload existing } old)
        {
            store.Replace(MarkupGeometry.Refit(old with { Payload = existing with { Strokes = [.. existing.Strokes, stroke] } }));
            return;
        }

        var colour = p.GetProperty("color").EnumerateArray().Select(static x => x.GetDouble()).ToArray();
        var a = new Annotation
        {
            Id = batch,
            Kind = MarkupKind.Ink,
            Page = p.GetProperty("page").GetInt32(),
            Rect = new PdfRect(0, 0, 0, 0),
            Color = Rgb.FromNumbers(colour),
            Opacity = p.TryGetProperty("opacity", out var o) ? o.GetDouble() : 1,
            Width = style.BaseWidth,
            Author = p.TryGetProperty("author", out var au) && au.ValueKind == JsonValueKind.String ? au.GetString()! : string.Empty,
            Subject = p.TryGetProperty("subj", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : MarkupDefaults.SubjectFor(MarkupKind.Ink),
            Created = p.TryGetProperty("created", out var cr) && PdfDates.TryParse(cr.GetString(), out var created) ? created : default,
            Modified = p.TryGetProperty("modified", out var mo) && PdfDates.TryParse(mo.GetString(), out var modified) ? modified : default,
            Payload = new InkPayload([stroke], style.Tool == InkTool.Highlighter),
        };
        store.Add(MarkupGeometry.Refit(a));
    }

    internal static InkStyle InkStyleFrom(JsonElement p) => new InkStyle(
        p.TryGetProperty("tool", out var tool) && tool.GetString() == "highlighter" ? InkTool.Highlighter : InkTool.Pen,
        p.TryGetProperty("width", out var w) ? (float)w.GetDouble() : InkConstants.DefaultPenWidth,
        p.TryGetProperty("curve", out var curve) ? curve.GetString() switch { "soft" => PressureCurve.Soft, "firm" => PressureCurve.Firm, _ => PressureCurve.Linear } : PressureCurve.Linear,
        p.TryGetProperty("minRatio", out var ratio) ? (float)ratio.GetDouble() : InkConstants.DefaultMinWidthRatio).Clamped();
}
