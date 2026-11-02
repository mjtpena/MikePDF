using System.Runtime.InteropServices;

namespace Quire.UI.Tests.Pages;

/// <summary>Reads (and empties) the Windows clipboard from the test process, to see what a copy in Quire put there. Text only.</summary>
internal static partial class NativeClipboard
{
    private const uint UnicodeText = 13;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(IntPtr owner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr GetClipboardData(uint format);

    public static string? GetText()
    {
        // The clipboard may be held for a moment by whoever just wrote to it.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    var data = GetClipboardData(UnicodeText);
                    return data == IntPtr.Zero ? null : Marshal.PtrToStringUni(data);
                }
                finally
                {
                    CloseClipboard();
                }
            }

            Thread.Sleep(50);
        }

        return null;
    }

    public static void Clear()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    return;
                }
                finally
                {
                    CloseClipboard();
                }
            }

            Thread.Sleep(50);
        }
    }
}
