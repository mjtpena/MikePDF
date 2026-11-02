using System.Runtime.InteropServices;

namespace MikePDF.ShellHost;

/// <summary>
/// The process at the other end of a pipe, and the section handles it names (ADR-0018 decision 3). The handle values in a message are valid in the <i>client</i>:
/// ShellHost duplicates them from the process that <c>GetNamedPipeClientProcessId</c> reports, never from a pid in a message, and maps only as many bytes as the
/// message promised, so a section that is smaller, a handle that is not a section, or a handle the client does not own all fail here and nowhere later.
/// </summary>
internal sealed class ClientProcess : IDisposable
{
    private readonly nint _handle;

    private ClientProcess(uint processId, nint handle, string? image, string? family)
    {
        ProcessId = processId;
        _handle = handle;
        Image = image;
        Family = family;
    }

    public uint ProcessId { get; }

    /// <summary>For the log only.</summary>
    public string? Image { get; }

    /// <summary>The client's package family, or null when it has no package identity.</summary>
    public string? Family { get; }

    /// <summary>Opens the client of a connected pipe for handle duplication. Null when the process cannot be opened.</summary>
    public static ClientProcess? OpenFor(nint connectedPipe)
    {
        if (!Native.GetNamedPipeClientProcessId(connectedPipe, out var pid))
        {
            return null;
        }

        var process = Native.OpenProcess(Native.ProcessDupHandle | Native.ProcessQueryLimitedInformation, false, pid);
        if (process == 0)
        {
            return null;
        }

        return new ClientProcess(pid, process, Native.ImagePathOf(process), Native.PackageFamilyOf(process));
    }

    /// <summary>A view of <paramref name="bytes"/> bytes of a section that lives in the client, or null (with <paramref name="failure"/> naming why) if it cannot be mapped.</summary>
    public unsafe SectionView? Map(ulong clientHandle, uint bytes, bool writable, out string failure)
    {
        failure = string.Empty;
        if (clientHandle == 0 || clientHandle > (ulong)nint.MaxValue || bytes == 0)
        {
            failure = "bad handle value";
            return null;
        }

        if (!Native.DuplicateHandle(_handle, (nint)clientHandle, Native.GetCurrentProcess(), out var local, 0, false, Native.DuplicateSameAccess))
        {
            failure = "the client has no such handle (error " + Marshal.GetLastPInvokeError() + ")";
            return null;
        }

        try
        {
            // Mapping exactly the promised size is the size check: a section that is smaller makes MapViewOfFile fail, and a handle that is not a section does too.
            var view = Native.MapViewOfFile(local, writable ? Native.FileMapWrite : Native.FileMapRead, 0, 0, bytes);
            if (view is null)
            {
                failure = "not a section, too small, or without the access needed (error " + Marshal.GetLastPInvokeError() + ")";
                return null;
            }

            return new SectionView(view, bytes);
        }
        finally
        {
            Native.CloseHandle(local);
        }
    }

    public void Dispose() => Native.CloseHandle(_handle);
}

/// <summary>A mapped view of a section; unmapped on dispose.</summary>
internal sealed unsafe class SectionView : IDisposable
{
    private void* _address;

    public SectionView(void* address, uint length)
    {
        _address = address;
        Length = length;
    }

    public uint Length { get; }

    public ReadOnlySpan<byte> Bytes => _address is null ? [] : new ReadOnlySpan<byte>(_address, (int)Length);

    public Span<byte> Writable => _address is null ? [] : new Span<byte>(_address, (int)Length);

    public void Dispose()
    {
        if (_address is not null)
        {
            Native.UnmapViewOfFile(_address);
            _address = null;
        }
    }
}
