using System.Buffers;
using System.IO.MemoryMappedFiles;
using Microsoft.Win32.SafeHandles;

namespace Quire.WriterHost;

/// <summary>
/// A read-only view of a file the broker opened and duplicated into this process (ADR-0005 rule 6, ADR-0008). The host never sees a path.
/// The view is exposed as <see cref="Data"/> without copying, so a file the parser refuses costs no allocation proportional to its size.
/// </summary>
internal sealed unsafe class MappedFile : MemoryManager<byte>
{
    /// <summary>A file larger than this cannot be addressed as one <see cref="Memory{T}"/>.</summary>
    public const long MaxLength = int.MaxValue;

    private readonly FileStream _stream;
    private readonly MemoryMappedFile? _mapping;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly byte* _base;
    private readonly int _length;

    private MappedFile(FileStream stream, MemoryMappedFile? mapping, MemoryMappedViewAccessor? view, byte* start, int length)
    {
        _stream = stream;
        _mapping = mapping;
        _view = view;
        _base = start;
        _length = length;
    }

    public ReadOnlyMemory<byte> Data => Memory;

    /// <summary>
    /// Takes ownership of <paramref name="handle"/>. Returns null (and the reason) when the value is not a readable disk file or does not fit in
    /// memory; the handle is closed either way.
    /// </summary>
    public static MappedFile? Open(SafeFileHandle handle, out MapFailure failure)
    {
        failure = MapFailure.None;
        FileStream stream;
        try
        {
            stream = new FileStream(handle, FileAccess.Read, bufferSize: 1, isAsync: false);
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            handle.Dispose();
            failure = MapFailure.NotAFile;
            return null;
        }

        try
        {
            var length = stream.Length;
            if (length > MaxLength)
            {
                stream.Dispose();
                failure = MapFailure.TooLarge;
                return null;
            }

            if (length == 0)
            {
                return new MappedFile(stream, null, null, null, 0);
            }

            var mapping = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
            var view = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            return new MappedFile(stream, mapping, view, pointer + view.PointerOffset, (int)length);
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            stream.Dispose();
            failure = MapFailure.NotAFile;
            return null;
        }
    }

    public override Span<byte> GetSpan() => new(_base, _length);

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elementIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(elementIndex, _length);
        return new MemoryHandle(_base + elementIndex);
    }

    public override void Unpin()
    {
        // The view stays mapped until Dispose; nothing to release per pin.
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        _view?.SafeMemoryMappedViewHandle.ReleasePointer();
        _view?.Dispose();
        _mapping?.Dispose();
        _stream.Dispose();
    }
}

internal enum MapFailure
{
    None,
    NotAFile,
    TooLarge,
}
