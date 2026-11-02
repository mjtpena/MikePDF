using System.Collections;
using System.Collections.Immutable;
using System.Globalization;

namespace MikePDF.PdfWriter;

/// <summary>
/// Immutable PDF object (ISO 32000-2 clause 7.3). The hierarchy is closed: only the types in this file derive from it.
/// </summary>
public abstract class PdfObject
{
    private protected PdfObject()
    {
    }
}

public sealed class PdfNull : PdfObject
{
    public static PdfNull Instance { get; } = new();

    private PdfNull()
    {
    }

    public override string ToString() => "null";
}

public sealed class PdfBoolean : PdfObject
{
    public static PdfBoolean True { get; } = new(true);

    public static PdfBoolean False { get; } = new(false);

    private PdfBoolean(bool value) => Value = value;

    public bool Value { get; }

    public static PdfBoolean From(bool value) => value ? True : False;

    public override string ToString() => Value ? "true" : "false";
}

public sealed class PdfInteger(long value) : PdfObject
{
    public long Value { get; } = value;

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public sealed class PdfReal : PdfObject
{
    /// <summary>Largest magnitude accepted; the range of a 32-bit float, which is what readers like PDFium use.</summary>
    public const double MaxMagnitude = 3.4028235e38;

    public PdfReal(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > MaxMagnitude)
            throw new ArgumentOutOfRangeException(nameof(value), "PDF reals must be finite and within float range.");
        Value = value;
    }

    public double Value { get; }

    public override string ToString() => Value.ToString("R", CultureInfo.InvariantCulture);
}

public enum PdfStringFormat
{
    Literal,
    Hex,
}

/// <summary>A byte string. The bytes are copied on construction, so the object is immutable.</summary>
public sealed class PdfString : PdfObject
{
    private readonly byte[] _bytes;

    public PdfString(ReadOnlySpan<byte> bytes, PdfStringFormat format = PdfStringFormat.Literal)
    {
        _bytes = bytes.ToArray();
        Format = format;
    }

    /// <summary>Takes ownership of <paramref name="bytes"/> without copying. Callers must not retain the array.</summary>
    internal PdfString(byte[] bytes, PdfStringFormat format)
    {
        _bytes = bytes;
        Format = format;
    }

    public ReadOnlySpan<byte> Value => _bytes;

    public int Length => _bytes.Length;

    public PdfStringFormat Format { get; }

    /// <summary>A literal string from ASCII text; throws for characters above 0x7F (callers needing UTF-16BE or PDFDocEncoding build the bytes themselves).</summary>
    public static PdfString FromAscii(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] > 0x7F)
                throw new ArgumentException("Text contains a non-ASCII character.", nameof(text));
            bytes[i] = (byte)text[i];
        }

        return new PdfString(bytes, PdfStringFormat.Literal);
    }

    public override string ToString() => $"({Convert.ToHexString(_bytes)})";
}

