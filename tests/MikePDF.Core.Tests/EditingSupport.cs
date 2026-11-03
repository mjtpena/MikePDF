using System.Text;
using MikePDF.Core.Editing;
using MikePDF.Core.Editing.Journal;

namespace MikePDF.Core.Tests;

/// <summary>A scratch directory for one test, removed afterwards.</summary>
internal sealed class ScratchDirectory : IDisposable
{
    public ScratchDirectory() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mikepdf-edit-" + Guid.NewGuid().ToString("N")[..10]);

    public string Path { get; }

    public string Combine(string name) => System.IO.Path.Combine(Path, name);

    public string Make(string name, byte[] content)
    {
        Directory.CreateDirectory(Path);
        var path = Combine(name);
        File.WriteAllBytes(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A scanner can hold a file for a moment; the next run's temp folder cleanup takes it.
        }
    }
}

internal static class JournalSamples
{
    public static JournalHeader Header(string path = @"C:\x\a.pdf") => new(path, null, 1000, "2026-11-02T10:30:00.000Z", new string('a', 64), "1.0.0.0", []);

    /// <summary>A record built by hand with the right suffix, for the cases the writer can never produce.</summary>
    public static byte[] RawRecord(string bodyWithoutClosingBrace)
    {
        var body = Encoding.UTF8.GetBytes(bodyWithoutClosingBrace);
        return [.. body, .. Encoding.ASCII.GetBytes($",\"crc\":\"{Crc32C.Compute(body):x8}\"}}\n")];
    }
}

/// <summary>A journal file layer that records what happens to it and can be made to fail, so a test can see the order write, flush, acknowledge.</summary>
internal sealed class RecordingJournalFiles : IJournalFileSystem
{
    public List<string> Log { get; } = [];

    public MemoryStream Content { get; } = new();

    public bool FailFlush { get; set; }

    public bool FailAppend { get; set; }

    public IJournalFile Create(string path)
    {
        Log.Add("create");
        return new File(this);
    }

    public IJournalFile OpenAppend(string path)
    {
        Log.Add("open");
        return new File(this);
    }

    public void Truncate(string path, long length) => Log.Add("truncate " + length);

    private sealed class File(RecordingJournalFiles owner) : IJournalFile
    {
        public long Length => owner.Content.Length;

        public void Append(ReadOnlySpan<byte> bytes)
        {
            if (owner.FailAppend)
            {
                throw new IOException("injected append failure");
            }

            owner.Log.Add("append");
            owner.Content.Write(bytes);
        }

        public void Flush()
        {
            if (owner.FailFlush)
            {
                throw new IOException("injected flush failure");
            }

            owner.Log.Add("flush");
        }

        public void Dispose()
        {
        }
    }
}
