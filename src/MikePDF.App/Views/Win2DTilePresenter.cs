using System.Collections.Concurrent;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using MikePDF.Core.Tiles;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace MikePDF.App.Views;

/// <summary>A Win2D bitmap in the tile cache.</summary>
internal sealed class Win2DTileSurface(CanvasBitmap bitmap) : ITileSurface
{
    public CanvasBitmap Bitmap { get; } = bitmap;

    public int Width { get; } = (int)bitmap.SizeInPixels.Width;

    public int Height { get; } = (int)bitmap.SizeInPixels.Height;

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// ADR-0006 decision 1: a Win2D <see cref="CanvasVirtualControl"/> behind <see cref="ITilePresenter"/>. Uploads happen off the UI thread
/// (<see cref="CreateSurface"/>); drawing happens in <c>RegionsInvalidated</c> on the UI thread and only ever reads the last frame handed to
/// <see cref="Present"/>. A Composition presenter can replace this class without touching the scheduler, the cache or the page surface.
/// </summary>
internal sealed class Win2DTilePresenter : ITilePresenter, IDisposable
{
    private static readonly ConcurrentDictionary<int, ConcurrentBag<byte[]>> Buffers = new();

    private readonly CanvasVirtualControl _control;
    private readonly CanvasDevice _device;
    private FrameComposition? _frame;
    private double _rasterScale = 1.0;
    private Color _background = Color.FromArgb(255, 0xEE, 0xEE, 0xEE);
    private Color _paper = Color.FromArgb(255, 255, 255, 255);
    private Color _paperBorder = Color.FromArgb(40, 0, 0, 0);

    public Win2DTilePresenter()
    {
        _device = CanvasDevice.GetSharedDevice();
        _control = new CanvasVirtualControl
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClearColor = _background,
            IsHitTestVisible = false,
            // Not part of the accessibility tree: PageSurface owns the automation peer (S7).
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(_control, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        _control.RegionsInvalidated += OnRegionsInvalidated;
    }

    public FrameworkElement Element => _control;

    /// <summary>Device pixels per device-independent pixel of the window the control is in. UI thread.</summary>
    public double RasterScale
    {
        get => _rasterScale;
        set => _rasterScale = value > 0 ? value : 1.0;
    }

    /// <summary>The colours behind and on the pages, for the current theme (and high contrast). UI thread.</summary>
    public void SetColors(Color background, Color paper, Color border)
    {
        _background = background;
        _paper = paper;
        _paperBorder = border;
        _control.ClearColor = background;
        _control.Invalidate();
    }

    public ITileSurface CreateSurface(IPixelBuffer pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        var width = pixels.Width;
        var height = pixels.Height;
        var rowBytes = width * 4;
        var size = rowBytes * height;
        // Pool only the common sizes (full tiles); the placeholders come in many sizes and are small.
        var pooled = size >= (256 << 10) && (Buffers.ContainsKey(size) || Buffers.Count < 32);
        ConcurrentBag<byte[]>? bag = pooled ? Buffers.GetOrAdd(size, _ => []) : null;
        if (bag is null || !bag.TryTake(out var bytes))
        {
            bytes = new byte[size];
        }

        try
        {
            var source = pixels.Pixels;
            var stride = pixels.Stride;
            if (stride == rowBytes)
            {
                source[..bytes.Length].CopyTo(bytes);
            }
            else
            {
                for (var y = 0; y < height; y++)
                {
                    source.Slice(y * stride, rowBytes).CopyTo(bytes.AsSpan(y * rowBytes, rowBytes));
                }
            }

            var bitmap = CanvasBitmap.CreateFromBytes(_device, bytes, width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96f, CanvasAlphaMode.Ignore);
            return new Win2DTileSurface(bitmap);
        }
        finally
        {
            if (bag is { Count: < 4 })
            {
                bag.Add(bytes);
            }
        }
    }

    public void Present(FrameComposition frame)
    {
        _frame = frame;
        _control.Invalidate();
    }

    public void Dispose()
    {
        _control.RegionsInvalidated -= OnRegionsInvalidated;
        _control.RemoveFromVisualTree();
    }

    private void OnRegionsInvalidated(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        var traceStart = AppTrace.FrameBegin();
        var frame = _frame;
        var scale = (float)_rasterScale;
        foreach (var region in args.InvalidatedRegions)
        {
            using var session = sender.CreateDrawingSession(region);
            session.Clear(_background);
            if (frame is null)
            {
                continue;
            }

            if (frame.GestureScale != 1.0)
            {
                session.Transform = Matrix3x2.CreateScale((float)frame.GestureScale, (float)frame.GestureScale, new Vector2((float)(frame.GestureOrigin.X / scale), (float)(frame.GestureOrigin.Y / scale)));
            }

            foreach (var page in frame.Pages)
            {
                var paper = ToDips(page.PageRect, scale);
                session.FillRectangle(paper, _paper);
                foreach (var layer in page.Layers)
                {
                    if (layer.Surface is Win2DTileSurface surface)
                    {
                        try
                        {
                            session.DrawImage(surface.Bitmap, ToDips(layer.Destination, scale), surface.Bitmap.Bounds, 1f, CanvasImageInterpolation.Linear);
                        }
                        catch (ObjectDisposedException)
                        {
                            // A surface evicted between composing and drawing; the next frame does not contain it.
                        }
                    }
                }

                session.DrawRectangle(paper, _paperBorder, 1f / Math.Max(1f, scale));
            }
        }

        AppTrace.FrameEnd(traceStart, frame);
    }

    private static Rect ToDips(MikePDF.Core.Geometry.RectD r, float scale) => new(r.X / scale, r.Y / scale, r.Width / scale, r.Height / scale);
}
