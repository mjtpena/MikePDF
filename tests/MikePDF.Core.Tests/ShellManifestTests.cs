using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MikePDF.Core.Tests;

/// <summary>
/// SHELL-01, a build check: the package manifest declares the thumbnail handler, the preview handler and their surrogate classes with real CLSIDs, every handler <c>Clsid</c>
/// resolves to a class the manifest declares (makeappx refuses a package where it does not, S6b), and the GUIDs are the ones the DLL serves. Also the rules about registry writes.
/// </summary>
public sealed partial class ShellManifestTests
{
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace Desktop2 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/2";
    private static readonly XNamespace Com = "http://schemas.microsoft.com/appx/manifest/com/windows10";

    private static string Root => ResourceText.RepoRoot();

    private static XDocument Manifest() => XDocument.Load(Path.Combine(Root, "src", "MikePDF.App", "Package.appxmanifest"));

    private static IEnumerable<XElement> Handlers(XDocument manifest, string name) =>
        manifest.Descendants(Uap + "FileTypeAssociation").Where(a => a.Element(Uap + "SupportedFileTypes")!.Elements(Uap + "FileType").Any(t => t.Value == ".pdf")).SelectMany(a => a.Elements(Desktop2 + name));

    [GeneratedRegex(@"kClsid(?<name>\w+)\s*=\s*\{0x(?<a>[0-9A-Fa-f]+),\s*0x(?<b>[0-9A-Fa-f]+),\s*0x(?<c>[0-9A-Fa-f]+),\s*\{(?<d>[^}]+)\}\}")]
    private static partial Regex ClsidConstant();

    private static Guid ConstantGuid(string name)
    {
        var text = File.ReadAllText(Path.Combine(Root, "src", "MikePDF.ShellExt", "shellext_ids.h"));
        var match = ClsidConstant().Matches(text).Single(m => m.Groups["name"].Value == name);
        var bytes = match.Groups["d"].Value.Split(',').Select(b => Convert.ToByte(b.Trim()[2..], 16)).ToArray();
        return new Guid(Convert.ToInt32(match.Groups["a"].Value, 16), Convert.ToInt16(match.Groups["b"].Value, 16), Convert.ToInt16(match.Groups["c"].Value, 16), bytes);
    }

    [Fact]
    public void TheManifestDeclaresBothHandlersForPdfAndEachClsidResolvesToASurrogateClass()
    {
        var manifest = Manifest();
        var thumbnail = Assert.Single(Handlers(manifest, "ThumbnailHandler"));
        var preview = Assert.Single(Handlers(manifest, "DesktopPreviewHandler"));

        var classes = manifest.Descendants(Com + "SurrogateServer").SelectMany(s => s.Elements(Com + "Class").Select(c => (Server: s, Class: c))).ToList();
        Assert.Equal(2, classes.Count);
        foreach (var (handler, expectedConstant) in new[] { (thumbnail, "ThumbnailProvider"), (preview, "PreviewHandler") })
        {
            var clsid = Guid.Parse((string)handler.Attribute("Clsid")!);
            Assert.NotEqual(Guid.Empty, clsid);
            var match = Assert.Single(classes, c => Guid.Parse((string)c.Class.Attribute("Id")!) == clsid);
            Assert.Equal(ConstantGuid(expectedConstant), clsid); // the DLL serves exactly this class
            Assert.Equal(@"ShellExt\MikePDF.ShellExt.dll", (string)match.Class.Attribute("Path")!);
            Assert.True(Guid.TryParse((string)match.Server.Attribute("AppId")!, out var appId) && appId != Guid.Empty);
        }

        // The preview class is hosted by the system's PreviewHost and is single-threaded-apartment; the thumbnail class has its own surrogate and is free-threaded.
        var previewServer = classes.Single(c => Guid.Parse((string)c.Class.Attribute("Id")!) == ConstantGuid("PreviewHandler"));
        Assert.Equal("PreviewHost", (string)previewServer.Server.Attribute("SystemSurrogate")!);
        Assert.Equal("STA", (string)previewServer.Class.Attribute("ThreadingModel")!);
        var thumbnailServer = classes.Single(c => Guid.Parse((string)c.Class.Attribute("Id")!) == ConstantGuid("ThumbnailProvider"));
        Assert.Null(thumbnailServer.Server.Attribute("SystemSurrogate"));
        Assert.Equal("Both", (string)thumbnailServer.Class.Attribute("ThreadingModel")!);
        Assert.NotEqual(Guid.Parse((string)thumbnailServer.Server.Attribute("AppId")!), Guid.Parse((string)previewServer.Server.Attribute("AppId")!));
    }

