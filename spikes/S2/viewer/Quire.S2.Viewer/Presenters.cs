using System.Collections.Concurrent;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace Quire.S2.Viewer;

/// <summary>Reusable exact-size byte[] buffers for CanvasBitmap.CreateFromBytes (the API takes a managed array, so one memcpy slot -> array is part of the upload cost and is reported separately).</summary>
static class BufferPool
{
    static readonly ConcurrentDictionary<int, ConcurrentBag<byte[]>> Pools = new();
    public static byte[] Rent(int n) => Pools.GetOrAdd(n, _ => new ConcurrentBag<byte[]>()).TryTake(out var b) ? b : new byte[n];
    public static void Return(byte[] b) => Pools.GetOrAdd(b.Length, _ => new ConcurrentBag<byte[]>()).Add(b);
}

public static class GpuSync
{
    /// <summary>Block until all work previously submitted on this device has executed: draw a pixel into a 1x1 target and read it back (same immediate context, in-order).</summary>
    public static void Sync(CanvasDevice device)
    {
        using var rt = new CanvasRenderTarget(device, 1, 1, 96);
        using (var ds = rt.CreateDrawingSession()) ds.Clear(Windows.UI.Color.FromArgb(255, 1, 2, 3));
        rt.GetPixelBytes();
    }
}

/// <summary>Presenter A: Win2D CanvasVirtualControl, one CanvasBitmap per tile (CreateFromBytes), redraw of the whole visible region each frame.</summary>
public sealed unsafe class Win2DPresenter : IPresenter
{
    readonly CanvasVirtualControl _cvc;
    public readonly CanvasDevice Device;
    readonly double _rs;
    FrameModel? _model;
    readonly Samples _drawMs = new();
    int _drawCount; long _resources; long _lastSig = -1;
    public string Name => "win2d";
    public FrameworkElement Element => _cvc;
    public Samples UpdateMs => _drawMs;
    public int DrawCount => Interlocked.Exchange(ref _drawCount, 0);
    public long ResourceCount => Interlocked.Read(ref _resources);
    public string CustomDeviceNote = "";
    static readonly Windows.UI.Color Bg = Windows.UI.Color.FromArgb(255, 0x30, 0x30, 0x30), Paper = Windows.UI.Color.FromArgb(255, 255, 255, 255);

    public Win2DPresenter(double rasterScale)
    {
        _rs = rasterScale;
        Device = CanvasDevice.GetSharedDevice();
        _cvc = new CanvasVirtualControl { ClearColor = Bg, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        try { _cvc.CustomDevice = Device; CustomDeviceNote = "CustomDevice = CanvasDevice.GetSharedDevice()"; } catch (Exception ex) { CustomDeviceNote = "CustomDevice not set: " + ex.Message; }
        _cvc.RegionsInvalidated += OnRegions;
    }

    public object Upload(byte* bgra, int w, int h, bool thumb, out double copyMs, out double uploadMs)
    {
        long t0 = Clock2.Now();
        var buf = BufferPool.Rent(w * h * 4);
        new ReadOnlySpan<byte>(bgra, buf.Length).CopyTo(buf);
        long t1 = Clock2.Now();
        var bmp = CanvasBitmap.CreateFromBytes(Device, buf, w, h, DirectXPixelFormat.B8G8R8A8UIntNormalized, (float)(96 * _rs), CanvasAlphaMode.Ignore);
        long t2 = Clock2.Now();
        BufferPool.Return(buf);
        copyMs = Clock2.Ms(t1 - t0); uploadMs = Clock2.Ms(t2 - t1);
        Interlocked.Increment(ref _resources);
        return bmp;
    }

    public void FreeResource(TileEntry e) { (e.Gpu as CanvasBitmap)?.Dispose(); Interlocked.Decrement(ref _resources); }

    public void Update(FrameModel m)
    {
        _model = m;
        long sig = HashCode.Combine(m.ScrollY, m.Gesture, m.SharpCells, m.LowResCells, m.VisiblePages, m.Pages.Count);
        long readySum = 0; foreach (var p in m.Pages) { readySum += p.Tiles.Count * 31 + (p.Thumb != null ? 7 : 0); }
        sig = sig * 1000003 + readySum;
        if (sig == _lastSig) return;
        _lastSig = sig;
        _cvc.Invalidate();
    }

    void OnRegions(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        var m = _model; if (m == null) return;
        long t0 = Clock2.Now();
        foreach (var region in args.InvalidatedRegions)
        {
            using var ds = sender.CreateDrawingSession(region);
            ds.Clear(Bg);
            float g = m.Gesture <= 0 ? 1 : m.Gesture;
            if (g != 1f) ds.Transform = Matrix3x2.CreateScale(g, g, new Vector2((float)(m.VW / 2 / _rs), (float)(m.VH / 2 / _rs)));
            float r = (float)_rs;
            foreach (var p in m.Pages)
            {
                var dest = new Rect(p.X / r, p.Y / r, p.W / r, p.H / r);
                ds.FillRectangle(dest, Paper);
                if (p.Thumb?.Gpu is CanvasBitmap tb && p.MissingCells > 0) ds.DrawImage(tb, dest, tb.Bounds, 1f, CanvasImageInterpolation.Linear);
                foreach (var t in p.Tiles) if (t.E.Gpu is CanvasBitmap b) ds.DrawImage(b, t.X / r, t.Y / r);
            }
        }
        _drawMs.Add(Clock2.MsSince(t0));
        Interlocked.Increment(ref _drawCount);
    }

    public void Dispose() { _cvc.RegionsInvalidated -= OnRegions; _cvc.RemoveFromVisualTree(); }
}

/// <summary>Presenter B: Composition. One CompositionDrawingSurface + SpriteVisual per tile; scrolling and zoom gestures are compositor transforms on one container.</summary>
public sealed unsafe class CompPresenter : IPresenter
{
    readonly Grid _host = new();
    public readonly CanvasDevice Device;
    readonly double _rs;
    Compositor _comp = null!;
    CompositionGraphicsDevice _gd = null!;
    ContainerVisual _clipRoot = null!, _content = null!, _papers = null!, _thumbs = null!, _tiles = null!;
    CompositionColorBrush _paperBrush = null!;
    readonly Dictionary<int, (SpriteVisual v, long stamp)> _pageVisuals = new();
    readonly List<TileEntry> _attached = new();
    readonly Samples _updMs = new();
    long _resources; long _retries;
    public long Retries => Interlocked.Read(ref _retries);
    public bool Ready { get; private set; }
    public string Name => "comp";
    public FrameworkElement Element => _host;
    public Samples UpdateMs => _updMs;
    public int DrawCount => 0;
    public long ResourceCount => Interlocked.Read(ref _resources);

