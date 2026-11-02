using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Quire.App.Services;
using Quire.Core.Reading;
using Quire.Protocol;

namespace Quire.App;

/// <summary>The sidebar: thumbnails of the pages and the bookmarks (WP-S2). Both are for getting around; neither changes the file.</summary>
public sealed partial class MainWindow
{
    private const double ThumbnailWidthDip = 150;

    private ThumbnailPages? _thumbnailPages;
    private DocumentSession? _sidebarSession;
    private int _lastSyncedPage = -1;
    private bool _outlineLoaded;
    private CancellationTokenSource? _outlineLoad;

    private bool _switchingTabs;

    private bool OutlineShown => BookmarksTab.IsChecked == true;

    // ---- attaching to a document -------------------------------------------------------------------------------------------------

    private void AttachSidebar(DocumentSession session)
    {
        DetachSidebar();
        _sidebarSession = session;
        var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        session.ThumbnailPixelWidth = (int)Math.Round(ThumbnailWidthDip * scale);
        _thumbnailPages = new ThumbnailPages(session.Controller.PageCount, ThumbnailShape);
        ThumbnailList.ItemsSource = _thumbnailPages;
        session.ThumbnailReady += OnThumbnailReady;
        session.ViewChanged += OnSidebarViewChanged;
        _outlineLoaded = false;
        _lastSyncedPage = -1;
        OutlineTree.RootNodes.Clear();
        OutlineMessage.Visibility = Visibility.Collapsed;
        SidebarButton.IsEnabled = true;
        FindButton.IsEnabled = true;
        if (Sidebar.IsPaneOpen && OutlineShown)
        {
            _ = LoadOutlineAsync();
        }

        SyncThumbnailSelection();
    }

    private void DetachSidebar()
    {
        if (_sidebarSession is { } session)
        {
            session.ThumbnailReady -= OnThumbnailReady;
            session.ViewChanged -= OnSidebarViewChanged;
        }

        _sidebarSession = null;
        _outlineLoad?.Cancel();
        ThumbnailList.ItemsSource = null;
        _thumbnailPages = null;
        OutlineTree.RootNodes.Clear();
        OutlineMessage.Visibility = Visibility.Collapsed;
        SidebarButton.IsEnabled = false;
        FindButton.IsEnabled = false;
        if (SidebarButton.IsChecked == true)
        {
            SidebarButton.IsChecked = false;
        }
    }

    private (double Width, double Height) ThumbnailShape(int page)
    {
        var sizes = _sidebarSession?.Controller;
        var size = sizes is { PageCount: > 0 } ? sizes.Layout.SizePt(page) : new Quire.Core.Geometry.SizeD(612, 792);
        // The layout holds the size after the view rotation; thumbnails show the page as the file has it.
        if (sizes is { ViewTurns: var turns } && Quire.Core.Geometry.QuarterTurns.IsOdd(turns))
        {
            size = new Quire.Core.Geometry.SizeD(size.Height, size.Width);
        }

        var height = size.Width > 0 ? ThumbnailWidthDip * size.Height / size.Width : ThumbnailWidthDip * 1.3;
        return (ThumbnailWidthDip, Math.Clamp(height, 40, 520));
    }

    private void OnSidebarViewChanged()
    {
        _thumbnailPages?.Reshape();
        SyncThumbnailSelection();
    }

    // ---- opening and closing the pane ---------------------------------------------------------------------------------------------

    private void OnSidebarToggled(object sender, RoutedEventArgs e)
    {
        var open = SidebarButton.IsChecked == true;
        Sidebar.IsPaneOpen = open;
        if (!open)
        {
            return;
        }

        if (OutlineShown)
        {
            _ = LoadOutlineAsync();
        }
        else
        {
            SyncThumbnailSelection(force: true);
        }
    }

    // F4 opens the pane and puts the keyboard in it (a thumbnail or a bookmark is a keystroke away); F4 again, or Esc, gives the keyboard back to the pages.
    private void OnSidebarAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (!SidebarButton.IsEnabled)
        {
            return;
        }

