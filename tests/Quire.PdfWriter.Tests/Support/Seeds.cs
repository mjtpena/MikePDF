namespace Quire.PdfWriter.Tests.Support;

/// <summary>The seed corpus for randomised tests: generated files in every layout, multi-revision chains and the small PDFium fixtures.</summary>
internal static class Seeds
{
    public static List<(string Name, byte[] Bytes)> Build(int maxFixtureBytes = 12_000)
    {
        var seeds = new List<(string, byte[])>();
        foreach (string layout in new[] { "classic", "xref-stream objstm flate+predictor", "xref-stream objstm plain", "xref-stream no-objstm", "hybrid" })
        {
            byte[] bytes = DocumentTests.Build(layout).Bytes;
            seeds.Add((layout, bytes));
            byte[] updated = DocInfo.AppendAnnotations(DocInfo.Strict(bytes), 2);
            seeds.Add((layout + " +1 revision", updated));
            seeds.Add((layout + " +2 revisions", DocInfo.AppendAnnotations(DocInfo.Strict(updated))));
        }

        foreach (var e in FixtureTests.Expectations)
        {
            byte[] bytes = FixtureTests.Read(e.File);
            if (bytes.Length <= maxFixtureBytes)
                seeds.Add((e.File, bytes));
        }

        return seeds;
    }
}
