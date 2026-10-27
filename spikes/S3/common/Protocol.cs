using System.Buffers.Binary;
using System.Text;

namespace Quire.S3;

/// <summary>Message types. The response to message X is sent as X | ResponseBit with the same reqId.</summary>
public enum Msg : ushort
{
    Hello = 1,
    Ping = 2,            // answered on the I/O thread (heartbeat path)
    PingEngine = 3,      // round-trips through the engine thread's work queue
    OpenDocument = 4,
    CloseDocument = 5,
    RenderTile = 6,
    Shutdown = 7,
    RunNegative = 8,
    AllocUntilLimit = 9,
    GdiProbe = 10,
    Crash = 11,
    SetFontProvider = 12,
    GetCharBoxes = 13,
    ApplyRuntimeMitigations = 14,
    SetFonts = 15,       // S3b: broker sends font bytes: i32 count, then {str key, i32 style(bit0 bold, bit1 italic), i32 len, str faceName, bytes}
    LoadDll = 16,        // S3b: engine LoadLibrary(path) and reports the Win32 error
}

public static class Proto
{
    public const uint Version = 1;
    public const ushort ResponseBit = 0x8000;
    public const int HeaderSize = 12;           // u32 len | u16 type | u16 flags | u32 reqId   (len counts bytes after itself)
    public const int MaxFrame = 8 << 20;        // validated by the receiver: an untrusted peer must not be able to make us allocate more
}

public sealed class ProtocolException(string m) : Exception(m);

/// <summary>Growable little-endian payload writer.</summary>
public sealed class PayloadWriter
{
    byte[] _b = new byte[256];
    int _n;
    public int Length => _n;
    public ReadOnlySpan<byte> Span => _b.AsSpan(0, _n);
    public void Reset() => _n = 0;
    void Ensure(int add) { if (_n + add > _b.Length) Array.Resize(ref _b, Math.Max(_b.Length * 2, _n + add)); }
    public PayloadWriter U8(byte v) { Ensure(1); _b[_n++] = v; return this; }
    public PayloadWriter U16(ushort v) { Ensure(2); BinaryPrimitives.WriteUInt16LittleEndian(_b.AsSpan(_n), v); _n += 2; return this; }
    public PayloadWriter I32(int v) { Ensure(4); BinaryPrimitives.WriteInt32LittleEndian(_b.AsSpan(_n), v); _n += 4; return this; }
    public PayloadWriter U32(uint v) { Ensure(4); BinaryPrimitives.WriteUInt32LittleEndian(_b.AsSpan(_n), v); _n += 4; return this; }
    public PayloadWriter I64(long v) { Ensure(8); BinaryPrimitives.WriteInt64LittleEndian(_b.AsSpan(_n), v); _n += 8; return this; }
    public PayloadWriter U64(ulong v) { Ensure(8); BinaryPrimitives.WriteUInt64LittleEndian(_b.AsSpan(_n), v); _n += 8; return this; }
    public PayloadWriter F32(float v) { Ensure(4); BinaryPrimitives.WriteSingleLittleEndian(_b.AsSpan(_n), v); _n += 4; return this; }
    public PayloadWriter Raw(ReadOnlySpan<byte> v) { Ensure(v.Length); v.CopyTo(_b.AsSpan(_n)); _n += v.Length; return this; }
    public PayloadWriter Str(string? s)
    {
        s ??= "";
        int max = Encoding.UTF8.GetMaxByteCount(s.Length);
        Ensure(4 + max);
        int w = Encoding.UTF8.GetBytes(s, _b.AsSpan(_n + 4));
        BinaryPrimitives.WriteInt32LittleEndian(_b.AsSpan(_n), w);
        _n += 4 + w;
        return this;
    }
}