    [Fact]
    public void TheClassesAreDeclaredAtApplicationLevelAndNothingElseIsRegistered()
    {
        var manifest = Manifest();
        foreach (var server in manifest.Descendants(Com + "ComServer"))
        {
            Assert.Equal("Extensions", server.Parent!.Parent!.Name.LocalName); // com:Extension inside the Application's Extensions
            Assert.Equal("Application", server.Parent.Parent.Parent!.Name.LocalName);
            Assert.Empty(server.Elements(Com + "ExeServer")); // an ExeServer class cannot be a thumbnail handler, and nothing else needs one
        }

        // One .pdf association, and no other shell hooks: no context-menu verbs, no property handlers.
        Assert.Single(manifest.Descendants(Uap + "FileTypeAssociation"));
        Assert.DoesNotContain("ContextMenu", manifest.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("windows.fileOpenPicker", manifest.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePackageScriptLaysOutTheFilesTheManifestNames()
    {
        var script = File.ReadAllText(Path.Combine(Root, "build", "package.ps1"));
        Assert.Contains("ShellExt", script, StringComparison.Ordinal);
        Assert.Contains("MikePDF.ShellExt.dll", script, StringComparison.Ordinal);
        Assert.Contains("MikePDF.ShellHost.exe", script, StringComparison.Ordinal);
        Assert.Contains("'ShellExt/MikePDF.ShellExt.dll'", script, StringComparison.Ordinal); // the release check demands it
        Assert.Contains("'ShellHost/MikePDF.ShellHost.exe'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDllExportsTheTwoComEntryPointsAndNothingToRegisterWith()
    {
        var def = File.ReadAllText(Path.Combine(Root, "src", "MikePDF.ShellExt", "MikePDF.ShellExt.def"));
        Assert.Contains("DllGetClassObject", def, StringComparison.Ordinal);
        Assert.Contains("DllCanUnloadNow", def, StringComparison.Ordinal);
        Assert.DoesNotContain("DllRegisterServer", def, StringComparison.Ordinal);
        Assert.DoesNotContain("DllInstall", def, StringComparison.Ordinal);
        var sources = Directory.GetFiles(Path.Combine(Root, "src", "MikePDF.ShellExt"), "*.cpp").Select(File.ReadAllText);
        Assert.All(sources, text => Assert.DoesNotContain("RegSetValue", text, StringComparison.Ordinal)); // the handler never writes the registry
        Assert.All(sources, text => Assert.DoesNotContain("RegCreateKey", text, StringComparison.Ordinal));
    }

    [Fact]
    public void RegistryWritesInTheProbeAreGuardedByTheDisposableRunnerCheck()
    {
        // ADR-0018: registry experiments on hosted runners only, under GITHUB_ACTIONS and RUNNER_ENVIRONMENT=github-hosted; nothing is ever registered on a developer machine.
        var probe = File.ReadAllText(Path.Combine(Root, "tests", "MikePDF.ShellExt.Tests", "probe", "shell_probe.cpp"));
        Assert.Contains("GITHUB_ACTIONS", probe, StringComparison.Ordinal);
        Assert.Contains("RUNNER_ENVIRONMENT", probe, StringComparison.Ordinal);
        Assert.Contains("github-hosted", probe, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(probe, "RegCreateKeyExW")); // SetString and SetDword, nothing else
        var phase = probe[probe.IndexOf("void PhasePolicy()", StringComparison.Ordinal)..];
        Assert.True(phase.IndexOf("OnDisposableRunner()", StringComparison.Ordinal) < phase.IndexOf("SetString(", StringComparison.Ordinal));
        Assert.Equal(1, Regex.Count(probe, @"\bPhasePolicy\(\);")); // the only caller of SetString and SetDword
        var installer = File.ReadAllText(Path.Combine(Root, "build", "store", "install-test.ps1"));
        Assert.Contains("RUNNER_ENVIRONMENT -ne 'github-hosted'", installer, StringComparison.Ordinal);
    }
}
