using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
using Quire.App.Services;
using Quire.Core.Reading;
using Windows.ApplicationModel.DataTransfer;

namespace Quire.App;

/// <summary>
/// Links in the pages. An internal link goes to its page and position. An external link is never followed: Quire opens no address and starts
/// no program (the privacy tests forbid the APIs). The window shows the whole address, says where it leads, and offers to copy it, so the person
/// can open it in a browser if they choose to.
/// </summary>
public sealed partial class MainWindow
{
    private bool _addressDialogOpen;

    private async void OnLinkActivated(int page, PageLink link)
    {
        if (_session is not { IsOpen: true } session)
        {
            return;
        }

        switch (link.Destination.Kind)
        {
            case DestinationKind.Page or DestinationKind.PageY or DestinationKind.PageXY:
                await session.GoToDestinationAsync(link.Destination);
                Pages.FocusPages();
                break;
            case DestinationKind.External:
                await ShowAddressAsync(session, page, link);
                break;
            case DestinationKind.Other:
                ShowBanner(InfoBarSeverity.Informational, "Banner_LinkSkipped_Title", null, closable: true, messageText: Strings.Format("Banner_LinkSkipped_Message", Strings.Get("AppName")));
                break;
            default:
                break;
        }
    }

    private async Task ShowAddressAsync(DocumentSession session, int page, PageLink link)
    {
        if (_addressDialogOpen || session.Store is not { } store)
        {
            return;
        }

        var reply = await store.Reader.GetAddressAsync(page, link.Index, CancellationToken.None);
        if (!reply.IsOk)
        {
            ShowBanner(InfoBarSeverity.Warning, "Banner_LinkAddress_Title", "Banner_LinkAddress_Message", closable: true);
            return;
        }

        var address = reply.Value!;
        var panel = new StackPanel { Spacing = 12, MaxWidth = 460 };
        panel.Children.Add(new TextBlock
        {
            Text = Strings.Format(address.IsWebOrMail ? "Link_ExternalWeb" : "Link_ExternalOther", Strings.Get("AppName")),
            TextWrapping = TextWrapping.Wrap,
        });
        var text = new TextBlock
        {
            Text = address.Text,
            TextWrapping = TextWrapping.WrapWholeWords,
            IsTextSelectionEnabled = true,
            FontWeight = FontWeights.SemiBold,
        };
        AutomationProperties.SetAutomationId(text, "ExternalLinkAddress");
        AutomationProperties.SetName(text, Strings.Format("Link_AddressName", address.Text));
        panel.Children.Add(text);
        if (address.AsciiHost is { } ascii)
        {
            var host = new TextBlock { Text = Strings.Format("Link_ExternalHostAscii", ascii), TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(host, "ExternalLinkHost");
            panel.Children.Add(host);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = Strings.Get("Link_ExternalTitle"),
            Content = panel,
            PrimaryButtonText = Strings.Get("Link_Copy"),
            CloseButtonText = Strings.Get("Dialog_Close"),
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetAutomationId(dialog, "ExternalLinkDialog");
        _addressDialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                package.SetText(address.Text);
                Clipboard.SetContent(package);
            }
        }
        finally
        {
            _addressDialogOpen = false;
        }

        Pages.FocusPages();
    }

    private void OnCopied(bool truncated)
    {
        if (truncated)
        {
            ShowBanner(InfoBarSeverity.Informational, "Banner_CopyTruncated_Title", "Banner_CopyTruncated_Message", closable: true);
            _bannerTimer.Interval = TimeSpan.FromSeconds(6);
            _bannerTimer.Stop();
            _bannerTimer.Start();
        }
    }
}
