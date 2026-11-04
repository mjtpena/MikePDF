using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MikePDF.UI.Tests.Pages;

/// <summary>Which window has the keyboard when a wait expires: written next to a failure's screenshot, because a key that goes to another window (a toast, a flyout, a console) looks like an app that ignored it.</summary>
internal static partial class ForegroundWindow
{
    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowText(nint window, [Out] char[] text, int count);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassName(nint window, [Out] char[] text, int count);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    public static string Describe()
    {
        var window = GetForegroundWindow();
        if (window == 0)
        {
            return "no foreground window";
        }

        var title = new char[256];
        var cls = new char[256];
        var titleLength = GetWindowText(window, title, title.Length);
        var classLength = GetClassName(window, cls, cls.Length);
        GetWindowThreadProcessId(window, out var pid);
        string process;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            process = p.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            process = "?";
        }

        return new StringBuilder().Append("foreground: process ").Append(process).Append(" (").Append(pid).Append("), class '").Append(cls, 0, classLength).Append("', title '").Append(title, 0, titleLength).Append('\'').ToString();
    }
}
