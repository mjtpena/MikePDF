using System.Text.RegularExpressions;

namespace MikePDF.Core.Tests;

/// <summary>
/// The handler DLL's strings (docs/features/shell-extensions.md section 12) live in its own resource file, because the DLL runs inside Explorer's hosts and cannot read the
/// app's .resw. The en-US text has to be the same in both places: this test reads the two and compares them.
/// </summary>
public sealed partial class ShellStringsTests
{
    private static readonly (string Define, string Key)[] Strings =
    [
        ("IDS_PREVIEW_NAME", "Shell_Preview_Name"),
        ("IDS_PREVIEW_VALUE", "Shell_Preview_Value"),
        ("IDS_PREVIEW_LOADING", "Shell_Preview_Loading"),
        ("IDS_PREVIEW_UNAVAILABLE", "Shell_Preview_Unavailable"),
        ("IDS_PREVIEW_PASSWORD", "Shell_Preview_Password"),
        ("IDS_PREVIEW_OFF", "Shell_Preview_Off"),
        ("IDS_PREVIEW_OFF_POLICY", "Shell_Preview_OffPolicy"),
    ];

    [GeneratedRegex("""^\s*(IDS_[A-Z_]+)\s+"((?:[^"]|"")*)"\s*$""", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ResourceLine();

    [Fact]
    public void TheHandlerResourcesCarryTheSameTextAsTheAppsResources()
    {
        var rc = File.ReadAllText(Path.Combine(ResourceText.RepoRoot(), "src", "MikePDF.ShellExt", "MikePDF.ShellExt.rc"));
        var inRc = ResourceLine().Matches(rc).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Replace("\"\"", "\""), StringComparer.Ordinal);

        foreach (var (define, key) in Strings)
        {
            Assert.True(inRc.TryGetValue(define, out var text), define + " is missing from MikePDF.ShellExt.rc");
            Assert.Equal(ResourceText.Get(key), text);
        }

        Assert.Equal(Strings.Length, inRc.Count);
    }

    [Fact]
    public void EveryStringIdIsDefinedOnceInTheHeader()
    {
        var header = File.ReadAllText(Path.Combine(ResourceText.RepoRoot(), "src", "MikePDF.ShellExt", "resource.h"));
        var ids = Regex.Matches(header, @"#define (IDS_[A-Z_]+) (\d+)").ToDictionary(m => m.Groups[1].Value, m => int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        foreach (var (define, _) in Strings)
        {
            Assert.True(ids.ContainsKey(define), define);
        }

        Assert.Equal(ids.Count, ids.Values.Distinct().Count());
    }

    [Fact]
    public void NoPreviewMessageNamesAPathOrAnythingFromTheDocument()
    {
        foreach (var (_, key) in Strings)
        {
            var text = ResourceText.Get(key);
            Assert.DoesNotContain("{2}", text, StringComparison.Ordinal);
            Assert.DoesNotContain(":\\", text, StringComparison.Ordinal);
        }
    }
}
