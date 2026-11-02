using MikePDF.Broker;
using MikePDF.Core.Geometry;
using MikePDF.Core.Layout;
using MikePDF.Core.Reading;
using MikePDF.Core.Search;
using MikePDF.Core.Text;
using MikePDF.Core.Thumbnails;

namespace MikePDF.App.Services;

/// <summary>A page drawn small for the thumbnail pane: BGRA, opaque.</summary>
internal sealed record ThumbnailImage(int Width, int Height, byte[] Bgra);

/// <summary>
/// What is under a point of the viewport: the page, the point in page space, the text atom nearest to it (-1 when the page's text is not loaded),
/// the position between atoms a caret at the point would take (before the atom when the point is in its left half, after it in its right half)
/// and the link, if any.
/// </summary>
internal readonly record struct TextHit(int Page, PointD PagePoint, int Atom, int CaretAtom, PageLink? Link);

/// <summary>
/// The reading half of a document session: bookmarks, links, text and its selection, search, and the thumbnails (WP-S2). It all runs against a
/// second document that the pool engine opens (ADR-0004: the shared engine for thumbnails and search), so the document's own engine keeps
/// drawing tiles while a page's text is read or a search runs. If the pool engine could not start, the document's own engine serves instead.
/// Events are raised on the UI thread.
/// </summary>
internal sealed partial class DocumentSession
{
    private const int PagesPrefetched = 6;

    private readonly Dictionary<int, IReadOnlyList<RectD>> _findRects = [];
    private ReadingStore? _readingStore;
    private SelectionController? _selection;
    private ThumbnailScheduler<ThumbnailImage>? _thumbnails;
    private Task<SupervisedDocument?>? _servicesTask;
    private SupervisedDocument? _servicesDocument;
    private FindSession? _find;
    private int _textChangePending;

    public ReadingStore? Store => _readingStore;

    public SelectionController? Selection => _selection;

    public ThumbnailScheduler<ThumbnailImage>? Thumbnails => _thumbnails;

    public FindSession? Find => _find;

    /// <summary>The width thumbnails are drawn at, in device pixels. Changing it forgets the thumbnails drawn so far.</summary>
    public int ThumbnailPixelWidth
    {
        get => _thumbnailPixelWidth;
        set
        {
            var clamped = Math.Clamp(value, 64, 512);
            if (clamped != _thumbnailPixelWidth)
            {
                _thumbnailPixelWidth = clamped;
                _thumbnails?.Clear();
            }
        }
    }

    private int _thumbnailPixelWidth = 160;

    /// <summary>The selection changed.</summary>
    public event Action? SelectionChanged;

    /// <summary>Text, links or geometry of a page arrived, or a search moved: what the page surface draws on top of the pages may have changed.</summary>
    public event Action? OverlaysChanged;

    /// <summary>A thumbnail was drawn.</summary>
    public event Action<int, ThumbnailImage>? ThumbnailReady;

    private void StartReading()
    {
        var engine = new SupervisedReadingEngine(ServicesDocumentAsync);
        var reader = new DocumentReader(engine, (int)_document!.PageCount);
        _readingStore = new ReadingStore(reader);
        _readingStore.ModelLoaded += _ => RaiseOverlaysChanged();
        _readingStore.LinksLoaded += _ => RaiseOverlaysChanged();
        _readingStore.Characters.Loaded += _ => RaiseOverlaysChanged();
        _selection = new SelectionController(_readingStore);
        _selection.Changed += () => SelectionChanged?.Invoke();
        _thumbnails = new ThumbnailScheduler<ThumbnailImage>(RenderThumbnailAsync);
        _thumbnails.Ready += (page, image) => _ui.TryEnqueue(() =>
        {
            if (!_disposed)
            {
                ThumbnailReady?.Invoke(page, image);
            }
        });
    }

    private void StopReading()
    {
        CancelFind();
        _thumbnails?.Dispose();
        _readingStore?.Dispose();
    }

