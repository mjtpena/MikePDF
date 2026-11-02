using System.Xml.Linq;

namespace MikePDF.Core.Tests;

/// <summary>
/// The en-US UI text, read straight from the app's resources file, so that tests of text built in MikePDF.Core (disabled reasons, the policy report, the
/// palette rows) check the words a person sees, and a missing string fails a test.
/// </summary>
internal static class ResourceText
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Table = new(Load);

    public static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MikePDF.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("MikePDF.slnx not found above " + AppContext.BaseDirectory);
    }

    public static IReadOnlyDictionary<string, string> All => Table.Value;

    public static bool Has(string key) => Table.Value.ContainsKey(key);

    /// <summary>The string, or a marker that no test expects (so a missing string shows in the failure).</summary>
    public static string Get(string key) => Table.Value.TryGetValue(key, out var value) ? value : "[[" + key + "]]";

    private static Dictionary<string, string> Load()
    {
        var path = Path.Combine(RepoRoot(), "src", "MikePDF.App", "Strings", "en-US", "Resources.resw");
        var document = XDocument.Load(path);
        return document.Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!, StringComparer.Ordinal);
    }
}