        var open = SidebarButton.IsChecked != true;
        SidebarButton.IsChecked = open;
        if (open)
        {
            FocusSidebar();
        }
        else
        {
            Pages.FocusPages();
        }
    }

    private void FocusSidebar() => DispatcherQueue.TryEnqueue(() =>
    {
        if (OutlineShown)
        {
            OutlineTree.Focus(FocusState.Programmatic);
        }
        else
        {
            SyncThumbnailSelection(force: true);
            ThumbnailList.Focus(FocusState.Programmatic);
        }
    });

    private void OnSidebarKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            Pages.FocusPages();
        }
    }

    // The two tabs are toggle buttons that behave as a pair: one is always on, turning one on turns the other off.
    private void OnSidebarTabToggled(object sender, RoutedEventArgs e)
    {
        // Checked is raised while the XAML is still being read: the rest of the window does not exist yet.
        if (_switchingTabs || OutlinePane is null || BookmarksTab is null)
        {
            return;
        }

        _switchingTabs = true;
        var outline = ReferenceEquals(sender, BookmarksTab) ? BookmarksTab.IsChecked == true : ThumbnailsTab.IsChecked != true;
        ThumbnailsTab.IsChecked = !outline;
        BookmarksTab.IsChecked = outline;
        _switchingTabs = false;
        ApplySidebarTab();
    }

    private void ApplySidebarTab()
    {
        var outline = OutlineShown;
        ThumbnailList.Visibility = outline ? Visibility.Collapsed : Visibility.Visible;
        OutlinePane.Visibility = outline ? Visibility.Visible : Visibility.Collapsed;
        if (outline && Sidebar.IsPaneOpen)
        {
            _ = LoadOutlineAsync();
        }
        else if (!outline)
        {
            SyncThumbnailSelection(force: true);
        }
    }

    // ---- thumbnails ---------------------------------------------------------------------------------------------------------------

    private void OnThumbnailContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (_sidebarSession?.Thumbnails is not { } thumbnails)
        {
            return;
        }

        if (args.InRecycleQueue)
        {
            thumbnails.Cancel(args.ItemIndex);
            return;
        }

        if (args.Item is not ThumbnailItem item || item.Image is not null)
        {
            return;
        }

        if (thumbnails.TryGet(item.Page, out var image))
        {
            item.Image = ToBitmap(image);
        }
        else
        {
            thumbnails.Request(item.Page);
        }
    }

    private void OnThumbnailReady(int page, ThumbnailImage image)
    {
        if (_thumbnailPages is { } list && list.TryGetMade(page, out var item) && item.Image is null)
        {
            item.Image = ToBitmap(image);
        }
    }

    private static WriteableBitmap ToBitmap(ThumbnailImage image)
    {
        var bitmap = new WriteableBitmap(image.Width, image.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(image.Bgra, 0, image.Bgra.Length);
        }

        bitmap.Invalidate();
        return bitmap;
    }

    private void OnThumbnailClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ThumbnailItem item && _session is { IsOpen: true } session)
        {
            session.GoToPage(item.Page);
        }
    }

    // The thumbnail of the page in view is selected, so keyboard users start the pane where they are.
    private void SyncThumbnailSelection(bool force = false)
    {
        if (_thumbnailPages is null || _sidebarSession is null || !Sidebar.IsPaneOpen || OutlineShown)
        {
            return;
        }

        var page = _sidebarSession.Controller.CurrentPage;
        if (page < 0 || (page == _lastSyncedPage && !force))
        {
            return;
        }

        _lastSyncedPage = page;
        ThumbnailList.SelectedIndex = page;
        if (!ThumbnailList.IsLoaded || FocusManager.GetFocusedElement(Root.XamlRoot) is not ListViewItem)
        {
            ThumbnailList.ScrollIntoView(_thumbnailPages.Get(page), ScrollIntoViewAlignment.Leading);
        }
    }

    // ---- bookmarks ------------------------------------------------------------------------------------------------------------------

    private async Task LoadOutlineAsync()
    {
        if (_outlineLoaded || _sidebarSession?.Store is not { } store)
        {
            return;
        }

        _outlineLoaded = true;
        _outlineLoad?.Cancel();
        var cancel = new CancellationTokenSource();
        _outlineLoad = cancel;
        ShowOutlineMessage("Outline_Loading");
        var level = await store.Reader.GetOutlineLevelAsync([], cancel.Token).ConfigureAwait(true);
        if (cancel.IsCancellationRequested || !ReferenceEquals(_sidebarSession?.Store, store))
        {
            return;
        }

        if (!level.IsOk)
        {
            _outlineLoaded = level.Status is Status.EngineCrashed or Status.Timeout ? false : _outlineLoaded;
            ShowOutlineMessage("Outline_Failed");
            return;
        }

        OutlineTree.RootNodes.Clear();
        OutlineMessage.Visibility = Visibility.Collapsed;
        var entries = level.Value!.Entries;
        for (var i = 0; i < entries.Count; i++)
        {
            OutlineTree.RootNodes.Add(Node(entries[i], [i]));
        }

        if (level.Value.Total > entries.Count)
        {
            OutlineTree.RootNodes.Add(new TreeViewNode { Content = new OutlineItem(new OutlineEntry(Strings.Format("Outline_More", level.Value.Total - entries.Count), default, false), []) });
        }

        if (entries.Count == 0)
        {
            ShowOutlineMessage("Outline_Empty");
        }
    }

    private void ShowOutlineMessage(string key)
    {
        OutlineMessage.Text = Strings.Get(key);
        OutlineMessage.Visibility = Visibility.Visible;
    }

    private static TreeViewNode Node(OutlineEntry entry, int[] path) => new()
    {
        Content = new OutlineItem(entry, path),
        HasUnrealizedChildren = entry.HasChildren && path.Length < DocumentReader.MaxOutlineDepth,
    };

    private async void OnOutlineExpanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (args.Node.Content is not OutlineItem parent || !args.Node.HasUnrealizedChildren || _sidebarSession?.Store is not { } store)
        {
            return;
        }

        args.Node.HasUnrealizedChildren = false;
        var level = await store.Reader.GetOutlineLevelAsync(parent.Path, CancellationToken.None).ConfigureAwait(true);
        if (!ReferenceEquals(_sidebarSession?.Store, store))
        {
            return;
        }

        if (!level.IsOk)
        {
            args.Node.HasUnrealizedChildren = level.Status is Status.EngineCrashed or Status.Timeout; // asked again at the next expansion
            return;
        }

        var entries = level.Value!.Entries;
        for (var i = 0; i < entries.Count; i++)
        {
            args.Node.Children.Add(Node(entries[i], [.. parent.Path, i]));
        }

        if (level.Value.Total > entries.Count)
        {
            args.Node.Children.Add(new TreeViewNode { Content = new OutlineItem(new OutlineEntry(Strings.Format("Outline_More", level.Value.Total - entries.Count), default, false), []) });
        }
    }

    private async void OnOutlineInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        // A tree built from nodes hands over the node; one built from items, the item.
        var content = args.InvokedItem is TreeViewNode node ? node.Content : args.InvokedItem;
        if (content is not OutlineItem item || _session is not { IsOpen: true } session)
        {
            return;
        }

        var destination = item.Entry.Destination;
        switch (destination.Kind)
        {
            case DestinationKind.Page or DestinationKind.PageY or DestinationKind.PageXY:
                await session.GoToDestinationAsync(destination);
                break;
            case DestinationKind.External:
                ShowBanner(InfoBarSeverity.Informational, "Banner_BookmarkExternal_Title", null, closable: true, messageText: Strings.Format("Banner_BookmarkExternal_Message", Strings.Get("AppName")));
                break;
            default:
                break;
        }
    }
}
