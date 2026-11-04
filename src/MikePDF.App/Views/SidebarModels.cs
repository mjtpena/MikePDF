using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using MikePDF.App.Services;
using MikePDF.Core.Reading;

namespace MikePDF.App;

/// <summary>One page in the thumbnail pane. Its picture arrives later; until then the item is a white page of the right shape.</summary>
public sealed partial class ThumbnailItem(int page, double width, double height) : INotifyPropertyChanged
{
    private double _width = width;
    private double _height = height;
    private ImageSource? _image;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Zero-based.</summary>
    public int Page { get; } = page;

    public string Label => (Page + 1).ToString(CultureInfo.CurrentCulture);

    public string AutomationName => Strings.Format("Thumbnail_Automation", Page + 1);

    public string AutomationId => "Thumbnail" + (Page + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>What a screen reader calls the list item: the item itself is the page, so its name is "Page 5".</summary>
    public override string ToString() => AutomationName;

    public double Width
    {
        get => _width;
        set => Set(ref _width, value);
    }

    public double Height
    {
        get => _height;
        set => Set(ref _height, value);
    }

    public ImageSource? Image
    {
        get => _image;
        set => Set(ref _image, value);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}

/// <summary>
/// The items of the thumbnail pane: one per page, made when the list asks for them (it asks only for the few that are on screen, so a
/// document of 100,000 pages costs the same as one of ten). A read-only list, as far as the list view is concerned.
/// </summary>
internal sealed partial class ThumbnailPages : IList
{
    private const int MaxKept = 400;

    private readonly Dictionary<int, ThumbnailItem> _items = [];
    private readonly Func<int, (double Width, double Height)> _shape;
    private int _lastAsked;

    public ThumbnailPages(int count, Func<int, (double Width, double Height)> shape)
    {
        Count = count;
        _shape = shape;
    }

    public int Count { get; }

    public bool IsReadOnly => true;

    public bool IsFixedSize => true;

    public bool IsSynchronized => false;

    public object SyncRoot => this;

    public object? this[int index]
    {
        get => Get(index);
        set => throw new NotSupportedException();
    }

    public ThumbnailItem Get(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        _lastAsked = index;
        if (!_items.TryGetValue(index, out var item))
        {
            if (_items.Count >= MaxKept)
            {
                foreach (var far in _items.Keys.Where(k => Math.Abs(k - _lastAsked) > MaxKept / 4).ToList())
                {
                    _items.Remove(far);
                }
            }

            var (width, height) = _shape(index);
            item = new ThumbnailItem(index, width, height);
            _items[index] = item;
        }

        return item;
    }

    /// <summary>The item for a page if the list has made it (so a picture that arrives for a page that is not on screen is not wasted on a new object).</summary>
    public bool TryGetMade(int page, out ThumbnailItem item) => _items.TryGetValue(page, out item!);

    /// <summary>The page sizes became known (they arrive after the first page does): give the items made so far their right shape.</summary>
    public void Reshape()
    {
        foreach (var item in _items.Values)
        {
            var (width, height) = _shape(item.Page);
            item.Width = width;
            item.Height = height;
        }
    }

    public int IndexOf(object? value) => value is ThumbnailItem item && item.Page >= 0 && item.Page < Count ? item.Page : -1;

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public IEnumerator GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
        {
            yield return Get(i);
        }
    }

    public void CopyTo(Array array, int index) => throw new NotSupportedException();

    public int Add(object? value) => throw new NotSupportedException();

    public void Clear() => throw new NotSupportedException();

    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();

    public void RemoveAt(int index) => throw new NotSupportedException();
}

/// <summary>A bookmark in the tree. <see cref="Path"/> is the sibling index taken at each level from the top, which is how the engine is asked for its children.</summary>
public sealed class OutlineItem(OutlineEntry entry, int[] path)
{
    public OutlineEntry Entry { get; } = entry;

    public int[] Path { get; } = path;

    public string Title => Entry.Title.Length == 0 ? Strings.Get("Outline_Untitled") : Entry.Title;

    public override string ToString() => Title;
}
