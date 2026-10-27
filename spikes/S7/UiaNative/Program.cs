// S7 extra: a NATIVE UIA client (IUIAutomation via CsWin32) to check what the managed System.Windows.Automation client cannot
// express: TextPattern2 availability and GetCaretRange.
using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Accessibility;

string exe = args[0];
string fixture = args[1];
var proc = Process.Start(new ProcessStartInfo(exe, $"--fixture \"{fixture}\" --zoom 1") { UseShellExecute = false })!;
try
{
    unsafe { PInvoke.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED); }
    PInvoke.CoCreateInstance<IUIAutomation>(typeof(CUIAutomation8).GUID, null!, CLSCTX.CLSCTX_INPROC_SERVER, out var uia).ThrowOnFailure();

    for (int i = 0; i < 100 && proc.MainWindowHandle == IntPtr.Zero; i++) { Thread.Sleep(100); proc.Refresh(); }
    Thread.Sleep(2000);
    var window = uia.ElementFromHandle(new HWND(proc.MainWindowHandle));
    var walker = uia.ControlViewWalker;
    IUIAutomationElement? page = Find(walker, window, "PageSurface", 0);
    if (page == null) { Console.WriteLine("RESULT page element not found"); return 2; }

    Console.WriteLine($"page name='{page.CurrentName}'");
    bool t1 = (bool)page.GetCurrentPropertyValue(UIA_PROPERTY_ID.UIA_IsTextPatternAvailablePropertyId);
    bool t2 = (bool)page.GetCurrentPropertyValue(UIA_PROPERTY_ID.UIA_IsTextPattern2AvailablePropertyId);
    Console.WriteLine($"RESULT IsTextPatternAvailable={t1}  IsTextPattern2Available={t2}");

    var tp2 = (IUIAutomationTextPattern2)page.GetCurrentPattern(UIA_PATTERN_ID.UIA_TextPattern2Id);
    unsafe
    {
        BOOL active = default;
        var caret = tp2.GetCaretRange(&active);
        Console.WriteLine($"RESULT TextPattern2.GetCaretRange isActive={active.Value != 0} caretText='{caret.GetText(-1)}'");
        var doc = tp2.DocumentRange;
        Console.WriteLine($"RESULT TextPattern2.DocumentRange.GetText(40)='{doc.GetText(40).ToString().Replace("\n", "\n")}'");
        var sa = doc.GetBoundingRectangles();
        if (sa != null)
        {
            PInvoke.SafeArrayGetLBound(sa, 1, out int lb); PInvoke.SafeArrayGetUBound(sa, 1, out int ub);
            void* p; PInvoke.SafeArrayAccessData(sa, out p);
            var d = (double*)p;
            Console.WriteLine($"RESULT native GetBoundingRectangles: {(ub - lb + 1) / 4} rect(s), first = ({d[0]:F1},{d[1]:F1},{d[2]:F1},{d[3]:F1})");
            PInvoke.SafeArrayUnaccessData(sa);
        }
    }
    return 0;
}
finally { try { if (!proc.HasExited) proc.Kill(true); } catch { } }

static IUIAutomationElement? Find(IUIAutomationTreeWalker w, IUIAutomationElement e, string id, int depth)
{
    if (e.CurrentAutomationId.ToString() == id) return e;
    if (depth > 12) return null;
    var c = w.GetFirstChildElement(e);
    while (c != null)
    {
        var f = Find(w, c, id, depth + 1);
        if (f != null) return f;
        c = w.GetNextSiblingElement(c);
    }
    return null;
}