/// <summary>Bounds-checked payload reader. Every read validates against the buffer: the receiver treats the peer as untrusted.</summary>
public sealed class PayloadReader(byte[] buf)
{
    int _p;
    public int Remaining => buf.Length - _p;
    void Need(int n) { if (n < 0 || _p + n > buf.Length) throw new ProtocolException($"short payload (need {n}, have {buf.Length - _p})"); }
    public byte U8() { Need(1); return buf[_p++]; }
    public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(_p)); _p += 2; return v; }
    public int I32() { Need(4); var v = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(_p)); _p += 4; return v; }
    public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(_p)); _p += 4; return v; }
    public long I64() { Need(8); var v = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(_p)); _p += 8; return v; }
    public ulong U64() { Need(8); var v = BinaryPrimitives.ReadUInt64LittleEndian(buf.AsSpan(_p)); _p += 8; return v; }
    public float F32() { Need(4); var v = BinaryPrimitives.ReadSingleLittleEndian(buf.AsSpan(_p)); _p += 4; return v; }
    public string Str(int maxLen = 64 * 1024)
    {
        int n = I32();
        if (n < 0 || n > maxLen) throw new ProtocolException($"string length {n} out of range");
        Need(n);
        var s = Encoding.UTF8.GetString(buf, _p, n);
        _p += n;
        return s;
    }
}

public static class FrameIO
{
    /// <summary>Write one frame with a single Write call (header+payload assembled in one buffer).</summary>
    public static void Write(Stream s, ushort type, uint reqId, ReadOnlySpan<byte> payload)
    {
        int total = Proto.HeaderSize + payload.Length;
        byte[] buf = System.Buffers.ArrayPool<byte>.Shared.Rent(total);
        try
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)(total - 4));
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), type);
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(6), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), reqId);
            payload.CopyTo(buf.AsSpan(Proto.HeaderSize));
            s.Write(buf, 0, total);
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
    }

    static readonly byte[] Empty = [];

    /// <summary>Read one frame. Returns false on clean EOF at a frame boundary.</summary>
    public static bool Read(Stream s, Span<byte> hdr, out ushort type, out uint reqId, out byte[] payload)
    {
        type = 0; reqId = 0; payload = Empty;
        int got = 0;
        while (got < Proto.HeaderSize)
        {
            int r = s.Read(hdr.Slice(got, Proto.HeaderSize - got));
            if (r == 0) { if (got == 0) return false; throw new EndOfStreamException("EOF inside frame header"); }
            got += r;
        }
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(hdr);
        type = BinaryPrimitives.ReadUInt16LittleEndian(hdr.Slice(4));
        reqId = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(8));
        if (len < Proto.HeaderSize - 4 || len > Proto.MaxFrame) throw new ProtocolException($"frame length {len} out of range");
        int plen = (int)len - (Proto.HeaderSize - 4);
        if (plen == 0) return true;
        payload = new byte[plen];
        s.ReadExactly(payload);
        return true;
    }
}

/// <summary>Layout of the shared-memory section. Header page is control data; slots are fixed-size tile buffers.</summary>
public static class Shm
{
    public const int HeaderSize = 64 * 1024;
    public const int SlotSize = 4 * 1024 * 1024;   // up to 1024x1024 BGRA
    public const int SlotCount = 8;
    public const long TotalSize = HeaderSize + (long)SlotSize * SlotCount;
    public const int OffMagic = 0;                 // u32
    public const int OffCommittedMB = 64;          // u64, written by the engine in AllocUntilLimit (progress that survives a kill)
    public const int OffEngineStage = 72;          // u64, last stage marker written by the engine
    public static long SlotOffset(int slot) => HeaderSize + (long)slot * SlotSize;
}

public static class Clock
{
    public static long Now() => System.Diagnostics.Stopwatch.GetTimestamp();   // QueryPerformanceCounter, system-wide, comparable across processes
    public static readonly double TicksPerMs = System.Diagnostics.Stopwatch.Frequency / 1000.0;
    public static double Ms(long ticks) => ticks / TicksPerMs;
    public static double Us(long ticks) => ticks * 1000.0 / TicksPerMs;
}

public sealed record NegResult(string Name, string Expect, string Outcome, int Win32, string Detail)
{
    public bool Ok => Expect == "deny" ? Outcome == "DENIED" : Expect == "allow" ? Outcome == "ALLOWED" : true;
}

public sealed record NegRequest(
    string Documents, string Temp, string RepoDir, string UncPath, int TcpLoopbackPort, string PublicIp, int PublicPort,
    int LauncherPid, string TempDll, string LocalRogueDll, string EngineDir, string FontsDir);
