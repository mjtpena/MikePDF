using System.Buffers.Binary;
using System.Text;
using MessagePack;
using MessagePack.Resolvers;

namespace Quire.S3.Launcher;

// Largest control message candidate: GetCharBoxes on a dense page. Two shapes are compared.
[MessagePackObject(AllowPrivate = false)]
public partial class CharBoxesAos { [Key(0)] public int Status { get; set; } [Key(1)] public CharBox[] Boxes { get; set; } = []; }

[MessagePackObject]
public partial struct CharBox
{
    [Key(0)] public uint Codepoint { get; set; }
    [Key(1)] public float L { get; set; }
    [Key(2)] public float T { get; set; }
    [Key(3)] public float R { get; set; }
    [Key(4)] public float B { get; set; }
}

[MessagePackObject]
public partial class CharBoxesSoa
{
    [Key(0)] public int Status { get; set; }
    [Key(1)] public uint[] Codepoints { get; set; } = [];
    [Key(2)] public float[] Boxes { get; set; } = [];     // l,t,r,b interleaved
}

public static unsafe partial class Verbs
{
    public static int MsgPackBench(string[] a)
    {
        int iters = int.Parse(Opt(a, "--iters", "2000"));
        PrepareDirs("fdd");
        Directory.CreateDirectory(Env.Gen);
        string dense = PdfGen.BuildDensePage(Path.Combine(Env.Gen, "dense-page.pdf"));
        var o = Base(Isolation.AppContainer, "msgpack", "fdd"); o.FilePath = null;
        using var ep = new EngineProcess(o); ep.Start();
        var op = ep.OpenDocumentFromPath(dense);
        if (op.status != 0) throw new Exception("open failed");

        // real round trip through the pipe: engine -> broker, custom binary framing
        var rtt = new List<double>(); byte[]? lastPayload = null; int nChars = 0;
        for (int i = 0; i < 200; i++)
        {
            long t0 = Clock.Now();
            var w = new PayloadWriter().I32(0);
            // Call() hides the payload buffer; re-implement minimal framing here to keep the raw bytes
            FrameIO.Write(ep.Pipe, (ushort)Msg.GetCharBoxes, 9000u + (uint)i, w.Span); ep.Pipe.Flush();
            Span<byte> hdr = stackalloc byte[Proto.HeaderSize];
            FrameIO.Read(ep.Pipe, hdr, out _, out _, out lastPayload);
            rtt.Add(Clock.Ms(Clock.Now() - t0));
        }
        var rd = new PayloadReader(lastPayload!); rd.I32(); nChars = rd.I32();
        var cps = new uint[nChars]; var f = new float[nChars * 4];
        for (int i = 0; i < nChars; i++) { cps[i] = rd.U32(); f[i * 4] = rd.F32(); f[i * 4 + 1] = rd.F32(); f[i * 4 + 2] = rd.F32(); f[i * 4 + 3] = rd.F32(); }
        Console.WriteLine($"dense page: {nChars} characters; GetCharBoxes custom-binary payload {lastPayload!.Length} bytes; RTT through pipe incl. engine work: {Stats.Of(rtt)}");

        var aos = new CharBoxesAos { Boxes = Enumerable.Range(0, nChars).Select(i => new CharBox { Codepoint = cps[i], L = f[i * 4], T = f[i * 4 + 1], R = f[i * 4 + 2], B = f[i * 4 + 3] }).ToArray() };
        var soa = new CharBoxesSoa { Codepoints = cps, Boxes = f };
        // Source-generated resolver (MessagePack 3.x) first, dynamic standard resolver as fallback
        var opts = MessagePackSerializerOptions.Standard.WithResolver(CompositeResolver.Create(GeneratedMessagePackResolver.Instance, StandardResolver.Instance));

        var sbOut = new StringBuilder();
        void Measure(string name, Func<byte[]> encode, Action<byte[]> decode)
        {
            var enc = new List<double>(); var dec = new List<double>(); byte[] bytes = encode();
            for (int i = 0; i < 200; i++) { encode(); decode(bytes); }   // warm-up (JIT)
            for (int i = 0; i < iters; i++) { long t0 = Clock.Now(); bytes = encode(); long t1 = Clock.Now(); decode(bytes); long t2 = Clock.Now(); enc.Add(Clock.Us(t1 - t0)); dec.Add(Clock.Us(t2 - t1)); }
            var se = Stats.Of(enc); var sd = Stats.Of(dec);
            Console.WriteLine($"{name,-44} size {bytes.Length,7} B | encode µs {se} | decode µs {sd}");
            sbOut.AppendLine($"| {name} | {bytes.Length} | {se.P50:F1} | {se.P95:F1} | {se.P99:F1} | {se.Max:F1} | {sd.P50:F1} | {sd.P95:F1} | {sd.P99:F1} | {sd.Max:F1} |");
        }
        Measure("MessagePack-CSharp, array of structs", () => MessagePackSerializer.Serialize(aos, opts), b => { var r = MessagePackSerializer.Deserialize<CharBoxesAos>(b, opts); if (r.Boxes.Length != nChars) throw new Exception(); });
        Measure("MessagePack-CSharp, struct of arrays", () => MessagePackSerializer.Serialize(soa, opts), b => { var r = MessagePackSerializer.Deserialize<CharBoxesSoa>(b, opts); if (r.Codepoints.Length != nChars) throw new Exception(); });
        Measure("custom binary (bounds-checked reader)", () =>
        {
            var w = new PayloadWriter().I32(0).I32(nChars);
            for (int i = 0; i < nChars; i++) w.U32(cps[i]).F32(f[i * 4]).F32(f[i * 4 + 1]).F32(f[i * 4 + 2]).F32(f[i * 4 + 3]);
            return w.Span.ToArray();
        }, b =>
        {
            var r = new PayloadReader(b); r.I32(); int n = r.I32();
            if (n < 0 || n > 1_000_000 || n * 20 > r.Remaining) throw new ProtocolException("count");
            var c2 = new uint[n]; var f2 = new float[n * 4];
            for (int i = 0; i < n; i++) { c2[i] = r.U32(); f2[i * 4] = r.F32(); f2[i * 4 + 1] = r.F32(); f2[i * 4 + 2] = r.F32(); f2[i * 4 + 3] = r.F32(); }
        });
        Console.WriteLine("resolver in use: " + typeof(GeneratedMessagePackResolver).FullName + " (source generated)");
        File.WriteAllText(Path.Combine(Env.Out, "msgpack.md"), $"Dense page: {nChars} chars; pipe RTT for GetCharBoxes: {Stats.Of(rtt)}\n\n| Encoding | Bytes | enc p50 µs | enc p95 | enc p99 | enc max | dec p50 µs | dec p95 | dec p99 | dec max |\n|---|---|---|---|---|---|---|---|---|---|\n" + sbOut, new UTF8Encoding(false));
        return 0;
    }
}
