using MikePDF.PdfWriter.Assembly;

namespace MikePDF.PdfWriter.Tests.Support;

/// <summary>Source profiles, read once, and the comparisons between an assembled file and the sources it came from.</summary>
internal static class AssemblyChecks
{
    private static readonly Dictionary<string, PdfiumProfile> Profiles = [];

    /// <summary>What PDFium sees of a fixture, read once.</summary>
    public static PdfiumProfile SourceProfile(string file)
    {
        lock (Profiles)
        {
            if (!Profiles.TryGetValue(file, out var profile))
            {
                profile = AssemblyFixtures.Profile(AssemblyFixtures.Read(file));
                Profiles[file] = profile;
            }

            return profile;
        }
    }

    public static PdfiumProfile SourceProfile(byte[] bytes) => AssemblyFixtures.Profile(bytes);

    /// <summary>
    /// Every link of every output page leads where the same link led in its source, and a link that led to a page that is not in the output is gone:
    /// the output page's links are its source page's links, in order, minus those whose target is not an output page (and minus dead ones).
    /// </summary>
    public static void AssertLinksFollow(PdfiumProfile output, params PdfiumProfile[] sources)
    {
        var included = output.Keys.Where(static k => k is not null).Select(static k => k!).ToHashSet();
        foreach (var page in output.Pages)
        {
            var origin = sources.SelectMany(static s => s.Pages).First(p => p.Key == page.Key);
            var expected = origin.Links.Where(l => l == "uri" || included.Contains(l)).ToList();
            Assert.True(expected.SequenceEqual(page.Links), $"{page.Key}: links [{string.Join(", ", page.Links)}], expected [{string.Join(", ", expected)}]");
        }
    }

    /// <summary>Every page keeps its size, boxes, rotation and rendering (same engine) from the source page with the same marker.</summary>
    public static void AssertPagesLookTheSame(PdfiumProfile output, params PdfiumProfile[] sources)
    {
        foreach (var page in output.Pages)
        {
            var origin = sources.SelectMany(static s => s.Pages).First(p => p.Key == page.Key);
            Assert.Equal((origin.Width, origin.Height, origin.Rotation, origin.Media, origin.Crop), (page.Width, page.Height, page.Rotation, page.Media, page.Crop));
            Assert.Equal(origin.RenderHash, page.RenderHash);
        }
    }

    public static IReadOnlyList<(string Title, int Depth, string Target)> Outline(PdfiumProfile profile) =>
        [.. profile.Outline.Select(static b => (b.Title, b.Depth, b.Target))];

    /// <summary>Strict reading of the output's catalog.</summary>
    public static PdfDictionary Catalog(StrictDocument document) => (PdfDictionary)document.GetObject(document.Root!.Number).Value;

    public static PdfObject? Resolve(StrictDocument document, PdfObject? value) => document.Resolve(value).Value is PdfNull ? null : document.Resolve(value).Value;

    /// <summary>Names of the root fields in the output's /AcroForm /Fields, as text.</summary>
    public static List<string> RootFieldNames(StrictDocument document)
    {
        var names = new List<string>();
        if (Resolve(document, Catalog(document)["AcroForm"]) is not PdfDictionary form || Resolve(document, form["Fields"]) is not PdfArray fields)
            return names;
        foreach (var field in fields)
        {
            if (Resolve(document, field) is PdfDictionary dictionary && Resolve(document, dictionary["T"]) is PdfString title)
                names.Add(System.Text.Encoding.Latin1.GetString(title.Value));
        }

        return names;
    }

    /// <summary>No orphans: every widget on a page has a /Parent chain that ends in a field listed in /AcroForm /Fields (or is itself listed), and every listed field has a widget below it.</summary>
    public static void AssertFormIsConsistent(StrictDocument document)
    {
        var listed = new HashSet<int>();
        if (Resolve(document, Catalog(document)["AcroForm"]) is PdfDictionary form && Resolve(document, form["Fields"]) is PdfArray fields)
        {
            foreach (var field in fields.OfType<PdfReference>())
                listed.Add(field.Number);
        }

        var reachedRoots = new HashSet<int>();
        foreach (var (_, page) in DocInfo.Pages(document))
        {
            if (Resolve(document, page["Annots"]) is not PdfArray annots)
                continue;
            foreach (var annot in annots.OfType<PdfReference>())
            {
                if (Resolve(document, annot) is not PdfDictionary dictionary || dictionary.GetName("Subtype") != "Widget")
                    continue;
                var current = annot;
                var dict = dictionary;
                for (var depth = 0; depth < 32 && dict["Parent"] is PdfReference parent; depth++)
                {
                    current = parent;
                    dict = (PdfDictionary)Resolve(document, parent)!;
                }

                Assert.Contains(current.Number, listed);
                reachedRoots.Add(current.Number);
            }
        }

        Assert.Equal(listed.Order(), reachedRoots.Order());
    }

    /// <summary>The output reads in PDFium with the same page count, and every page found its marker.</summary>
    public static PdfiumProfile AssertOpensInFreshEngine(AssemblyOutput output)
    {
        var profile = AssemblyFixtures.Profile(output.Bytes.ToArray());
        Assert.Equal(output.PageCount, profile.PageCount);
        return profile;
    }
}
