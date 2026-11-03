using MikePDF.Core.Editing;

namespace MikePDF.Core.Tests;

internal static class Edits
{
    public static EditCommand Add(int page = 0, string name = "n1", string text = "hello") =>
        EditCommand.Create("AddAnnotation", new { page, kind = "Highlight", name, contents = text });

    public static EditCommand Delete(int page = 0, string name = "n1") => EditCommand.Create("DeleteAnnotation", new { page, name });

    public static EditCommand Field(string field = "f1", string value = "v") => EditCommand.Create("SetFieldValue", new { field, value });

    public static EditCommand Rotate(int page = 0) => EditCommand.Create("RotatePages", new { pages = new[] { page }, degrees = 90 });
}
