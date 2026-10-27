namespace S7.Core.Engine;

// Plain data that crosses the (simulated) engine boundary. The UIA layer sees only these types.
// In production this is what the engine process returns over IPC (GetCharBoxes + text + struct tree).

public readonly record struct UserRect(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;
}

public sealed record PdfChar(
    int Index,            // PDFium char index (stable for the loaded text page)
    string Text,          // 1 UTF-16 unit, or a surrogate pair; "" if PDFium reported no Unicode
    UserRect Box,         // FPDFText_GetCharBox: tight glyph box, PDF user space (points, origin bottom-left, NOT CropBox-relative)
    UserRect Loose,       // FPDFText_GetLooseCharBox: font-metric box (consistent height per line), same space
    bool Generated,       // FPDFText_IsGenerated (space/CRLF inserted by PDFium)
    bool Hyphen,          // FPDFText_IsHyphen (line-end hyphen that PDFium replaced with U+0002)
    int Mcid,             // marked-content id of the owning text object, -1 if none (artifact / untagged)
    double FontSize,
    float Angle);

public sealed record StructBlock(string Type, IReadOnlyList<int> Mcids, string? Alt, string? ActualText);

public sealed class PageBox
{
    public required UserRect Media { get; init; }
    public required UserRect Crop { get; init; }   // effective crop box (CropBox if present else MediaBox, as PDFium reports it)
    public required int Rotate { get; init; }      // /Rotate normalised to 0, 90, 180, 270
}

public sealed class PageTextData
{
    public required int PageIndex { get; init; }
    public required PageBox Box { get; init; }
    public required IReadOnlyList<PdfChar> Chars { get; init; }
    public required bool IsTagged { get; init; }
    public required IReadOnlyList<StructBlock> StructBlocks { get; init; }
    /// <summary>The raw string FPDFText_GetText returns for the whole page, for comparison in the report.</summary>
    public required string PdfiumRawText { get; init; }
}

public sealed class RenderedPage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Bgra { get; init; }    // Width*Height*4, straight BGRA (white opaque background)
}
