using MikePDF.PdfWriter.Editing;
using MikePDF.Protocol.Edits;

namespace MikePDF.WriterHost;

/// <summary>
/// The one place a decoded edit blob becomes the writer's own terms. The fuzz harness and the integration tests compile this file too (as a linked
/// source file: the host is a self-contained executable and cannot be referenced), so a field added to the blob cannot be forgotten in a copy.
/// </summary>
internal static class EditConversion
{
    internal static AnnotationChange ToChange(AnnotationEdit edit) => new()
    {
        Kind = (AnnotationEditKind)(int)edit.Operation,
        Page = (int)edit.Page,
        Annotation = (NewAnnotationKind)(int)edit.Kind,
        Rect = edit.Rect,
        Color = edit.Color,
        Opacity = edit.Opacity,
        Width = edit.Width,
        Contents = edit.Contents,
        Author = edit.Author,
        Name = edit.Name,
        Flags = edit.Flags,
        QuadPoints = edit.QuadPoints,
        Strokes = edit.Strokes,
        TargetObject = (int)edit.TargetObject,
        ExpectSubtype = edit.ExpectSubtype,
        ExpectRect = edit.ExpectRect,
        InteriorColor = edit.InteriorColor,
        Subject = edit.Subject,
        Created = edit.Created,
        Modified = edit.Modified,
        Dash = (int)edit.Dash,
        Vertices = edit.Vertices,
        LineStart = (int)edit.LineStart,
        LineEnd = (int)edit.LineEnd,
        StandardName = edit.StandardName,
        Traits = (uint)edit.Traits,
        FontId = edit.FontId,
        FontSize = edit.FontSize,
        TextColor = edit.TextColor,
        Align = edit.Align,
        InkWidths = edit.InkWidths,
        ParentName = edit.ParentName,
        ReplyKind = edit.ReplyKind,
        State = edit.State,
        PopupRect = edit.PopupRect,
    };
}