/// <summary>A name. Each char of <see cref="Value"/> is one byte (0-255) of the decoded name, so any byte sequence round-trips.</summary>
public sealed class PdfName : PdfObject, IEquatable<PdfName>
{
    public PdfName(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        foreach (char c in value)
        {
            if (c > 0xFF)
                throw new ArgumentException("Name characters must be bytes (0-255).", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public bool Equals(PdfName? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as PdfName);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => "/" + Value;
}

public sealed class PdfArray : PdfObject, IReadOnlyList<PdfObject>
{
    public static PdfArray Empty { get; } = FromTrusted(ImmutableArray<PdfObject>.Empty);

    private readonly ImmutableArray<PdfObject> _items;

    public PdfArray(IEnumerable<PdfObject> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = [.. items];
    }

    private PdfArray(ImmutableArray<PdfObject> items, bool trusted) => _items = items;

    internal static PdfArray FromTrusted(ImmutableArray<PdfObject> items) => new(items, trusted: true);

    public int Count => _items.Length;

    public PdfObject this[int index] => _items[index];

    public PdfArray Append(PdfObject item) => FromTrusted(_items.Add(item));

    public IEnumerator<PdfObject> GetEnumerator() => ((IEnumerable<PdfObject>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"[{Count} items]";
}

/// <summary>An indirect reference. Equality is by number and generation.</summary>
public sealed class PdfReference : PdfObject, IEquatable<PdfReference>
{
    public PdfReference(int number, int generation = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(number);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(generation, 65535);
        Number = number;
        Generation = generation;
    }

    public int Number { get; }

    public int Generation { get; }

    public bool Equals(PdfReference? other) => other is not null && other.Number == Number && other.Generation == Generation;

    public override bool Equals(object? obj) => Equals(obj as PdfReference);

    public override int GetHashCode() => HashCode.Combine(Number, Generation);

    public override string ToString() => $"{Number} {Generation} R";
}

#pragma warning disable CA1711 // PDF's own names for these object types (ISO 32000-2 7.3.6, 7.3.8)
/// <summary>
/// An ordered dictionary with unique keys. Order is the order the entries were given (or parsed), which the
/// serializer preserves. Two entries with the same key can never exist in this type: duplicate keys are rejected
/// (constructor) or handled by the parser's mode before an instance is built.
/// </summary>
public sealed class PdfDictionary : PdfObject, IReadOnlyCollection<KeyValuePair<PdfName, PdfObject>>
{
    private const int LinearSearchThreshold = 8;

    public static PdfDictionary Empty { get; } = FromTrusted(ImmutableArray<KeyValuePair<PdfName, PdfObject>>.Empty);

    private readonly ImmutableArray<KeyValuePair<PdfName, PdfObject>> _entries;
    private Dictionary<string, int>? _index;

    public PdfDictionary(IEnumerable<KeyValuePair<PdfName, PdfObject>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = [.. entries];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in _entries)
        {
            if (!seen.Add(kv.Key.Value))
                throw new ArgumentException($"Duplicate key /{kv.Key.Value}.", nameof(entries));
        }
    }

    private PdfDictionary(ImmutableArray<KeyValuePair<PdfName, PdfObject>> entries, bool trusted) => _entries = entries;

    /// <summary>Caller guarantees key uniqueness.</summary>
    internal static PdfDictionary FromTrusted(ImmutableArray<KeyValuePair<PdfName, PdfObject>> entries) => new(entries, trusted: true);

    public int Count => _entries.Length;

    public IEnumerable<PdfName> Keys => _entries.Select(e => e.Key);

    public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PdfObject? value)
    {
        int i = IndexOf(key);
        if (i < 0)
        {
            value = null;
            return false;
        }

        value = _entries[i].Value;
        return true;
    }

    public bool ContainsKey(string key) => IndexOf(key) >= 0;

    /// <summary>The value for <paramref name="key"/>, or null when absent.</summary>
    public PdfObject? this[string key] => TryGetValue(key, out var v) ? v : null;

    public string? GetName(string key) => this[key] is PdfName n ? n.Value : null;

    public long? GetInteger(string key) => this[key] is PdfInteger i ? i.Value : null;

    public PdfReference? GetReference(string key) => this[key] as PdfReference;

    /// <summary>Returns a copy with <paramref name="key"/> set. An existing key keeps its position; a new key is appended.</summary>
    public PdfDictionary With(string key, PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int i = IndexOf(key);
        return i < 0
            ? FromTrusted(_entries.Add(new KeyValuePair<PdfName, PdfObject>(new PdfName(key), value)))
            : FromTrusted(_entries.SetItem(i, new KeyValuePair<PdfName, PdfObject>(_entries[i].Key, value)));
    }

    /// <summary>Returns a copy without <paramref name="key"/> (the same instance when the key is absent).</summary>
    public PdfDictionary Without(string key)
    {
        int i = IndexOf(key);
        return i < 0 ? this : FromTrusted(_entries.RemoveAt(i));
    }

    public IEnumerator<KeyValuePair<PdfName, PdfObject>> GetEnumerator() =>
        ((IEnumerable<KeyValuePair<PdfName, PdfObject>>)_entries).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"<<{Count} entries>>";

    private int IndexOf(string key)
    {
        if (_entries.Length <= LinearSearchThreshold)
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                if (string.Equals(_entries[i].Key.Value, key, StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }

        var index = _index;
        if (index is null)
        {
            index = new Dictionary<string, int>(_entries.Length, StringComparer.Ordinal);
            for (int i = 0; i < _entries.Length; i++)
                index[_entries[i].Key.Value] = i;
            _index = index;
        }

        return index.TryGetValue(key, out int found) ? found : -1;
    }
}

/// <summary>A stream: its dictionary plus the raw (still encoded) bytes. <see cref="RawData"/> may alias the source file buffer.</summary>
public sealed class PdfStream : PdfObject
{
    public PdfStream(PdfDictionary dictionary, ReadOnlyMemory<byte> rawData)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        Dictionary = dictionary;
        RawData = rawData;
    }

    public PdfDictionary Dictionary { get; }

    public ReadOnlyMemory<byte> RawData { get; }

    public override string ToString() => $"stream({RawData.Length} bytes)";
}

/// <summary>Structural comparison and traversal helpers.</summary>
public static class PdfObjects
{
    /// <summary>Structural equality (dictionary entry order matters, string format does not). Returns false past <paramref name="maxDepth"/>.</summary>
    public static bool DeepEquals(PdfObject? a, PdfObject? b, int maxDepth = 256)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null || maxDepth < 0)
            return false;
        switch (a)
        {
            case PdfNull:
                return b is PdfNull;
            case PdfBoolean ab:
                return b is PdfBoolean bb && ab.Value == bb.Value;
            case PdfInteger ai:
                return b is PdfInteger bi && ai.Value == bi.Value;
            case PdfReal ar:
                return b is PdfReal br && ar.Value.Equals(br.Value);
            case PdfName an:
                return an.Equals(b as PdfName);
            case PdfReference arf:
                return arf.Equals(b as PdfReference);
            case PdfString asr:
                return b is PdfString bsr && asr.Value.SequenceEqual(bsr.Value);
            case PdfArray aa:
                if (b is not PdfArray ba || aa.Count != ba.Count)
                    return false;
                for (int i = 0; i < aa.Count; i++)
                {
                    if (!DeepEquals(aa[i], ba[i], maxDepth - 1))
                        return false;
                }

                return true;
            case PdfDictionary ad:
                if (b is not PdfDictionary bd || ad.Count != bd.Count)
                    return false;
                using (var ea = ad.GetEnumerator())
                using (var eb = bd.GetEnumerator())
                {
                    while (ea.MoveNext() && eb.MoveNext())
                    {
                        if (!ea.Current.Key.Equals(eb.Current.Key) || !DeepEquals(ea.Current.Value, eb.Current.Value, maxDepth - 1))
                            return false;
                    }
                }

                return true;
            case PdfStream ast:
                return b is PdfStream bst
                    && DeepEquals(ast.Dictionary, bst.Dictionary, maxDepth - 1)
                    && ast.RawData.Span.SequenceEqual(bst.RawData.Span);
            default:
                return false;
        }
    }

    /// <summary>Every indirect reference reachable from <paramref name="root"/> without following references. Iterative; safe for any depth.</summary>
    public static IEnumerable<PdfReference> EnumerateReferences(PdfObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var stack = new Stack<PdfObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            switch (stack.Pop())
            {
                case PdfReference r:
                    yield return r;
                    break;
                case PdfArray a:
                    for (int i = a.Count - 1; i >= 0; i--)
                        stack.Push(a[i]);
                    break;
                case PdfDictionary d:
                    foreach (var kv in d)
                        stack.Push(kv.Value);
                    break;
                case PdfStream s:
                    stack.Push(s.Dictionary);
                    break;
            }
        }
    }
}
