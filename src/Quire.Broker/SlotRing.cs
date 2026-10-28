using System.Collections.Concurrent;
using Quire.Broker.Native;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;

namespace Quire.Broker;

/// <summary>
/// The shared-memory ring of fixed-size slots for bulk data (ADR-0005 rule 4): one pagefile-backed section, mapped
/// <b>read-only</b> here and writable only in the engine, which gets its own duplicated handle. Control messages carry
/// slot indices; the engine writes pixels, this process reads them.
/// </summary>
public sealed class SlotRing : IDisposable
{
    public const int MaxSlots = 1024; // the schema's slotIndex limit is 1023
    public const int MinSlotSize = 4096;
    public const int MaxSlotSize = 64 << 20;
    public const long MaxSectionSize = 1L << 30;

    private const uint SectionMapRead = 0x4;
    private const uint SectionMapWrite = 0x2;

    private readonly OwnedHandle _section;
    private readonly nint _view;
    private readonly ConcurrentQueue<int> _free = new();
    private readonly SemaphoreSlim _available;
    private int _references = 1; // the ring itself; each lease adds one
    private int _disposed;

    public unsafe SlotRing(int slotCount, int slotSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slotCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slotCount, MaxSlots);
        ArgumentOutOfRangeException.ThrowIfLessThan(slotSize, MinSlotSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slotSize, MaxSlotSize);
        if (slotSize % MinSlotSize != 0)
        {
            throw new ArgumentException("The slot size must be a multiple of 4096.", nameof(slotSize));
        }

        var total = (long)slotCount * slotSize;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(total, MaxSectionSize);
        SlotCount = slotCount;
        SlotSize = slotSize;

        var handle = PInvoke.CreateFileMapping(Win32.InvalidHandle, null, PAGE_PROTECTION_FLAGS.PAGE_READWRITE, (uint)(total >> 32), (uint)total, default(PCWSTR));
        if (handle.IsNull)
        {
            throw Win32.LastError("CreateFileMapping");
        }

        _section = new OwnedHandle(handle);
        var view = PInvoke.MapViewOfFile(_section.Value, FILE_MAP.FILE_MAP_READ, 0, 0, 0);
        if (view.Value is null)
        {
            _section.Dispose();
            throw Win32.LastError("MapViewOfFile");
        }

        _view = (nint)view.Value;
        for (var i = 0; i < slotCount; i++)
        {
            _free.Enqueue(i);
        }

        _available = new SemaphoreSlim(slotCount, slotCount);
    }

    /// <summary>The section handle, for tests that check it is not inheritable.</summary>
    internal nint SectionHandle => _section.DangerousGetHandle();

    public int SlotCount { get; }

    public int SlotSize { get; }

    /// <summary>
    /// Duplicates the section into <paramref name="process"/> with write access and returns the handle value in that process's table. Nothing is
    /// inherited (R1): the engine learns this value from AttachSlotRing and maps it then. The duplicate carries only SECTION_MAP_READ and
    /// SECTION_MAP_WRITE: it cannot extend, query or re-secure the section.
    /// </summary>
    internal ulong DuplicateInto(SandboxedProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return process.DuplicateInto(_section.Value, SectionMapRead | SectionMapWrite, 0);
    }

    /// <summary>A writable duplicate in this process, for tests that play the engine's side. Not inheritable.</summary>
    internal unsafe OwnedHandle DuplicateWritableForTests()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        HANDLE duplicate;
        Win32.ThrowIfFalse(
            PInvoke.DuplicateHandle(PInvoke.GetCurrentProcess(), _section.Value, PInvoke.GetCurrentProcess(), &duplicate, SectionMapRead | SectionMapWrite, false, 0),
            "DuplicateHandle(section)");
        return new OwnedHandle(duplicate);
    }

    /// <summary>Waits for a free slot.</summary>
    public async ValueTask<SlotLease> RentAsync(CancellationToken cancellationToken = default)
    {
        await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
        return TakeFree();
    }

    public bool TryRent(out SlotLease? lease)
    {
        if (!_available.Wait(0))
        {
            lease = null;
            return false;
        }

        lease = TakeFree();
        return true;
    }

    /// <summary>A read-only view of one slot. Throws if the index is outside the ring.</summary>
    internal unsafe ReadOnlySpan<byte> Slot(int index)
    {
        if ((uint)index >= (uint)SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return new ReadOnlySpan<byte>((byte*)_view + ((long)index * SlotSize), SlotSize);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Release();
        }
    }

    internal void AddReference()
    {
        ObjectDisposedException.ThrowIf(Interlocked.Increment(ref _references) <= 1, this);
    }

    internal unsafe void Release()
    {
        // The view is unmapped when the ring and every outstanding lease have let go, so a Span over it is never left dangling.
        if (Interlocked.Decrement(ref _references) == 0)
        {
            PInvoke.UnmapViewOfFile(new MEMORY_MAPPED_VIEW_ADDRESS((void*)_view));
            _section.Dispose();
        }
    }

    internal void Return(int index)
    {
        _free.Enqueue(index);
        _available.Release();
    }

    private SlotLease TakeFree()
    {
        if (!_free.TryDequeue(out var index))
        {
            throw new InvalidOperationException("The slot semaphore and the free list disagree.");
        }

        AddReference();
        return new SlotLease(this, index);
    }
}

/// <summary>One rented slot. Disposing returns it to the ring.</summary>
public sealed class SlotLease : IDisposable
{
    private readonly SlotRing _ring;
    private int _released;

    internal SlotLease(SlotRing ring, int index)
    {
        _ring = ring;
        Index = index;
    }

    public int Index { get; }

    /// <summary>The slot's bytes. The engine owns the writing side: contents are untrusted and can change until the lease is disposed.</summary>
    public ReadOnlySpan<byte> Bytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
            return _ring.Slot(Index);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _ring.Return(Index);
            _ring.Release();
        }
    }
}
