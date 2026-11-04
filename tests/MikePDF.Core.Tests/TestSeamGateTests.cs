using System.Text.RegularExpressions;

namespace MikePDF.Core.Tests;

/// <summary>The one gate for the test seams (environment variables that steer the app for the journeys): none is read from a Store-signed package, and the list is the documented list.</summary>
public sealed class TestSeamGateTests
{
    private static string? Env(string name) => name switch
    {
        TestSeamGate.PolicyKey => @"Software\MikePDF.Tests\x",
        TestSeamGate.Restore => "Always",
        TestSeamGate.FakeDirty => "1",
        TestSeamGate.JumpListDump => "dump.txt",
        TestSeamGate.PresentationDump => "present.txt",
        TestSeamGate.PseudoRtl => "1",
        TestSeamGate.NoPrefetch => "1",
        TestSeamGate.PrintOutput => "out",
        TestSeamGate.SaveAsPath => "copy.pdf",
        "SOMETHING_ELSE" => "yes",
        _ => null,
    };

    [Theory]
    [InlineData(TestSeamGate.PolicyKey)]
    [InlineData(TestSeamGate.Restore)]
    [InlineData(TestSeamGate.FakeDirty)]
    [InlineData(TestSeamGate.JumpListDump)]
    [InlineData(TestSeamGate.PresentationDump)]
    [InlineData(TestSeamGate.PseudoRtl)]
    [InlineData(TestSeamGate.NoPrefetch)]
    [InlineData(TestSeamGate.PrintOutput)]
    [InlineData(TestSeamGate.SaveAsPath)]
    public void ASeamIsReadWhenThePackageIsNotStoreSigned(string name) => Assert.NotNull(TestSeamGate.Read(name, Env, storeSigned: false));

    [Theory]
    [InlineData(TestSeamGate.PolicyKey)]
    [InlineData(TestSeamGate.Restore)]
    [InlineData(TestSeamGate.FakeDirty)]
    [InlineData(TestSeamGate.JumpListDump)]
    [InlineData(TestSeamGate.PresentationDump)]
    [InlineData(TestSeamGate.PseudoRtl)]
    [InlineData(TestSeamGate.NoPrefetch)]
    [InlineData(TestSeamGate.PrintOutput)]
    [InlineData(TestSeamGate.SaveAsPath)]
    public void EverySeamIsIgnoredWhenThePackageIsStoreSignedWhateverTheEnvironmentSays(string name) => Assert.Null(TestSeamGate.Read(name, Env, storeSigned: true));

    [Fact]
    public void ANameThatIsNotASeamIsNeverReadAndAnEmptyValueIsNotSet()
    {
        Assert.Null(TestSeamGate.Read("SOMETHING_ELSE", Env, storeSigned: false));
        Assert.Null(TestSeamGate.Read(TestSeamGate.Restore, _ => string.Empty, storeSigned: false));
        Assert.Null(TestSeamGate.Read(TestSeamGate.Restore, _ => null, storeSigned: false));
    }

    [Fact]
    public void TheAppReadsNoTestVariableExceptThroughTheGate()
    {
        var app = Path.Combine(ResourceText.RepoRoot(), "src", "MikePDF.App");
        foreach (var file in Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            foreach (var line in File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.TrimStart().StartsWith("///", StringComparison.Ordinal)))
            {
                Assert.False(line.Contains("GetEnvironmentVariable", StringComparison.Ordinal) && line.Contains("MIKEPDF_TEST_", StringComparison.Ordinal), $"{Path.GetFileName(file)} reads a test seam directly: {line.Trim()}");
            }
        }
    }

    [Fact]
    public void TheDocsListEverySeamInOnePlace()
    {
        var docs = File.ReadAllText(Path.Combine(ResourceText.RepoRoot(), "docs", "features", "tabs-windows-session.md"));
        var section = docs[docs.IndexOf("## 16. Test seams", StringComparison.Ordinal)..];
        var listed = Regex.Matches(section, @"`(MIKEPDF_TEST_[A-Z_]+)`").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.Equal([.. TestSeamGate.All.Order(StringComparer.Ordinal)], [.. listed.Order(StringComparer.Ordinal)]);
    }
}
