using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.UI.Input.Inking;

namespace HelloQuire;

/// <summary>
/// S5.1 compile + runtime probe of the ink-relevant API surface on WinAppSDK 2.5.1 / WinUI 3 / .NET 10.
/// Everything below COMPILES against the pinned SDK (that is itself the evidence for "this API exists").
/// What can run without a pen is run in <see cref="Run"/>; pen-dependent behaviour is NOT measured.
/// </summary>
internal static class InkProbe
{
    // ---- Compile-only: pointer/pen properties exposed to WinUI 3 pointer events (Microsoft.UI.Input) ----
    public static string DescribePointer(UIElement relativeTo, PointerRoutedEventArgs e)
    {
        PointerPoint pp = e.GetCurrentPoint(relativeTo);
        PointerPointProperties p = pp.Properties;
        var sb = new StringBuilder();
        sb.Append($"type={pp.PointerDeviceType} id={pp.PointerId} contact={pp.IsInContact} pos=({pp.Position.X:F1},{pp.Position.Y:F1}) frame={pp.FrameId} ts={pp.Timestamp}");
        sb.Append($" pressure={p.Pressure} xTilt={p.XTilt} yTilt={p.YTilt} twist={p.Twist} orientation={p.Orientation}");
        sb.Append($" barrel={p.IsBarrelButtonPressed} eraser={p.IsEraser} inverted={p.IsInverted} inRange={p.IsInRange} touchConf={p.TouchConfidence} contactRect={p.ContactRect}");
        // Coalesced (intermediate) points between two events: the high-rate samples
        foreach (PointerPoint ip in e.GetIntermediatePoints(relativeTo)) sb.Append($" ip({ip.Position.X:F1},{ip.Position.Y:F1},{ip.Properties.Pressure:F2})");
        return sb.ToString();
    }

    // PointerPredictor: OS pen/touch point prediction from Microsoft.UI.Input (needs an InputPointerSource from a ContentIsland)
    public static IReadOnlyList<PointerPoint> Predict(PointerPredictor predictor, PointerPoint latest) => predictor.GetPredictedPoints(latest);
    public static PointerPredictor CreatePredictor(InputPointerSource src) => PointerPredictor.CreateForInputPointerSource(src);

    // PenDeviceInterop: map a PointerPoint to Windows.Devices.Input.PenDevice (pen GUID, handedness etc.)
    public static Windows.Devices.Input.PenDevice? PenOf(PointerPoint pp) => Microsoft.UI.Input.Interop.PenDeviceInterop.FromPointerPoint(pp);

