using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Quire.Broker.Native;

/// <summary>A Win32 kernel handle (process, job, section, pipe, port) closed with CloseHandle.</summary>
internal sealed class OwnedHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public OwnedHandle()
        : base(ownsHandle: true)
    {
    }

    public OwnedHandle(nint handle)
        : base(ownsHandle: true) => SetHandle(handle);

    public HANDLE Value => (HANDLE)handle;

    protected override bool ReleaseHandle() => PInvoke.CloseHandle((HANDLE)handle);
}