    private void RaiseOverlaysChanged()
    {
        if (Interlocked.Exchange(ref _textChangePending, 1) == 0)
        {
            _ui.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref _textChangePending, 0);
                if (!_disposed)
                {
                    OverlaysChanged?.Invoke();
                }
            });
        }
    }

    // ---- the document the reading services use ---------------------------------------------------------------------------------

    private Task<SupervisedDocument?> ServicesDocumentAsync() => _servicesTask ??= OpenServicesAsync();

    private async Task<SupervisedDocument?> OpenServicesAsync()
    {
        if (_pool is not null)
        {
            try
            {
                var opened = await _pool.OpenDocumentAsync(Path, _password, _cts.Token).ConfigureAwait(false);
                if (opened.IsOk)
                {
                    _servicesDocument = opened.Value;
                    return opened.Value;
                }
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return _document; // no pool engine: the document's own engine serves
    }

    private async Task DisposeServicesAsync()
    {
        if (_servicesTask is { IsCompleted: true } && _servicesDocument is not null)
        {
            await _servicesDocument.DisposeAsync().ConfigureAwait(true);
        }
    }

    // ---- visible pages ---------------------------------------------------------------------------------------------------------

    /// <summary>Starts reading the text, links and geometry of the pages in view (and nothing else), so selection and the accessible text have what they need.</summary>
    public void PrefetchVisible()
    {
        if (_readingStore is null || _disposed)
        {
            return;
        }

        var (first, last) = Controller.Layout.VisiblePages(Controller.ScrollY, Controller.ScrollY + Controller.ViewportHeight);
        for (var page = first; page <= last && page < first + PagesPrefetched; page++)
        {
            _ = _readingStore.GetModelAsync(page);
            _ = _readingStore.Links.GetAsync(page);
        }
    }

    public IReadOnlyList<int> VisiblePages()
    {
        var (first, last) = Controller.Layout.VisiblePages(Controller.ScrollY, Controller.ScrollY + Controller.ViewportHeight);
        return last < first ? [] : [.. Enumerable.Range(first, Math.Min(last - first + 1, PagesPrefetched))];
    }

    // ---- hit testing -----------------------------------------------------------------------------------------------------------

    /// <summary>What is at a viewport point (device pixels), or null for an empty document.</summary>
    public TextHit? HitTest(PointD viewportPoint)
    {
        var view = Controller.Snapshot();
        var projection = PageProjection.At(view, viewportPoint);
        if (projection is null || _readingStore is null)
        {
            return null;
        }

        var pagePoint = projection.ViewportToPage(viewportPoint);
        var model = _readingStore.GetLoaded(projection.Page);
        var atom = model is not null ? model.HitTest(projection.Page, pagePoint) : -1;
        var caret = atom;
        if (model is not null && atom >= 0 && model.Atoms[atom].Rect is { } box && pagePoint.X > box.X + (box.Width / 2))
        {
            caret = atom + 1; // the right half of a letter puts the caret after it
        }

        return new TextHit(projection.Page, pagePoint, atom, caret, projection.PageRect.Contains(viewportPoint) ? LinkAt(projection.Page, pagePoint) : null);
    }

    /// <summary>The link at a point of a page, if the page's links and geometry are loaded. The smallest link wins where they overlap.</summary>
    public PageLink? LinkAt(int page, PointD pagePoint)
    {
        if (_readingStore is null || !_readingStore.Links.TryGetLoaded(page, out var links) || !_readingStore.Geometry.TryGetLoaded(page, out var geometry))
        {
            return null;
        }

        PageLink? best = null;
        var bestArea = double.MaxValue;
        foreach (var link in links.Links)
        {
            if (link.Destination.Kind == DestinationKind.None)
            {
                continue;
            }

            var rect = geometry.PdfToPage(link.Rect);
            if (rect.Contains(pagePoint) && rect.Width * rect.Height < bestArea)
            {
                best = link;
                bestArea = rect.Width * rect.Height;
            }
        }

        return best;
    }

    // ---- going to destinations --------------------------------------------------------------------------------------------------

    /// <summary>Scrolls to a bookmark or link destination: the page and, when the destination has one, the position on it.</summary>
    public async Task GoToDestinationAsync(Destination destination)
    {
        if (!destination.IsInternal || _disposed)
        {
            return;
        }

        var before = HistoryMark.Take(this);
        if (destination.Kind == DestinationKind.Page || _readingStore is null)
        {
            GoToPage(destination.Page);
            RecordJump(before, null);
            return;
        }

        var geometry = await _readingStore.Geometry.GetAsync(destination.Page).ConfigureAwait(true);
        if (_disposed)
        {
            return;
        }

        if (!geometry.IsOk)
        {
            GoToPage(destination.Page);
            RecordJump(before, null);
            return;
        }

        var g = geometry.Value!;
        var x = destination.HasX ? destination.X : g.Box.Left;
        var point = g.PdfToPage(new PointD(x, destination.Y));
        Change(() => Controller.GoToPoint(destination.Page, point, destination.HasX));
        RecordJump(before, null);
    }

    /// <summary>Scrolls just enough to bring a box of a page (page space) into view: not at all if it is already there, otherwise to the middle of the viewport.</summary>
    public void RevealPageRect(int page, RectD pageRect)
    {
        var projection = PageProjection.For(Controller.Snapshot(), page);
        if (projection is null)
        {
            return;
        }

        var r = projection.PageToViewport(pageRect);
        var height = Controller.ViewportHeight;
        var width = Controller.ViewportWidth;
        double dy = 0;
        double dx = 0;
        if (r.Y < 0 || r.Bottom > height)
        {
            dy = r.Y + (r.Height / 2) - (height / 2);
        }

        if (r.X < 0 || r.Right > width)
        {
            dx = r.X + (r.Width / 2) - (width / 2);
        }

        if (dx != 0 || dy != 0)
        {
            Change(() => Controller.ScrollBy(dx, dy));
        }
    }

    // ---- selection ---------------------------------------------------------------------------------------------------------------

    /// <summary>The selected text, for the clipboard, with line breaks as Windows text has them.</summary>
    public async Task<(string Text, bool Truncated)> CopySelectionAsync(CancellationToken cancellationToken = default)
    {
        if (_selection is not { HasSelection: true } selection || _readingStore is not { } store)
        {
            return (string.Empty, false);
        }

        return await SelectionText.CollectAsync(
            selection.Selection,
            async (page, token) =>
            {
                var model = await store.GetModelAsync(page, token).ConfigureAwait(false);
                return model.IsOk ? model.Value : null;
            },
            "\r\n",
            cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Shift with an arrow key: moves the selection's active end, loading the text it needs, and scrolls it into view.</summary>
    public async Task ExtendSelectionAsync(ExtendKind kind, int direction)
    {
        if (_selection is not { } selection || _readingStore is not { } store)
        {
            return;
        }

        var page = Math.Max(0, Controller.CurrentPage);
        for (var attempt = 0; attempt < 4 && !_disposed; attempt++)
        {
            var outcome = selection.Extend(kind, direction, TextPosition.StartOf(page));
            if (outcome != ExtendOutcome.NeedsPage)
            {
                if (outcome == ExtendOutcome.Moved)
                {
                    RevealActiveEnd();
                }

                return;
            }

            var needed = selection.PageNeeded >= 0 ? selection.PageNeeded : page;
            if (!(await store.GetModelAsync(needed).ConfigureAwait(true)).IsOk)
            {
                return;
            }
        }
    }

    private void RevealActiveEnd()
    {
        if (_selection is not { HasSelection: true } selection || _readingStore?.GetLoaded(selection.Selection.Active.Page) is not { Length: > 0 } model)
        {
            return;
        }

        var active = selection.Selection.Active;
        var atom = Math.Clamp(active.Atom, 0, model.Length - 1);
        if (model.Atoms[atom].Rect is { } rect)
        {
            RevealPageRect(active.Page, rect);
        }
    }

    /// <summary>The boxes (page space) of the selection on a page, or none if the page's text is not loaded.</summary>
    public IReadOnlyList<RectD> SelectionRects(int page)
    {
        if (_selection is not { HasSelection: true } selection || _readingStore?.GetLoaded(page) is not { } model)
        {
            return [];
        }

        return SelectionText.RectsOnPage(selection.Selection, page, model);
    }

    // ---- find ---------------------------------------------------------------------------------------------------------------------

    /// <summary>Starts searching for a phrase from the page in view, replacing any search in progress.</summary>
    public FindSession StartFind(SearchOptions options)
    {
        CancelFind();
        _history.ResetKey();
        var store = _readingStore ?? throw new InvalidOperationException("No document is open.");
        ISearchSource source = store.Reader;
        if (options.UseRegex)
        {
            // A regular expression is matched here, over the characters already read for each page (the engine never sees it).
            var compiled = RegexQuery.TryCompile(options.Query, options.MatchCase);
            if (!compiled.IsOk)
            {
                throw new ArgumentException("The pattern is not one that can be searched for: " + compiled.Problem, nameof(options));
            }

            source = new RegexSearchSource((page, token) => store.Characters.GetAsync(page, token), compiled.Regex!);
        }

        var session = new FindSession(source, store.PageCount, options, Math.Max(0, Controller.CurrentPage));
        session.Changed += () => _ui.TryEnqueue(() =>
        {
            if (ReferenceEquals(_find, session) && !_disposed)
            {
                OverlaysChanged?.Invoke();
            }
        });
        _find = session;
        _ = session.StartAsync();
        OverlaysChanged?.Invoke();
        return session;
    }

    /// <summary>Stops the search and removes its highlights.</summary>
    public void CancelFind()
    {
        var find = _find;
        _find = null;
        _findRects.Clear();
        find?.Dispose();
        if (find is not null && !_disposed)
        {
            OverlaysChanged?.Invoke();
        }
    }

    /// <summary>Goes to the next (or previous) match and brings it into view. Returns false if there is none.</summary>
    public async Task<bool> MoveToMatchAsync(bool forward)
    {
        if (_find is not { } find || _readingStore is not { } store)
        {
            return false;
        }

        FoundMatch? match;
        var before = HistoryMark.Take(this);
        try
        {
            match = forward ? await find.NextAsync().ConfigureAwait(true) : find.Previous();
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (match is not { } found || !ReferenceEquals(_find, find) || _disposed)
        {
            return false;
        }

        var characters = await store.Characters.GetAsync(found.Page).ConfigureAwait(true);
        var geometry = await store.Geometry.GetAsync(found.Page).ConfigureAwait(true);
        if (characters.IsOk && geometry.IsOk)
        {
            var rects = MatchGeometry.RectsFor(characters.Value!, geometry.Value!, found.Match.Start, found.Match.Length);
            if (rects.Count > 0)
            {
                RevealPageRect(found.Page, rects[0]);
            }
            else
            {
                GoToPage(found.Page);
            }
        }
        else
        {
            GoToPage(found.Page);
        }

        RecordJump(before, "find:" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(find).ToString(System.Globalization.CultureInfo.InvariantCulture));
        OverlaysChanged?.Invoke();
        return true;
    }

    /// <summary>The properties dialog's data from the document (the file size and page size are the window's to add).</summary>
    public async Task<ReadReply<DocumentProperties>> GetPropertiesAsync(CancellationToken cancellationToken)
    {
        if (_readingStore is not { } store || _disposed)
        {
            return ReadReply.Fail<DocumentProperties>(MikePDF.Protocol.Status.InvalidArgument, "No document is open.");
        }

        return await store.Reader.GetPropertiesAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>The boxes of every match on a page (page space), empty until the page's characters and geometry are loaded (then <see cref="OverlaysChanged"/> is raised).</summary>
    public IReadOnlyList<RectD> FindRects(int page)
    {
        if (_find is not { } find || _readingStore is not { } store || find.MatchesOnPage(page).Count == 0)
        {
            return [];
        }

        if (_findRects.TryGetValue(page, out var cached))
        {
            return cached;
        }

        if (!store.Characters.TryGetLoaded(page, out var characters) || !store.Geometry.TryGetLoaded(page, out var geometry))
        {
            _ = store.Characters.GetAsync(page);
            _ = store.Geometry.GetAsync(page);
            return [];
        }

        var rects = new List<RectD>();
        foreach (var match in find.MatchesOnPage(page))
        {
            rects.AddRange(MatchGeometry.RectsFor(characters, geometry, match.Start, match.Length));
        }

        _findRects[page] = rects;
        return rects;
    }

    /// <summary>The boxes of the match the person is on, if it is on this page.</summary>
    public IReadOnlyList<RectD> CurrentMatchRects(int page)
    {
        if (_find?.Current is not { } current || current.Page != page || _readingStore is not { } store
            || !store.Characters.TryGetLoaded(page, out var characters) || !store.Geometry.TryGetLoaded(page, out var geometry))
        {
            return [];
        }

        return MatchGeometry.RectsFor(characters, geometry, current.Match.Start, current.Match.Length);
    }

    // ---- thumbnails -----------------------------------------------------------------------------------------------------------------

    private async Task<ThumbnailImage?> RenderThumbnailAsync(int page, CancellationToken token)
    {
        var document = await ServicesDocumentAsync().ConfigureAwait(false);
        if (document is null || page < 0 || page >= (int)document.PageCount)
        {
            return null;
        }

        var sizes = _sizes;
        var size = page < sizes.Length ? sizes[page] : new SizeD(612, 792);
        var width = _thumbnailPixelWidth;
        var scale = width / size.Width;
        var pixelWidth = (int)Math.Max(1, PageTransform.EngineRounding(size.Width * scale));
        var pixelHeight = (int)Math.Max(1, PageTransform.EngineRounding(size.Height * scale));
        // The pool's slots are 1 MiB: a very tall page is drawn smaller rather than refused.
        while ((long)pixelWidth * pixelHeight * 4 > 1 << 20)
        {
            scale *= 0.9;
            pixelWidth = (int)Math.Max(1, PageTransform.EngineRounding(size.Width * scale));
            pixelHeight = (int)Math.Max(1, PageTransform.EngineRounding(size.Height * scale));
        }

        var args = new RenderTileArgs((uint)page, (float)scale, 0, 0, 0, (uint)pixelWidth, (uint)pixelHeight, RenderTileArgs.Annotations | RenderTileArgs.FormFields); // a thumbnail shows what the page shows, filled fields included
        var result = await document.RenderTileAsync(args, 0, TileDeadline, CancellationToken.None).ConfigureAwait(false);
        if (result.Value is not { } lease)
        {
            return null;
        }

        using (lease)
        {
            var bytes = new byte[pixelWidth * pixelHeight * 4];
            var source = lease.Pixels;
            for (var y = 0; y < pixelHeight; y++)
            {
                source.Slice(y * lease.Stride, pixelWidth * 4).CopyTo(bytes.AsSpan(y * pixelWidth * 4, pixelWidth * 4));
            }

            return new ThumbnailImage(pixelWidth, pixelHeight, bytes);
        }
    }
}
