namespace MikePDF.Core.Tests;

/// <summary>
/// Master prompt 14: no telemetry and no network calls at all in this build; external links are not opened yet. These checks read the source of
/// the UI app and the packaging manifest, so a network call cannot slip in unnoticed.
/// </summary>
public sealed class PrivacyTests
{
    private static string RepoRoot()
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

    private static IEnumerable<string> Sources(string project) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", project), "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".cs", StringComparison.Ordinal) || p.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    [Theory]
    [InlineData("System.Net")]
    [InlineData("HttpClient")]
    [InlineData("WebClient")]
    [InlineData("WebRequest")]
    [InlineData("Socket")]
    [InlineData("WebView")]
    [InlineData("LaunchUriAsync")]
    [InlineData("Windows.Web")]
    [InlineData("Windows.Networking")]
    [InlineData("AppCenter")]
    [InlineData("Telemetry")]
    [InlineData("ApplicationInsights")]
    public void TheAppAndTheCoreContainNothingThatTalksToTheNetwork(string forbidden)
    {
        foreach (var project in new[] { "MikePDF.App", "MikePDF.Core" })
        {
            foreach (var file in Sources(project))
            {
                var text = File.ReadAllText(file);
                // The privacy statement in comments and strings may name these words; code may not use them.
                var offending = text.Split('\n').Where(l => l.Contains(forbidden, StringComparison.Ordinal) && !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.TrimStart().StartsWith("///", StringComparison.Ordinal) && !l.TrimStart().StartsWith('*') && !l.TrimStart().StartsWith("<!--", StringComparison.Ordinal)).ToList();
                Assert.True(offending.Count == 0, $"{Path.GetFileName(file)} mentions {forbidden}: {offending.FirstOrDefault()?.Trim()}");
            }
        }
    }

    [Fact]
    public void TheManifestDeclaresRunFullTrustAndNoNetworkCapability()
    {
        var manifest = File.ReadAllText(Path.Combine(RepoRoot(), "src", "MikePDF.App", "Package.appxmanifest"));
        Assert.Contains("Name=\"runFullTrust\"", manifest, StringComparison.Ordinal);
        foreach (var capability in new[] { "internetClient", "internetClientServer", "privateNetworkClientServer", "enterpriseAuthentication", "sharedUserCertificates", "documentsLibrary", "broadFileSystemAccess", "webcam", "microphone", "location" })
        {
            Assert.DoesNotContain($"Name=\"{capability}\"", manifest, StringComparison.Ordinal);
        }

        // No thumbnail or preview handlers in this build (ADR-0015 decision 6).
        Assert.DoesNotContain("ThumbnailHandler", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviewHandler", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAppDoesNotReferenceTheWriterHostOrPdfium()
    {
        var project = File.ReadAllText(Path.Combine(RepoRoot(), "src", "MikePDF.App", "MikePDF.App.csproj"));
        Assert.DoesNotContain("MikePDF.WriterHost", project, StringComparison.Ordinal);
        Assert.DoesNotContain("MikePDF.PdfWriter", project, StringComparison.Ordinal);
        foreach (var file in Sources("MikePDF.App"))
        {
            // PDFium is only ever reached through the sandboxed engine: no P/Invoke to it from the UI process (A.5).
            Assert.DoesNotContain("FPDF_", File.ReadAllText(file), StringComparison.Ordinal);
        }
    }
}