    sealed class CompTile { public CompositionDrawingSurface Surface = null!; public SpriteVisual Visual = null!; public CompositionSurfaceBrush Brush = null!; public ContainerVisual? Parent; }

    public CompPresenter(double rasterScale)
    {
        _rs = rasterScale;
        Device = CanvasDevice.GetSharedDevice();
    }

    /// <summary>UI thread, after the host element is in the tree.</summary>
    public void Initialize()
    {
        var hv = ElementCompositionPreview.GetElementVisual(_host);
        _comp = hv.Compositor;
        _gd = CanvasComposition.CreateCompositionGraphicsDevice(_comp, Device);
        _clipRoot = _comp.CreateContainerVisual(); _clipRoot.Clip = _comp.CreateInsetClip();
        _content = _comp.CreateContainerVisual(); _papers = _comp.CreateContainerVisual(); _thumbs = _comp.CreateContainerVisual(); _tiles = _comp.CreateContainerVisual();
        _content.Children.InsertAtTop(_papers); _content.Children.InsertAtTop(_thumbs); _content.Children.InsertAtTop(_tiles);
        _clipRoot.Children.InsertAtTop(_content);
        _paperBrush = _comp.CreateColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
        ElementCompositionPreview.SetElementChildVisual(_host, _clipRoot);
        Ready = true;
    }

    public object Upload(byte* bgra, int w, int h, bool thumb, out double copyMs, out double uploadMs)
    {
        long t0 = Clock2.Now();
        var buf = BufferPool.Rent(w * h * 4);
        new ReadOnlySpan<byte>(bgra, buf.Length).CopyTo(buf);
        long t1 = Clock2.Now();
        using var bmp = CanvasBitmap.CreateFromBytes(Device, buf, w, h, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96f, CanvasAlphaMode.Ignore);
        var surf = _gd.CreateDrawingSurface(new Size(w, h), Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, Microsoft.Graphics.DirectX.DirectXAlphaMode.Ignore);
        CanvasDrawingSession? dsx = null;
        for (int attempt = 0; ; attempt++)
        {   // CreateDrawingSession(surface) throws a transient InvalidOperationException (0x80131509) from a background thread in a minority of calls: retry and count
            try { dsx = CanvasComposition.CreateDrawingSession(surf); break; }
            catch (System.Runtime.InteropServices.COMException ex) when ((uint)ex.HResult == 0x80131509 && attempt < 8) { Interlocked.Increment(ref _retries); Thread.Sleep(1); }
        }
        using (dsx) { dsx.Blend = CanvasBlend.Copy; dsx.DrawImage(bmp, 0, 0); }
        var brush = _comp.CreateSurfaceBrush(surf); brush.Stretch = CompositionStretch.Fill;
        var sv = _comp.CreateSpriteVisual(); sv.Size = new Vector2((float)(w / _rs), (float)(h / _rs)); sv.Brush = brush;
        long t2 = Clock2.Now();
        BufferPool.Return(buf);
        copyMs = Clock2.Ms(t1 - t0); uploadMs = Clock2.Ms(t2 - t1);
        Interlocked.Increment(ref _resources);
        return new CompTile { Surface = surf, Visual = sv, Brush = brush };
    }

