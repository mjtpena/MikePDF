using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MikePDF.Broker.Native;

internal static class Win32
{
    public static readonly Windows.Win32.Foundation.HANDLE InvalidHandle = new(-1);


    /// <summary>HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS), what CreateAppContainerProfile returns for an existing profile.</summary>
    public const int HResultAlreadyExists = unchecked((int)0x800700B7);

    public static Win32Exception LastError(string what) => Error(Marshal.GetLastPInvokeError(), what);

    /// <summary>The message names the call, the system's text for the code and the code itself: a failure seen once on a build machine has to say which error it was.</summary>
    public static Win32Exception Error(int code, string what) => new(code, $"{what} failed: {new Win32Exception(code).Message} (error {code})");

    public static void ThrowIfFalse(bool success, string what)
    {
        if (!success)
        {
            throw LastError(what);
        }
    }

    public static void ThrowIfFailed(int hresult, string what)
    {
        if (hresult < 0)
        {
            throw new Win32Exception(hresult & 0xFFFF, what + " failed (HRESULT 0x" + hresult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ")");
        }
    }
}
