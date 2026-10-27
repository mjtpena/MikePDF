using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace S4;

/// <summary>qpdf wrapper: --check and --json=2 object access.</summary>
public sealed class Qpdf
{
    public static string Exe = "";

    public static (int code, string output) Run(params string[] args)
    {
        var psi = new ProcessStartInfo(Exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.Latin1, StandardErrorEncoding = Encoding.Latin1 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, so.Result + se.Result);
    }

    public static byte[] RunBytes(params string[] args)
    {
        var psi = new ProcessStartInfo(Exe) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        return ms.ToArray();
    }

    public static string Version() => Run("--version").output.Split('\n')[0].Trim();

    public readonly JsonDocument Doc;
    readonly JsonElement _objs;
    public JsonElement Root => Doc.RootElement;
    public string File { get; }

    public Qpdf(string file)
    {
        File = file;
        var (code, text) = RunJson(file);
        Doc = JsonDocument.Parse(text);
        _objs = Doc.RootElement.GetProperty("qpdf")[1];
    }

    static (int, string) RunJson(string file)
    {
        var psi = new ProcessStartInfo(Exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
        psi.ArgumentList.Add("--json=2"); psi.ArgumentList.Add(file);
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, so.Result);
    }

    static readonly Regex RefRx = new(@"^(\d+) (\d+) R$");
    public static bool IsRef(JsonElement e, out string key)
    {
        key = "";
        if (e.ValueKind == JsonValueKind.String && RefRx.IsMatch(e.GetString()!)) { key = "obj:" + e.GetString(); return true; }
        return false;
    }

    /// <summary>Resolve references to the object's value (dict/array/etc.) or stream dict.</summary>
    public JsonElement Resolve(JsonElement e)
    {
        int guard = 0;
        while (IsRef(e, out var key) && guard++ < 20)
        {
            if (!_objs.TryGetProperty(key, out var o)) return default;
            if (o.TryGetProperty("value", out var v)) e = v;
            else if (o.TryGetProperty("stream", out var s)) return s.GetProperty("dict");
            else return default;
        }
        return e;
    }

    public bool IsStream(JsonElement e) => IsRef(e, out var key) && _objs.TryGetProperty(key, out var o) && o.TryGetProperty("stream", out _);

    public static int ObjNum(JsonElement e) => IsRef(e, out var k) ? int.Parse(k.Split(' ')[0].Substring(4)) : -1;

    public JsonElement Get(JsonElement dict, string key)
    {
        dict = Resolve(dict);
        if (dict.ValueKind == JsonValueKind.Object && dict.TryGetProperty(key, out var v)) return v;
        return default;
    }

    public JsonElement Page1()
    {
        var pg = Root.GetProperty("pages")[0].GetProperty("object");
        return pg;
    }

    public static string Text(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String)
        {
            var s = e.GetString()!;
            if (s.StartsWith("u:")) return s.Substring(2);
            if (s.StartsWith("b:")) return "<bin:" + s.Substring(2) + ">";
            return s;
        }
        if (e.ValueKind == JsonValueKind.Number) return e.GetRawText();
        if (e.ValueKind == JsonValueKind.Undefined) return "";
        return e.GetRawText();
    }

    /// <summary>Decoded stream data of an object, empty if not a stream.</summary>
    public string StreamText(int objNum)
    {
        var b = RunBytes("--show-object=" + objNum, "--filtered-stream-data", File);
        return Encoding.Latin1.GetString(b);
    }

    public IEnumerable<(string key, JsonElement val)> AllObjects()
    {
        foreach (var p in _objs.EnumerateObject()) if (p.Name.StartsWith("obj:")) yield return (p.Name, p.Value);
    }
}
