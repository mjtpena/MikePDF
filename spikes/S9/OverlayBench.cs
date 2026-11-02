using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace S9;

/// <summary>S9 question 1: cost of drawing N annotations as Win2D vector overlays above page tiles, per frame, GPU flushed.</summary>
internal static class OverlayBench
{
    const int VW = 1600, VH = 900, TS = 512;
    const float PageW = 612, PageH = 792;
    static readonly double ToMs = 1000.0 / Stopwatch.Frequency;
    static readonly Color Bg = Color.FromArgb(255, 0xEE, 0xEE, 0xEE);

    sealed class Tiles
    {
        public readonly List<(int x, int y, CanvasBitmap bmp)> List = [];
        public void Build(CanvasDevice dev, nint page, double s, int baseX, int baseY)
        {
            var pw = (int)Math.Round(PageW * s); var ph = (int)Math.Round(PageH * s);
            int x0 = Math.Max(0, (baseX - 512) / TS * TS), y0 = Math.Max(0, (baseY - 512) / TS * TS);
            int x1 = Math.Min(pw, baseX + VW + 512), y1 = Math.Min(ph, baseY + VH + 512);
            for (var ty = y0; ty < y1; ty += TS)
                for (var tx = x0; tx < x1; tx += TS)
                {
                    var img = PdfRender.Viewport(page, s, tx, ty, TS, TS);
                    List.Add((tx, ty, CanvasBitmap.CreateFromBytes(dev, img.Px, TS, TS, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96f, CanvasAlphaMode.Ignore)));
                }
        }
        public void Draw(CanvasDrawingSession ds, int viewX, int viewY)
        {
            foreach (var (x, y, b) in List)
            {
                var dx = x - viewX; var dy = y - viewY;
                if (dx > VW || dy > VH || dx + TS < 0 || dy + TS < 0) continue;
                ds.DrawImage(b, new Rect(dx, dy, TS, TS), new Rect(0, 0, TS, TS), 1f, CanvasImageInterpolation.NearestNeighbor);
            }
        }
        public int Visible(int viewX, int viewY) => List.Count(t => !(t.x - viewX > VW || t.y - viewY > VH || t.x - viewX + TS < 0 || t.y - viewY + TS < 0));
    }

    record Row(string Device, string Kind, string Strategy, int N, double Zoom, int Frames, double P50, double P95, double Max, double SubmitP50, double FloorP50, double OverP50, double BuildMs);

    static double Pct(List<double> v, double p) { v.Sort(); return v[Math.Min(v.Count - 1, (int)Math.Ceiling(p * v.Count) - 1)]; }

