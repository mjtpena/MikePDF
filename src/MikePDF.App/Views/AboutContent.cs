using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MikePDF.App.Services;
using MikePDF.Core.Settings;
using Windows.ApplicationModel.DataTransfer;

namespace MikePDF.App.Views;

/// <summary>
/// What About shows, built once and used both in the About dialog and in the About page of Settings: the version, what the app does and does not do, and the
/// "Managed by your organisation" report (scenario 9). The text of the policy report comes from <see cref="PolicyReport"/>; this only lays it out.
/// </summary>
internal static class AboutContent
{
    public static StackPanel Build(AppServices services)
    {
        var report = PolicyReport.Build(services.Policy.Current, Strings.Get);
        var panel = new StackPanel { Spacing = 8, MinWidth = 380, MaxWidth = 560 };

        var title = new TextBlock { Text = Strings.Get("AppName"), Style = (Style)Application.Current.Resources["TitleTextBlockStyle"] };
        AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        panel.Children.Add(title);
        panel.Children.Add(Line("AboutVersion", Strings.Format("About_Version", AppPaths.Version, RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant())));
        panel.Children.Add(Line("AboutEngine", Strings.Get("About_Engine")));
        panel.Children.Add(Line("AboutPrivacy", Strings.Get("About_NoTracking")));
        panel.Children.Add(Line("AboutUpdates", Strings.Get("About_Updates")));
        panel.Children.Add(Line("AboutLicence", Strings.Get("About_Licences")));

        if (report.AnyPresent)
        {
            var header = new TextBlock { Text = Strings.Get("About_Managed_Header"), Margin = new Thickness(0, 12, 0, 0), Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] };
            AutomationProperties.SetHeadingLevel(header, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
            panel.Children.Add(header);
            panel.Children.Add(Line("AboutPoliciesCount", Strings.Format("About_Policies_Count", report.InEffectCount)));
            panel.Children.Add(PolicyList(report));
        }

        var copied = new TextBlock { Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        AutomationProperties.SetAutomationId(copied, "AboutCopied");
        AutomationProperties.SetLiveSetting(copied, AutomationLiveSetting.Polite);
        var copy = new Button { Content = Strings.Get("About_Copy"), Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetAutomationId(copy, "AboutCopy");
        copy.Click += (_, _) =>
        {
            var header = new[] { Strings.Format("About_Header_Product", AppPaths.Version + ", " + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()), Strings.Format("About_Header_Os", Environment.OSVersion.Version) };
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(PolicyReport.Build(services.Policy.Current, Strings.Get).ToPlainText(header, Strings.Get));
            try
            {
                Clipboard.SetContent(package);
                copied.Text = Strings.Get("About_Copied");
            }
            catch (COMException)
            {
                copied.Text = string.Empty; // another program holds the clipboard
            }
        };
        panel.Children.Add(copy);
        panel.Children.Add(copied);
        return panel;
    }

    /// <summary>The policy lines as a list: each item is read as one line, "Name: set to value. Effect (source)".</summary>
    public static ListView PolicyList(PolicyReport report)
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false };
        AutomationProperties.SetName(list, Strings.Get("Settings_Policies_List"));
        AutomationProperties.SetAutomationId(list, "PolicyList");
        foreach (var line in report.Lines)
        {
            var text = line.Text(Strings.Get);
            var item = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
            AutomationProperties.SetName(item, text);
            AutomationProperties.SetAutomationId(item, "Policy_" + line.PolicyName);
            list.Items.Add(item);
        }

        return list;
    }

    private static TextBlock Line(string id, string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(block, id);
        return block;
    }
}
