using S7.Core.Fixtures;

namespace A11yTest;

/// <summary>
/// Hand-written expected reading results, derived from how each fixture was AUTHORED (not from the model under test).
/// A line's expected text is its visible text; a paragraph is its lines joined by single spaces (hyphenated line breaks
/// are joined without the hyphen); paragraphs are separated by one newline in the document text.
/// </summary>
public sealed record Expect(string File, string[] Lines, string[] Paragraphs)
{
    public string FullText => string.Join("\n", Paragraphs);
    public string[] Words => FullText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static string J(IEnumerable<string> l) => string.Join(" ", l);

    public static IReadOnlyList<Expect> All { get; } = Build();

    private static IReadOnlyList<Expect> Build()
    {
        var left = FixtureGenerator.TwoColLeft.ToArray();
        var right = FixtureGenerator.TwoColRight.ToArray();
        string title = "A Study of Reading Order in Two Column Pages";
        return new[]
        {
            new Expect(FixtureGenerator.TaggedName,
                new[]
                {
                    "Quarterly Findings",
                    "The committee reviewed three proposals during",
                    "the spring session and approved two of them.",
                    "Funding was released in May. Delivery is",
                    "expected before the end of the year.",
                    "Page 1 of 1",
                },
                new[]
                {
                    "Quarterly Findings",
                    "The committee reviewed three proposals during the spring session and approved two of them.",
                    "Funding was released in May. Delivery is expected before the end of the year.",
                    "Page 1 of 1",
                }),
            new Expect(FixtureGenerator.TwoColName,
                new[] { title }.Concat(left.Where(x => x.Length > 0)).Concat(right.Where(x => x.Length > 0)).ToArray(),
                new[]
                {
                    title,
                    J(left.Take(6)), J(left.Skip(7)),
                    J(right.Take(6)), J(right.Skip(7)),
                }),
            new Expect(FixtureGenerator.RotateName,
                new[]
                {
                    "TOP LEFT",
                    "Rotated page with an offset crop box.",
                    "The page dictionary carries Rotate 90 and",
                    "a CropBox whose origin is not at zero.",
                    "Reading it correctly needs the transform.",
                    "BOTTOM RIGHT",
                },
                new[]
                {
                    "TOP LEFT",
                    "Rotated page with an offset crop box. The page dictionary carries Rotate 90 and a CropBox whose origin is not at zero. Reading it correctly needs the transform.",
                    "BOTTOM RIGHT",
                }),
            new Expect(FixtureGenerator.LigatureName,
                new[]
                {
                    "The official offline workflow is a well-known",
                    "case of finding flaws in the data.",
                    "This sentence is a hyphenated exam",
                    "ple of line breaking, and so is a sec",
                    "ond one that wraps again.",
                },
                new[]
                {
                    "The official offline workflow is a well-known case of finding flaws in the data.",
                    "This sentence is a hyphenated example of line breaking, and so is a second one that wraps again.",
                }),
        };
    }
}
