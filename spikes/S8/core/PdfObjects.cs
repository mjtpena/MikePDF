using System.Text;

namespace S8.Core;

/// <summary>Minimal PDF object model. Spike code: only what S8 needs (parse, compare, re-serialise).</summary>
public abstract class PdfObj { }

public sealed class PdfNull : PdfObj { public static readonly PdfNull I = new(); }

public sealed class PdfBool(bool v) : PdfObj { public bool Value { get; } = v; }

public sealed class PdfInt(long v) : PdfObj { public long Value { get; } = v; }

public sealed class PdfReal(double v, string raw) : PdfObj { public double Value { get; } = v; public string Raw { get; } = raw; }

public sealed class PdfName(string v) : PdfObj { public string Value { get; } = v; }

public sealed class PdfString(byte[] v, bool hex, int start, int end) : PdfObj
{
    public byte[] Value { get; } = v;
    public bool IsHex { get; } = hex;
    /// <summary>Absolute file offset of the opening '(' or '&lt;' (-1 if synthesised).</summary>
    public int Start { get; } = start;
    /// <summary>Absolute file offset one past the closing ')' or '&gt;'.</summary>
    public int End { get; } = end;
    public override string ToString() => Encoding.Latin1.GetString(Value);
}

public sealed class PdfArray : PdfObj, System.Collections.Generic.IEnumerable<PdfObj>
{
    public void Add(PdfObj o) => Items.Add(o);
    public System.Collections.Generic.IEnumerator<PdfObj> GetEnumerator() => Items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => Items.GetEnumerator();
    public List<PdfObj> Items { get; } = new();
    public PdfArray() { }
    public PdfArray(IEnumerable<PdfObj> items) { Items.AddRange(items); }
    public int Count => Items.Count;
    public PdfObj this[int i] => Items[i];
}

public sealed class PdfRef(int num, int gen) : PdfObj
{
    public int Num { get; } = num;
    public int Gen { get; } = gen;
    public override string ToString() => $"{Num} {Gen} R";
    public override bool Equals(object? obj) => obj is PdfRef r && r.Num == Num && r.Gen == Gen;
    public override int GetHashCode() => HashCode.Combine(Num, Gen);
}

public class PdfDict : PdfObj
{
    public OrderedDictionary<string, PdfObj> Map { get; } = new();
    /// <summary>Keys that appeared more than once in the source (readers disagree on which one wins).</summary>
    public List<string> Duplicates { get; } = new();
    public PdfObj? this[string key] { get => Map.TryGetValue(key, out var v) ? v : null; set { if (value == null) Map.Remove(key); else Map[key] = value; } }
    public bool Has(string key) => Map.ContainsKey(key);
    public IEnumerable<string> Keys => Map.Keys;
    public PdfDict Clone() { var d = new PdfDict(); foreach (var kv in Map) d.Map[kv.Key] = kv.Value; return d; }
}

/// <summary>A stream: dictionary + the absolute position of the raw (still encoded) bytes in the file.</summary>
public sealed class PdfStream : PdfObj
{
    public PdfDict Dict { get; }
    public byte[] Source { get; }
    public int DataStart { get; }
    public int DataLength { get; }
    public PdfStream(PdfDict dict, byte[] source, int start, int len) { Dict = dict; Source = source; DataStart = start; DataLength = len; }
    public ReadOnlySpan<byte> Raw => Source.AsSpan(DataStart, DataLength);
}

/// <summary>A parsed "N G obj ... endobj" together with where it sits in the file.</summary>
public sealed class PdfIndirect(int num, int gen, PdfObj obj, int start, int end)
{
    public int Num { get; } = num;
    public int Gen { get; } = gen;
    public PdfObj Obj { get; } = obj;
    public int Start { get; } = start;
    public int End { get; } = end;
}

public sealed class PdfFormatException(string msg) : Exception(msg);
public sealed class PdfUnsupportedException(string msg) : Exception(msg);

public static class PdfExt
{
    public static string? NameOf(this PdfObj? o) => (o as PdfName)?.Value;
    public static long? IntOf(this PdfObj? o) => o switch { PdfInt i => i.Value, PdfReal r => (long)r.Value, _ => null };
}
