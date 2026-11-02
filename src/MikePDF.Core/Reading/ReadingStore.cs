using MikePDF.Core.Geometry;
using MikePDF.Core.Text;
using MikePDF.Protocol;

namespace MikePDF.Core.Reading;

/// <summary>
/// Everything the reader knows about the pages of one document beyond how they look: their geometry, characters, links and reading-ordered
/// text, each fetched when first asked for and kept for the pages near the one being read. The text model of a page is built off the calling
/// thread from the engine's characters and the page's geometry.
/// </summary>
public sealed class ReadingStore : ITextModelSource, IDisposable
{
    public ReadingStore(DocumentReader reader, int pagesKept = 48)
    {
        ArgumentNullException.ThrowIfNull(reader);
        Reader = reader;
        Geometry = new PageDataCache<PageGeometry>(reader.GetGeometryAsync, pagesKept);
        Characters = new PageDataCache<PageCharacters>(reader.GetCharactersAsync, pagesKept);
        Links = new PageDataCache<PageLinks>(reader.GetLinksAsync, pagesKept);
        Models = new PageDataCache<TextModel>(BuildModelAsync, pagesKept);
        Models.Loaded += page => ModelLoaded?.Invoke(page);
        Links.Loaded += page => LinksLoaded?.Invoke(page);
    }

    public DocumentReader Reader { get; }

    public PageDataCache<PageGeometry> Geometry { get; }

    public PageDataCache<PageCharacters> Characters { get; }

    public PageDataCache<PageLinks> Links { get; }

    public PageDataCache<TextModel> Models { get; }

    public int PageCount => Reader.PageCount;

    /// <summary>A page's text model is ready. Raised from a worker thread.</summary>
    public event Action<int>? ModelLoaded;

    /// <summary>A page's links are ready. Raised from a worker thread.</summary>
    public event Action<int>? LinksLoaded;

    public TextModel? GetLoaded(int page) => Models.TryGetLoaded(page, out var model) ? model : null;

    public Task<ReadReply<TextModel>> GetModelAsync(int page, CancellationToken cancellationToken = default) => Models.GetAsync(page, cancellationToken);

    public void Dispose()
    {
        Geometry.Dispose();
        Characters.Dispose();
        Links.Dispose();
        Models.Dispose();
    }

    private async Task<ReadReply<TextModel>> BuildModelAsync(int page, CancellationToken cancellationToken)
    {
        var characters = Characters.GetAsync(page, cancellationToken);
        var geometry = Geometry.GetAsync(page, cancellationToken);
        var chars = await characters.ConfigureAwait(false);
        var geo = await geometry.ConfigureAwait(false);
        if (!chars.IsOk)
        {
            return ReadReply.Fail<TextModel>(chars.Status, chars.Detail);
        }

        if (!geo.IsOk)
        {
            return ReadReply.Fail<TextModel>(geo.Status, geo.Detail);
        }

        try
        {
            return new ReadReply<TextModel>(Status.Ok, TextModelBuilder.Build(chars.Value!, geo.Value!, page));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or OverflowException)
        {
            // The characters were hostile in a way the builder did not expect: this page has no text, the rest of the document is unaffected.
            return ReadReply.Fail<TextModel>(Status.Damaged, e.Message);
        }
    }
}