    public void FreeResource(TileEntry e)
    {
        if (e.Gpu is CompTile t)
        {
            if (e.Attached) { t.Parent?.Children.Remove(t.Visual); _attached.Remove(e); e.Attached = false; }
            t.Visual.Dispose(); t.Brush.Dispose(); t.Surface.Dispose();
        }
        Interlocked.Decrement(ref _resources);
    }

    public void Update(FrameModel m)
    {
        long t0 = Clock2.Now();
        float rs = (float)_rs;
        _clipRoot.Size = new Vector2((float)(m.VW / _rs), (float)(m.VH / _rs));
        float g = m.Gesture <= 0 ? 1 : m.Gesture;
        _content.CenterPoint = new Vector3((float)((m.VW / 2 + m.ScrollX) / _rs), (float)((m.VH / 2 + m.ScrollY) / _rs), 0);
        _content.Scale = new Vector3(g, g, 1);
        _content.Offset = new Vector3((float)(-m.ScrollX / _rs), (float)(-m.ScrollY / _rs), 0);
        foreach (var p in m.Pages)
        {
            // page paper
            if (!_pageVisuals.TryGetValue(p.Page, out var pv))
            {
                var sv = _comp.CreateSpriteVisual(); sv.Brush = _paperBrush; sv.Size = new Vector2(p.W / rs, p.H / rs); sv.Offset = new Vector3(p.X / rs + (float)(m.ScrollX / _rs), (p.Y + (float)m.ScrollY) / rs, 0);
                _papers.Children.InsertAtTop(sv); pv = (sv, m.Frame);
            }
            _pageVisuals[p.Page] = (pv.v, m.Frame);
            if (p.Thumb != null && !p.Thumb.Attached && p.Thumb.Gpu is CompTile tt) { tt.Visual.Size = new Vector2(p.W / rs, p.H / rs); tt.Visual.Offset = new Vector3(p.X / rs + (float)(m.ScrollX / _rs), (p.Y + (float)m.ScrollY) / rs, 0); Attach(p.Thumb, tt, _thumbs); }
            foreach (var t in p.Tiles)
                if (!t.E.Attached && t.E.Gpu is CompTile ct) { ct.Visual.Offset = new Vector3((t.X + (float)m.ScrollX) / rs, (t.Y + (float)m.ScrollY) / rs, 0); Attach(t.E, ct, _tiles); }
        }
        // detach what the model no longer mentions
        for (int i = _attached.Count - 1; i >= 0; i--)
        {
            var e = _attached[i];
            if (e.ModelStamp < m.Frame && e.Gpu is CompTile t) { t.Parent?.Children.Remove(t.Visual); e.Attached = false; _attached.RemoveAt(i); }
        }
        if (_pageVisuals.Count > 0 && (m.Frame & 15) == 0)
        {
            var dead = _pageVisuals.Where(kv => kv.Value.stamp < m.Frame - 32).Select(kv => kv.Key).ToList();
            foreach (var k in dead) { _papers.Children.Remove(_pageVisuals[k].v); _pageVisuals[k].v.Dispose(); _pageVisuals.Remove(k); }
        }
        _updMs.Add(Clock2.MsSince(t0));
    }

    /// <summary>Memory test only (UI thread): put every tile's sprite into the visual tree at (0,0) so the compositor really realises the surfaces.</summary>
    public void AttachForTest(IEnumerable<TileEntry> entries)
    {
        foreach (var e in entries) if (e.Gpu is CompTile t && !e.Attached) { t.Visual.Offset = Vector3.Zero; Attach(e, t, _tiles); }
    }

    void Attach(TileEntry e, CompTile t, ContainerVisual parent)
    {
        parent.Children.InsertAtTop(t.Visual); t.Parent = parent; e.Attached = true; _attached.Add(e);
    }

    public void Dispose() { }
}