    // ---- Runtime: headless UWP-style ink model (no CoreWindow needed) + Win2D rendering of InkStrokes ----
    public static string Run(CanvasDevice device)
    {
        var sb = new StringBuilder();
        try
        {
            var da = new InkDrawingAttributes { Color = Windows.UI.Color.FromArgb(255, 220, 20, 20), Size = new(4, 4), PenTip = PenTipShape.Circle, IgnorePressure = false, FitToCurve = true };
            var builder = new InkStrokeBuilder();
            builder.SetDefaultDrawingAttributes(da);
            // pressure-varying synthetic stroke
            var pts = new List<InkPoint>();
            for (int i = 0; i <= 40; i++)
                pts.Add(new InkPoint(new Windows.Foundation.Point(20 + i * 6, 60 + 30 * Math.Sin(i / 6.0)), (float)(0.1 + 0.9 * Math.Sin(i / 40.0 * Math.PI))));
            InkStroke stroke = builder.CreateStrokeFromInkPoints(pts, System.Numerics.Matrix3x2.Identity);
            var container = new InkStrokeContainer();
            container.AddStroke(stroke);
            var segs = stroke.GetRenderingSegments();
            sb.AppendLine($"InkStrokeBuilder.CreateStrokeFromInkPoints ok: inputPoints={pts.Count} renderingSegments={segs.Count} boundingRect={stroke.BoundingRect} width@pressure(first/mid)={stroke.GetInkPoints()[0].Pressure:F2}/{stroke.GetInkPoints()[20].Pressure:F2}");
            sb.AppendLine($"InkStrokeContainer strokes={container.GetStrokes().Count} canPaste={container.CanPasteFromClipboard()}");

            using var rt = new CanvasRenderTarget(device, 300, 140, 96);
            using (var ds = rt.CreateDrawingSession())
            {
                ds.Clear(Microsoft.UI.Colors.White);
                // Win2D-WinUI 1.4.0 has NO CanvasDrawingSession.DrawInk (UWP Win2D had it): render the Bezier rendering segments ourselves.
                var cur = segs.Count > 0 ? stroke.GetInkPoints()[0].Position : default;
                foreach (var seg in segs)
                {
                    using var pb = new Microsoft.Graphics.Canvas.Geometry.CanvasPathBuilder(ds);
                    pb.BeginFigure((float)cur.X, (float)cur.Y);
                    pb.AddCubicBezier(new System.Numerics.Vector2((float)seg.BezierControlPoint1.X, (float)seg.BezierControlPoint1.Y), new System.Numerics.Vector2((float)seg.BezierControlPoint2.X, (float)seg.BezierControlPoint2.Y), new System.Numerics.Vector2((float)seg.Position.X, (float)seg.Position.Y));
                    pb.EndFigure(Microsoft.Graphics.Canvas.Geometry.CanvasFigureLoop.Open);
                    using var g = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePath(pb);
                    ds.DrawGeometry(g, da.Color, (float)(da.Size.Width * 2 * seg.Pressure));
                    cur = seg.Position;
                }
            }
            var colors = rt.GetPixelColors();
            int inked = colors.Count(c => c.R > 150 && c.G < 100 && c.B < 100);
            sb.AppendLine($"Manual Bezier rendering of InkStroke segments via Win2D ok: red pixels={inked} of {colors.Length}");
        }
        catch (Exception ex) { sb.AppendLine("Inking runtime probe FAILED: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + ": " + ex.Message); }

        // Unsupported-in-WinUI3 path: InkPresenter needs a UWP InkCanvas / CoreWindow. Record what constructing it does.
        try
        {
            // InkPresenter has no public constructor; it is only obtained from Windows.UI.Xaml.Controls.InkCanvas (UWP XAML),
            // which is not present in WinUI 3 (see winmd-ink.txt). Nothing to call: documented, not tested.
            sb.AppendLine("InkPresenter / InkCanvas / InkToolbar: not constructible from WinUI 3 (types live in Windows.UI.Xaml.Controls, UWP XAML only).");
        }
        catch { }

        sb.Append(DelegatedInk.Probe());
        return sb.ToString();
    }
}

/// <summary>Raw DirectComposition delegated-ink probe through dcomp.dll P/Invoke (no WinUI/WinAppSDK wrapper exists).</summary>
internal static class DelegatedInk
{
    [StructLayout(LayoutKind.Sequential)] struct DCompositionInkTrailPoint { public float x, y, radius; }
    [StructLayout(LayoutKind.Sequential)] struct D2D1_COLOR_F { public float r, g, b, a; }

    [ComImport, Guid("C2448E9B-547D-4057-8CF5-8144EDE1C2DA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDCompositionDelegatedInkTrail
    {
        [PreserveSig] int AddTrailPoints(ref DCompositionInkTrailPoint pts, uint n, out uint generationId);
        [PreserveSig] int AddTrailPointsWithPrediction(ref DCompositionInkTrailPoint pts, uint n, ref DCompositionInkTrailPoint pred, uint np, out uint generationId);
        [PreserveSig] int RemoveTrailPoints(uint generationId);
        [PreserveSig] int StartNewTrail(ref D2D1_COLOR_F color);
    }

    [ComImport, Guid("DF0C7CEC-CDEB-4D4A-B91C-721BF22F4E6C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDCompositionInkTrailDevice
    {
        [PreserveSig] int CreateDelegatedInkTrail(out IDCompositionDelegatedInkTrail trail);
        [PreserveSig] int CreateDelegatedInkTrailForSwapChain(IntPtr swapChain, out IDCompositionDelegatedInkTrail trail);
    }

    [DllImport("dcomp.dll")] static extern int DCompositionCreateDevice3(IntPtr renderingDevice, ref Guid iid, out IntPtr device);

    public static string Probe()
    {
        var sb = new StringBuilder();
        try
        {
            var iidDevice = new Guid("C37EA93A-E7AA-450D-B16F-9746CB0407F3"); // IDCompositionDevice
            int hr = DCompositionCreateDevice3(IntPtr.Zero, ref iidDevice, out var dev);
            sb.AppendLine($"DCompositionCreateDevice3(null, IDCompositionDevice) hr=0x{hr:X8}");
            if (hr != 0) return sb.ToString();
            try
            {
                object obj = Marshal.GetObjectForIUnknown(dev);
                if (obj is IDCompositionInkTrailDevice inkDev)
                {
                    sb.AppendLine("QueryInterface(IDCompositionInkTrailDevice): OK");
                    int hr2 = inkDev.CreateDelegatedInkTrail(out var trail);
                    sb.AppendLine($"CreateDelegatedInkTrail hr=0x{hr2:X8}");
                    if (hr2 == 0)
                    {
                        var pt = new DCompositionInkTrailPoint { x = 10, y = 10, radius = 2 };
                        int hr3 = trail.AddTrailPoints(ref pt, 1, out var gen);
                        sb.AppendLine($"IDCompositionDelegatedInkTrail.AddTrailPoints hr=0x{hr3:X8} generationId={gen}");
                    }
                }
                else sb.AppendLine("QueryInterface(IDCompositionInkTrailDevice): NOT supported by the device object");
            }
            finally { Marshal.Release(dev); }
        }
        catch (Exception ex) { sb.AppendLine("DelegatedInk probe FAILED: " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + ": " + ex.Message); }
        sb.AppendLine("NOTE: a trail only shows ink when attached as content (IDCompositionVisual::SetContent) in a DirectComposition tree whose target is the app HWND; WinUI 3's Microsoft.UI.Composition does not expose an IDCompositionVisual. Not tested end to end (needs pen hardware).");
        return sb.ToString();
    }
}
