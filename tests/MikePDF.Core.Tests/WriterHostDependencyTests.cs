using System.Xml.Linq;

namespace MikePDF.Core.Tests;

/// <summary>
/// The sandboxed writer host must not carry MikePDF.Core (the viewer-side library, with the document model, the scheduler and the engine client): it ships
/// self-contained and, in time, Native AOT, and everything it contains is attack surface. These checks read the project files and the sources, so a
/// reference cannot slip in directly or through another project.
/// </summary>
public sealed class WriterHostDependencyTests
{
    private static readonly string[] WriterSide = ["MikePDF.WriterHost", "MikePDF.PdfWriter", "MikePDF.Markup", "MikePDF.Ink", "MikePDF.Protocol"];

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

    private static IEnumerable<string> ProjectReferences(string project)
    {
        var path = Path.Combine(RepoRoot(), "src", project, project + ".csproj");
        var document = XDocument.Load(path);
        return document.Descendants("ProjectReference")
            .Select(static e => (string?)e.Attribute("Include"))
            .Where(static i => i is not null)
            .Select(static i => Path.GetFileNameWithoutExtension(i!.Replace('\\', '/')));
    }

    private static HashSet<string> Closure(string project)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(project);
        while (queue.Count > 0)
        {
            foreach (var reference in ProjectReferences(queue.Dequeue()))
            {
                if (seen.Add(reference))
                {
                    queue.Enqueue(reference);
                }
            }
        }

        return seen;
    }

    private static IEnumerable<string> Sources(string project) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", project), "*.cs", SearchOption.AllDirectories)
            .Where(static p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    [Theory]
    [InlineData("MikePDF.WriterHost")]
    [InlineData("MikePDF.PdfWriter")]
    [InlineData("MikePDF.Markup")]
    [InlineData("MikePDF.Ink")]
    public void NothingTheWriterHostLoadsReferencesMikePDFCore(string project)
    {
        var closure = Closure(project);
        Assert.DoesNotContain("MikePDF.Core", closure);
        Assert.DoesNotContain("MikePDF.Markup.Editing", closure);
        Assert.DoesNotContain("MikePDF.Broker", closure);
        Assert.DoesNotContain("MikePDF.App", closure);
    }

    [Fact]
    public void TheWriterHostReachesMarkupAndInkOnlyThroughThePdfWriter()
    {
        var closure = Closure("MikePDF.WriterHost");
        Assert.Contains("MikePDF.PdfWriter", closure);
        Assert.Contains("MikePDF.Markup", closure);
        Assert.Contains("MikePDF.Ink", closure);
        Assert.Equal(["MikePDF.Ink"], ProjectReferences("MikePDF.Markup").ToArray());
    }

    [Theory]
    [InlineData("MikePDF.WriterHost")]
    [InlineData("MikePDF.PdfWriter")]
    [InlineData("MikePDF.Markup")]
    [InlineData("MikePDF.Ink")]
    [InlineData("MikePDF.Protocol")]
    public void NoSourceOnTheWriterSideNamesTheCoreNamespace(string project)
    {
        Assert.Contains(project, WriterSide);
        foreach (var file in Sources(project))
        {
            var offending = File.ReadAllLines(file)
                .Where(static l => l.Contains("MikePDF.Core", StringComparison.Ordinal) && !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .ToList();
            Assert.True(offending.Count == 0, $"{Path.GetRelativePath(RepoRoot(), file)} names MikePDF.Core: {offending.FirstOrDefault()?.Trim()}");
        }
    }

    [Fact]
    public void TheWriterSideProjectsAreTrimAndNativeAotSafe()
    {
        foreach (var project in new[] { "MikePDF.Markup", "MikePDF.Ink" })
        {
            var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", project, project + ".csproj"));
            Assert.Contains("<IsAotCompatible>true</IsAotCompatible>", text, StringComparison.Ordinal);
            Assert.DoesNotContain("IL2026", text, StringComparison.Ordinal);
            Assert.DoesNotContain("IL3050", text, StringComparison.Ordinal);
            foreach (var file in Sources(project))
            {
                var source = File.ReadAllText(file);
                Assert.DoesNotContain("UnconditionalSuppressMessage", source, StringComparison.Ordinal);
                Assert.DoesNotContain("RequiresUnreferencedCode", source, StringComparison.Ordinal);
                Assert.DoesNotContain("pragma warning disable IL", source, StringComparison.Ordinal);
            }
        }
    }
}
