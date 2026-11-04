namespace MikePDF.Markup.Editing;

/// <summary>
/// The annotations of a document, as the edit model holds them (annotation-core 3.1): per page, in <c>/Annots</c> order, which is the stacking order
/// (later is on top, annotation-core 3.4). Owned annotations created this session and annotations read from the file live side by side. Not
/// thread-safe: one owner, the session.
/// </summary>
public sealed class AnnotationStore
{
    private readonly Dictionary<string, Annotation> _byId = new(StringComparer.Ordinal);
    private readonly SortedDictionary<int, List<Annotation>> _pages = [];
    private readonly Dictionary<int, SpatialIndex> _indexes = [];

    public int Count => _byId.Count;

    public IEnumerable<int> Pages => _pages.Keys;

    public Annotation? Get(string id) => _byId.GetValueOrDefault(id);

    public bool Contains(string id) => _byId.ContainsKey(id);

    /// <summary>The annotations of a page in stacking order, bottom first.</summary>
    public IReadOnlyList<Annotation> OnPage(int page) => _pages.TryGetValue(page, out var list) ? list : [];

    /// <summary>The index of an annotation in its page's list, or -1.</summary>
    public int IndexOf(string id) => _byId.TryGetValue(id, out var a) && _pages.TryGetValue(a.Page, out var list) ? list.FindIndex(x => x.Id == id) : -1;

    public IEnumerable<Annotation> All() => _pages.Values.SelectMany(static l => l);

    /// <summary>Adds an annotation on top (or at a given place in its page's order, so that undoing a delete puts it back where it was).</summary>
    public void Add(Annotation annotation, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (!_byId.TryAdd(annotation.Id, annotation))
        {
            throw new InvalidOperationException($"There is already an annotation with the id {annotation.Id}.");
        }

        if (!_pages.TryGetValue(annotation.Page, out var list))
        {
            _pages[annotation.Page] = list = [];
        }

        list.Insert(index is { } i ? Math.Clamp(i, 0, list.Count) : list.Count, annotation);
        _indexes.Remove(annotation.Page);
    }

    /// <summary>Removes an annotation and returns it with the place it had.</summary>
    public (Annotation Annotation, int Index)? Remove(string id)
    {
        if (!_byId.Remove(id, out var annotation))
        {
            return null;
        }

        var list = _pages[annotation.Page];
        var index = list.FindIndex(x => x.Id == id);
        list.RemoveAt(index);
        if (list.Count == 0)
        {
            _pages.Remove(annotation.Page);
        }

        _indexes.Remove(annotation.Page);
        return (annotation, index);
    }

    /// <summary>Replaces an annotation by a changed version of itself, keeping its place. The id and the page may not change.</summary>
    public void Replace(Annotation changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        if (!_byId.TryGetValue(changed.Id, out var old) || old.Page != changed.Page)
        {
            throw new InvalidOperationException("An annotation can only be replaced by a version of itself on the same page.");
        }

        var list = _pages[changed.Page];
        list[list.FindIndex(x => x.Id == changed.Id)] = changed;
        _byId[changed.Id] = changed;
        _indexes.Remove(changed.Page);
    }

    /// <summary>The annotation that answers (replies and review states), directly, in page order.</summary>
    public IEnumerable<Annotation> RepliesTo(string id) => All().Where(a => a.Parent == id);

    /// <summary>The annotation and everything that answers it, at any depth, parents before children. Cycles are cut.</summary>
    public IReadOnlyList<Annotation> Thread(string id)
    {
        var result = new List<Annotation>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(id);
        while (queue.Count > 0 && result.Count < 100_000)
        {
            var next = queue.Dequeue();
            if (!seen.Add(next) || Get(next) is not { } a)
            {
                continue;
            }

            result.Add(a);
            foreach (var reply in RepliesTo(next))
            {
                queue.Enqueue(reply.Id);
            }
        }

        return result;
    }

    internal SpatialIndex IndexFor(int page)
    {
        if (!_indexes.TryGetValue(page, out var index))
        {
            _indexes[page] = index = new SpatialIndex(OnPage(page));
        }

        return index;
    }
}
