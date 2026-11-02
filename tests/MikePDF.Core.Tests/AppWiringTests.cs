using System.Text.RegularExpressions;
using MikePDF.Core.Commands;

namespace MikePDF.Core.Tests;

/// <summary>
/// Checks, from the source of the UI app, that it is wired the way the command spec says (CMD-24, rule 1 of docs/design/commands.md): no static accelerators, every
/// toolbar button is a view of a command, every command has an action, and every string the code asks for exists. Reading source is how PrivacyTests does it too:
/// the UI itself needs a desktop, these need nothing.
/// </summary>
public sealed class AppWiringTests
{
    private static string AppDirectory => Path.Combine(ResourceText.RepoRoot(), "src", "MikePDF.App");

    private static IEnumerable<string> Sources(string pattern) =>
        Directory.EnumerateFiles(AppDirectory, pattern, SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    private static string AllCode() => string.Join('\n', Sources("*.cs").Select(File.ReadAllText));

    [Fact]
    public void CMD24_NoStaticKeyboardAcceleratorIsLeftInTheXamlOrTheCode()
    {
        foreach (var file in Sources("*.xaml").Concat(Sources("*.cs")))
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in new[] { "<KeyboardAccelerator", "KeyboardAccelerators", "KeyboardAcceleratorInvokedEventArgs", "new KeyboardAccelerator", "KeyboardAcceleratorPlacementMode" })
            {
                Assert.False(text.Contains(forbidden, StringComparison.Ordinal), $"{Path.GetFileName(file)} still uses {forbidden}");
            }
        }
    }

    [Fact]
    public void CMD24_EveryToolbarButtonAndMenuItemOfTheWindowIsBoundToACommand()
    {
        var xaml = File.ReadAllText(Path.Combine(AppDirectory, "MainWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(AppDirectory, "MainWindow.Commands.cs"));
        var names = Regex.Matches(xaml, @"<(AppBarButton|AppBarToggleButton|MenuFlyoutItem|RadioMenuFlyoutItem)\s+x:Name=""(\w+)""").Select(m => m.Groups[2].Value)
            .Concat(Regex.Matches(xaml, @"<Button\s+x:Name=""(\w+)""").Select(m => m.Groups[1].Value))
            .Where(n => n is not ("PageColourButton" or "BannerAction")) // a menu's parent has no action of its own; the banner's one button is handled in the code behind
            .ToList();
        Assert.Contains("OpenButton", names);
        Assert.Contains("ZoomActualSizeItem", names);
        foreach (var name in names)
        {
            Assert.True(code.Contains($"Bind({name},", StringComparison.Ordinal), $"{name} is a button or menu item in MainWindow.xaml with no command bound to it");
        }

        // A button's own click handler would be a second way to do what the command does.
        Assert.DoesNotMatch(@"<(AppBarButton|MenuFlyoutItem|RadioMenuFlyoutItem)\b[^>]*\sClick=", xaml);
    }

    [Fact]
    public void CMD01_EveryCommandInTheCatalogueHasAnActionInTheWindow()
    {
        var code = AllCode();
        var actions = Regex.Matches(code, @"(?:Sync|Async)\(""([a-z][A-Za-z0-9.]+)""").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(code, @"_actions\[""([a-z][A-Za-z0-9.]+)""\]").Select(m => m.Groups[1].Value))
            .Concat(Regex.Matches(code, @"(?:sync|async)\(""([a-z][A-Za-z0-9.]+)""").Select(m => m.Groups[1].Value))
            .ToHashSet();
        var missing = CommandCatalog.All.Select(c => c.Id).Where(id => !actions.Contains(id)).ToList();
        Assert.True(missing.Count == 0, "Commands with no action in the window (they would sit in the palette doing nothing): " + string.Join(", ", missing));
        var orphans = actions.Where(id => !CommandCatalog.ById.ContainsKey(id)).ToList();
        Assert.True(orphans.Count == 0, "Actions for ids that are not in the catalogue: " + string.Join(", ", orphans));
    }

    [Fact]
    public void EveryStringTheCodeAsksForExistsInTheResources()
    {
        var keys = Regex.Matches(AllCode(), @"Strings\.(?:Get|Format)\(""([A-Za-z0-9_]+)""").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(AllCode(), @"(?:ShowBanner|ShowBannerText)\([^;]*?""([A-Z][A-Za-z0-9]*_[A-Za-z0-9_]+)""").Select(m => m.Groups[1].Value))
            .Concat(Regex.Matches(AllCode(), @"ConfirmAsync\(""([A-Za-z0-9_]+)"", ""([A-Za-z0-9_]+)""").SelectMany(m => new[] { m.Groups[1].Value, m.Groups[2].Value }))
            .Where(k => !k.EndsWith('_')) // the start of a key that is finished at run time (Properties_Form_ + the form type)
            .Distinct()
            .ToList();
        Assert.NotEmpty(keys);
        var missing = keys.Where(k => !ResourceText.Has(k)).ToList();
        Assert.True(missing.Count == 0, "Strings used in code but missing from Resources.resw: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryXUidInTheXamlHasResources()
    {
        foreach (var file in Sources("*.xaml"))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"x:Uid=""(\w+)"""))
            {
                var uid = match.Groups[1].Value;
                Assert.True(ResourceText.All.Keys.Any(k => k.StartsWith(uid + ".", StringComparison.Ordinal)), $"{Path.GetFileName(file)}: x:Uid {uid} has no resources");
            }
        }
    }

    [Fact]
    public void ThePolicyCodeNeverWritesTheRegistry()
    {
        // Policy is read, never written (the rules for this work package: no HKLM writes, and the app has no reason to touch HKCU either).
        foreach (var file in Directory.EnumerateFiles(Path.Combine(ResourceText.RepoRoot(), "src", "MikePDF.Core", "Settings"), "*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in new[] { "CreateSubKey", "SetValue(", "DeleteValue", "DeleteSubKey", "RegSetValue", "RegCreateKey", "RegDeleteKey" })
            {
                Assert.False(text.Contains(forbidden, StringComparison.Ordinal), $"{Path.GetFileName(file)} contains {forbidden}");
            }
        }
    }
}