    public static void Run(string[] args)
    {
        var devNames = args.Where(a => !a.StartsWith("--")).ToArray();
        if (devNames.Length == 0) devNames = ["dgpu", "igpu", "warp"];
        var quick = args.Contains("--quick");
        var b = BaseDoc.Build(1);
        using var doc = new PdfDoc(b.Bytes);
        var page = Pdfium.FPDF_LoadPage(doc.Handle, 0);
        var ns = quick ? new[] { 10, 1000 } : new[] { 10, 100, 1000, 5000 };
        var zooms = new[] { 1.0, 4.0, 16.0 };
        var rows = new List<Row>();
        var adapters = Gpu.Adapters();
        var info = new List<string>();

        foreach (var dn in devNames)
        {
            var idx = dn switch { "dgpu" => 0, "igpu" => 1, "warp" => -1, _ => -2 };
            var devName = dn switch { "dgpu" => adapters[0].Name, "igpu" => adapters[1].Name, "warp" => "WARP (software)", _ => "shared default" };
            info.Add($"{dn}: {devName}");
            Console.WriteLine($"=== device {dn} ({devName})");
            using var dev = Gpu.Create(idx);
            using var target = new CanvasRenderTarget(dev, VW, VH, 96);
            using var tileRT = new CanvasRenderTarget(dev, VW, VH, 96);
            using var hlRT = new CanvasRenderTarget(dev, VW, VH, 96);
            // Time cap per cell: software rendering is slow, don't let it dominate the run.
            var capMs = dn == "warp" ? 2500 : 4000;

            foreach (var z in zooms)
            {
                var s = z * 96.0 / 72.0;
                var (bx, by) = z == 1 ? (-392, 0) : z == 4 ? (800, 1500) : (4000, 6000);
                var tiles = new Tiles();
                var tb = Stopwatch.GetTimestamp();
                tiles.Build(dev, page, s, bx, by);
                Console.WriteLine($"  zoom {z}x: {tiles.List.Count} tiles built in {(Stopwatch.GetTimestamp() - tb) * ToMs:F0} ms, {tiles.Visible(bx, by)} visible");

                (int vx, int vy) View(int i) => (bx + ((i * 7) % 257) - 128, by + ((i * 5) % 251) - 125);
                Matrix3x2 M(int vx, int vy) => new((float)s, 0, 0, -(float)s, -vx, (float)(PageH * s) - vy);
                Rect Cull(int vx, int vy) => new((vx) / s, (PageH * s - vy - VH) / s, VW / s, VH / s);

                // Generic cell runner: frame(ds, i) draws the overlay (and for multiply cells does its own composition).
                void Cell(string kind, string strat, int n, double buildMs, Action<CanvasDrawingSession, int> overlayOnly, Action<CanvasDrawingSession, int>? fullFrame = null)
                {
                    var frames = new List<double>(); var submits = new List<double>();
                    var total = Stopwatch.GetTimestamp();
                    for (var i = 0; i < 400; i++)
                    {
                        var t0 = Stopwatch.GetTimestamp();
                        using (var ds = target.CreateDrawingSession())
                        {
                            if (fullFrame != null) fullFrame(ds, i);
                            else
                            {
                                ds.Clear(Bg); var (vx, vy) = View(i); tiles.Draw(ds, vx, vy);
                                overlayOnly(ds, i);
                            }
                        }
                        var t1 = Stopwatch.GetTimestamp(); Gpu.Flush(target); var t2 = Stopwatch.GetTimestamp();
                        if (i >= 20) { frames.Add((t2 - t0) * ToMs); submits.Add((t1 - t0) * ToMs); }
                        if (i >= 40 && (t2 - total) * ToMs > capMs) break;
                    }
                    rows.Add(new Row(dn, kind, strat, n, z, frames.Count, Pct(frames, .5), Pct(frames, .95), frames.Max(), Pct(submits, .5), 0, 0, buildMs));
                    Console.WriteLine($"    {kind,-9} {strat,-22} N={n,-5} p50 {rows[^1].P50,7:F2} p95 {rows[^1].P95,7:F2} max {rows[^1].Max,7:F2} ms (submit p50 {rows[^1].SubmitP50:F2}) build {buildMs:F0} ms");
                }

                // Floor: tiles only.
                Cell("floor", "tiles only", 0, 0, (_, _) => { });
                // Floor with a flush of an empty frame (clear only) to expose the readback overhead.
                {
                    var f = new List<double>();
                    for (var i = 0; i < 120; i++) { var t0 = Stopwatch.GetTimestamp(); using (var ds = target.CreateDrawingSession()) ds.Clear(Bg); Gpu.Flush(target); if (i >= 20) f.Add((Stopwatch.GetTimestamp() - t0) * ToMs); }
                    rows.Add(new Row(dn, "floor", "empty frame + flush", 0, z, f.Count, Pct(f, .5), Pct(f, .95), f.Max(), 0, 0, 0, 0));
                    Console.WriteLine($"    floor     empty frame + flush           p50 {rows[^1].P50:F3} ms");
                }

                foreach (var n in ns)
                {
                    // ---------- INK ----------
                    var strokes = Gen.Strokes(n, 1000 + n);
                    var cols = Gen.InkColours.Select(c => c.ToColor()).ToArray();
                    var bounds = strokes.Select(st => st.Bounds()).ToArray();

                    // A: rebuild geometry each frame for visible strokes only
                    Cell("ink", "A rebuild+cull", n, 0, (ds, i) =>
                    {
                        var (vx, vy) = View(i); var cr = Cull(vx, vy); ds.Transform = M(vx, vy);
                        for (var k = 0; k < n; k++)
                        {
                            var bb = bounds[k]; if (bb.r < cr.X || bb.l > cr.X + cr.Width || bb.t < cr.Y || bb.b > cr.Y + cr.Height) continue;
                            using var g = strokes[k].ToGeometry(dev); ds.FillGeometry(g, cols[k % 3]);
                        }
                        ds.Transform = Matrix3x2.Identity;
                    });

                    // B: cached geometry + cull
                    var tb2 = Stopwatch.GetTimestamp();
                    var geoms = strokes.Select(st => st.ToGeometry(dev)).ToArray();
                    var buildGeom = (Stopwatch.GetTimestamp() - tb2) * ToMs;
                    Cell("ink", "B cached+cull", n, buildGeom, (ds, i) =>
                    {
                        var (vx, vy) = View(i); var cr = Cull(vx, vy); ds.Transform = M(vx, vy);
                        for (var k = 0; k < n; k++)
                        {
                            var bb = bounds[k]; if (bb.r < cr.X || bb.l > cr.X + cr.Width || bb.t < cr.Y || bb.b > cr.Y + cr.Height) continue;
                            ds.FillGeometry(geoms[k], cols[k % 3]);
                        }
                        ds.Transform = Matrix3x2.Identity;
                    });

                    // C: cached geometry, no cull (D2D clips)
                    Cell("ink", "C cached no cull", n, buildGeom, (ds, i) =>
                    {
                        var (vx, vy) = View(i); ds.Transform = M(vx, vy);
                        for (var k = 0; k < n; k++) ds.FillGeometry(geoms[k], cols[k % 3]);
                        ds.Transform = Matrix3x2.Identity;
                    });

                    // D: one retained command list (page space), replayed with a transform
                    var tb3 = Stopwatch.GetTimestamp();
                    using var cl = new CanvasCommandList(dev);
                    using (var cds = cl.CreateDrawingSession()) for (var k = 0; k < n; k++) cds.FillGeometry(geoms[k], cols[k % 3]);
                    var buildCl = (Stopwatch.GetTimestamp() - tb3) * ToMs;
                    Cell("ink", "D command list", n, buildCl, (ds, i) => { var (vx, vy) = View(i); ds.Transform = M(vx, vy); ds.DrawImage(cl); ds.Transform = Matrix3x2.Identity; });

                    // E: one grouped geometry per colour
                    var tb4 = Stopwatch.GetTimestamp();
                    var groups = Enumerable.Range(0, 3).Select(c => CanvasGeometry.CreateGroup(dev, geoms.Where((_, k) => k % 3 == c).ToArray(), CanvasFilledRegionDetermination.Winding)).ToArray();
                    var buildGroup = (Stopwatch.GetTimestamp() - tb4) * ToMs;
                    Cell("ink", "E grouped per colour", n, buildGroup, (ds, i) => { var (vx, vy) = View(i); ds.Transform = M(vx, vy); for (var c = 0; c < 3; c++) ds.FillGeometry(groups[c], cols[c]); ds.Transform = Matrix3x2.Identity; });

                    foreach (var g in groups) g.Dispose();

                    // F: CanvasCachedGeometry (realised tessellation at this zoom's tolerance) + cull
                    var tb6 = Stopwatch.GetTimestamp();
                    var cached = geoms.Select(g => CanvasCachedGeometry.CreateFill(g, (float)(0.25 / s))).ToArray();
                    var buildCached = (Stopwatch.GetTimestamp() - tb6) * ToMs;
                    Cell("ink", "F cached realisation+cull", n, buildCached, (ds, i) =>
                    {
                        var (vx, vy) = View(i); var cr = Cull(vx, vy); ds.Transform = M(vx, vy);
                        for (var k = 0; k < n; k++)
                        {
                            var bb = bounds[k]; if (bb.r < cr.X || bb.l > cr.X + cr.Width || bb.t < cr.Y || bb.b > cr.Y + cr.Height) continue;
                            ds.DrawCachedGeometry(cached[k], cols[k % 3]);
                        }
                        ds.Transform = Matrix3x2.Identity;
                    });

                    // G: raster cache of the overlay: rasterise once into a viewport-sized layer (the settle cost), then each frame is a blit.
                    using (var layer = new CanvasRenderTarget(dev, VW, VH, 96))
                    {
                        var t0 = Stopwatch.GetTimestamp();
                        using (var ls = layer.CreateDrawingSession())
                        {
                            ls.Clear(Microsoft.UI.Colors.Transparent); var cr = Cull(bx, by); ls.Transform = M(bx, by);
                            for (var k = 0; k < n; k++) { var bb = bounds[k]; if (bb.r < cr.X || bb.l > cr.X + cr.Width || bb.t < cr.Y || bb.b > cr.Y + cr.Height) continue; ls.DrawCachedGeometry(cached[k], cols[k % 3]); }
                        }
                        Gpu.Flush(layer); var settle = (Stopwatch.GetTimestamp() - t0) * ToMs;
                        Cell("ink", "G raster cache (blit)", n, settle, (ds, i) => { ds.DrawImage(layer, 0, 0); });
                    }
                    foreach (var c in cached) c.Dispose();
                    foreach (var g in geoms) g.Dispose();

                    // ---------- HIGHLIGHTS ----------
                    var quads = Gen.Quads(n, 2000 + n);
                    var hcols = Gen.HlColours.Select(c => c.ToColor(1f)).ToArray();
                    // H0: normal alpha-over (not the right look; shows the cost of rectangles alone)
                    Cell("highlight", "H0 SourceOver rects", n, 0, (ds, i) =>
                    {
                        var (vx, vy) = View(i); ds.Transform = M(vx, vy);
                        for (var k = 0; k < n; k++) { var q = quads[k]; ds.FillRectangle(q.L, q.B, q.R - q.L, q.T - q.B, Color.FromArgb(128, hcols[k % 3].R, hcols[k % 3].G, hcols[k % 3].B)); }
                        ds.Transform = Matrix3x2.Identity;
                    });

                    // H1: Multiply: tiles into tileRT, highlight rects into hlRT, BlendEffect(Multiply) into the target
                    Cell("highlight", "H1 Multiply (2 RTs)", n, 0, null!, (ds, i) =>
                    {
                        var (vx, vy) = View(i);
                        using (var t = tileRT.CreateDrawingSession()) { t.Clear(Bg); tiles.Draw(t, vx, vy); }
                        using (var h = hlRT.CreateDrawingSession())
                        {
                            h.Clear(Microsoft.UI.Colors.Transparent); h.Transform = M(vx, vy);
                            for (var k = 0; k < n; k++) { var q = quads[k]; h.FillRectangle(q.L, q.B, q.R - q.L, q.T - q.B, hcols[k % 3]); }
                        }
                        using var fx = new BlendEffect { Background = tileRT, Foreground = hlRT, Mode = BlendEffectMode.Multiply };
                        ds.DrawImage(fx);
                    });

                    // H3: Multiply with a retained highlight command list (page space) transformed by an effect, no hlRT redraw
                    var tb5 = Stopwatch.GetTimestamp();
                    using var hcl = new CanvasCommandList(dev);
                    using (var hds = hcl.CreateDrawingSession()) for (var k = 0; k < n; k++) { var q = quads[k]; hds.FillRectangle(q.L, q.B, q.R - q.L, q.T - q.B, hcols[k % 3]); }
                    var buildH = (Stopwatch.GetTimestamp() - tb5) * ToMs;
                    Cell("highlight", "H3 Multiply (retained)", n, buildH, null!, (ds, i) =>
                    {
                        var (vx, vy) = View(i);
                        using (var t = tileRT.CreateDrawingSession()) { t.Clear(Bg); tiles.Draw(t, vx, vy); }
                        using var xf = new Transform2DEffect { Source = hcl, TransformMatrix = M(vx, vy) };
                        using var fx = new BlendEffect { Background = tileRT, Foreground = xf, Mode = BlendEffectMode.Multiply };
                        ds.DrawImage(fx);
                    });
                    if (n == 1000 || n == 100)
                    {
                        // MIX: n highlights (H3) plus n ink strokes (B cached+cull) in one frame
                        var g2 = strokes.Select(st => st.ToGeometry(dev)).ToArray();
                        Cell("mixed", "H3 + B", n, 0, null!, (ds, i) =>
                        {
                            var (vx, vy) = View(i);
                            using (var t = tileRT.CreateDrawingSession()) { t.Clear(Bg); tiles.Draw(t, vx, vy); }
                            using var xf = new Transform2DEffect { Source = hcl, TransformMatrix = M(vx, vy) };
                            using var fx = new BlendEffect { Background = tileRT, Foreground = xf, Mode = BlendEffectMode.Multiply };
                            ds.DrawImage(fx);
                            var cr = Cull(vx, vy); ds.Transform = M(vx, vy);
                            for (var k = 0; k < n; k++) { var bb = bounds[k]; if (bb.r < cr.X || bb.l > cr.X + cr.Width || bb.t < cr.Y || bb.b > cr.Y + cr.Height) continue; ds.FillGeometry(g2[k], cols[k % 3]); }
                            ds.Transform = Matrix3x2.Identity;
                        });
                        foreach (var g in g2) g.Dispose();
                    }
                }
                foreach (var t in tiles.List) t.bmp.Dispose();
            }

            // Document-scale: 50,000 ink strokes: build time, private memory, per-page cull cost is the same as N=5000 above.
            if (dn == "dgpu" || dn == "igpu")
            {
                GC.Collect(); GC.WaitForPendingFinalizers(); var mem0 = Process.GetCurrentProcess().PrivateMemorySize64;
                var tb = Stopwatch.GetTimestamp();
                var st = Gen.Strokes(50000, 777);
                var tg = Stopwatch.GetTimestamp();
                var gs = st.Select(x => x.ToGeometry(dev)).ToArray();
                // force realisation by a single draw of all
                using (var ds = target.CreateDrawingSession()) { ds.Transform = Matrix3x2.CreateScale(0.4f); foreach (var g in gs) ds.FillGeometry(g, Microsoft.UI.Colors.Black); }
                Gpu.Flush(target);
                var te = Stopwatch.GetTimestamp();
                var mem1 = Process.GetCurrentProcess().PrivateMemorySize64;
                var line = $"{dn}: 50,000 strokes (48 pts each): model {(tg - tb) * ToMs:F0} ms, geometry {(te - tg) * ToMs:F0} ms (incl. one full draw), private bytes +{(mem1 - mem0) / 1048576.0:F0} MB";
                Console.WriteLine("  " + line); info.Add(line);
                foreach (var g in gs) g.Dispose();
            }
        }

        // Fix up: overlay cost = p50 - floor p50 for the same device and zoom ("tiles only"). Multiply cells include their own tile pass, so subtract the same floor.
        var sb = new StringBuilder("device,kind,strategy,n,zoom,frames,p50_ms,p95_ms,max_ms,submit_p50_ms,floor_p50_ms,overlay_cost_p50_ms,build_ms\n");
        var fixedRows = rows.Select(r =>
        {
            var floor = rows.First(x => x.Device == r.Device && x.Zoom == r.Zoom && x.Strategy == "tiles only").P50;
            return r with { FloorP50 = floor, OverP50 = r.P50 - floor };
        });
        foreach (var r in fixedRows) sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{r.Device},{r.Kind},{r.Strategy},{r.N},{r.Zoom},{r.Frames},{r.P50:F3},{r.P95:F3},{r.Max:F3},{r.SubmitP50:F3},{r.FloorP50:F3},{r.OverP50:F3},{r.BuildMs:F1}"));
        File.WriteAllText(Path.Combine(Program.Out, quick ? "s9-overlay-quick.csv" : "s9-overlay.csv"), sb.ToString());
        File.WriteAllLines(Path.Combine(Program.Out, "s9-overlay-devices.txt"), info);
        Console.WriteLine("written " + Program.Out);
    }
}
