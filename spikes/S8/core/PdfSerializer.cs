using System.Globalization;
using System.Text;

namespace S8.Core;

/// <summary>Serialises PdfObj trees (no streams) to PDF syntax. Strings are emitted as hex strings so the byte value is preserved exactly.</summary>
public static class PdfSerializer
{
    public static string ToPdf(PdfObj o) { var sb = new StringBuilder(); Write(sb, o); return sb.ToString(); }

    public static void Write(StringBuilder sb, PdfObj o)
    {
        switch (o)
        {
            case PdfNull: sb.Append("null"); break;
            case PdfBool b: sb.Append(b.Value ? "true" : "false"); break;
            case PdfInt i: sb.Append(i.Value.ToString(CultureInfo.InvariantCulture)); break;
            case PdfReal r: sb.Append(r.Raw); break;
            case PdfName n: WriteName(sb, n.Value); break;
            case PdfString s:
                sb.Append('<');
                foreach (var x in s.Value) sb.Append(x.ToString("X2"));
                sb.Append('>');
                break;
            case PdfRef rf: sb.Append(rf.Num).Append(' ').Append(rf.Gen).Append(" R"); break;
            case PdfArray a:
                sb.Append('[');
                for (int k = 0; k < a.Count; k++) { if (k > 0) sb.Append(' '); Write(sb, a[k]); }
                sb.Append(']');
                break;
            case PdfDict d:
                sb.Append("<<");
                foreach (var kv in d.Map) { sb.Append(' '); WriteName(sb, kv.Key); sb.Append(' '); Write(sb, kv.Value); }
                sb.Append(" >>");
                break;
            case PdfStream: throw new NotSupportedException("stream serialisation is done by the caller");
            default: throw new NotSupportedException(o.GetType().Name);
        }
    }

    public static void WriteName(StringBuilder sb, string n)
    {
        sb.Append('/');
        foreach (char c in n)
        {
            if (c < 33 || c > 126 || "()<>[]{}/%#".Contains(c)) sb.Append('#').Append(((int)c).ToString("X2"));
            else sb.Append(c);
        }
    }

    public static string LiteralString(string s)
    {
        var sb = new StringBuilder("(");
        foreach (char c in s) { if (c == '(' || c == ')' || c == (char)92) sb.Append((char)92); sb.Append(c); }
        return sb.Append(')').ToString();
    }
}
