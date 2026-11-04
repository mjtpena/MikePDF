using MikePDF.PdfWriter.Assembly;

namespace MikePDF.PdfWriter.Tests.Support;

/// <summary>The assembler's test inputs and the checks every output goes through.</summary>
internal static class AssemblyFixtures
{
    public static readonly AssemblerOptions Fixed = new()
    {
        ModificationDate = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero),
        DocumentId = Enumerable.Range(0, 16).Select(static i => (byte)(0xC0 + i)).ToArray(),
    };

    public static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "assembly", name));

    public static AssemblerSource Source(string file) => new(Path.GetFileNameWithoutExtension(file), Read(file));

    public static AssemblerSource Source(string name, byte[] bytes) => new(name, bytes);

    /// <summary>The fixture rewritten with an xref stream and an object stream: the same document in the other layout.</summary>
    public static byte[] AsXrefStream(byte[] classic)
    {
        var document = DocInfo.Strict(classic);
        var pdf = MiniPdf.From(document);
        return pdf.WriteXrefStream(document.Root!.Number, (document.Trailer["Info"] as PdfReference)?.Number);
    }

    /// <summary>Assembles and fails the test with the refusal when it is refused.</summary>
    public static AssemblyOutput Assemble(IReadOnlyList<AssemblerSource> sources, AssemblyPlan plan, AssemblerOptions? options = null)
    {
        var result = PageAssembler.Assemble(sources, plan, options ?? Fixed);
        Assert.True(result.IsSuccess, result.IsSuccess ? "" : result.Refusal.ToString());
        return result.Output;
    }

    /// <summary>The checks every output must pass: it parses strictly, validates with no error and no dangling reference, and reads the planned page count.</summary>
    public static StrictDocument CheckStructure(AssemblyOutput output)
    {
        var bytes = output.Bytes.ToArray();
        var document = DocInfo.Strict(bytes);
        var report = StructureValidator.Validate(document, new ValidationOptions { DanglingReferencesAreErrors = true });
        Assert.True(report.IsValid, string.Join("; ", report.Issues.Where(static i => i.Severity == ValidationSeverity.Error).Take(5).Select(static i => i.Message)));
        Assert.Equal(output.PageCount, DocInfo.PageCount(document));
        return document;
    }

    public static PdfiumProfile Profile(byte[] bytes, bool render = true)
    {
        Assert.SkipWhen(Pdfium.UnavailableReason is not null, Pdfium.UnavailableReason ?? "");
        var profile = Pdfium.Profile(bytes, render);
        Assert.NotNull(profile);
        return profile!;
    }
}
