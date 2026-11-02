#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using MikePDF.Broker.Saving;
using MikePDF.Core.Editing;
using MikePDF.PdfWriter;
using MikePDF.PdfWriter.Editing;
using MikePDF.Protocol.Edits;

namespace MikePDF.Integration.Tests.Saving;

/// <summary>A kill, as far as a test can stand in for one: nothing in the save catches it, so nothing cleans up.</summary>
internal sealed class SimulatedCrash(string where) : Exception("simulated crash at " + where);

internal sealed class CrashAt(SaveStep step) : ISaveFaults
{
    public List<SaveStep> Reached { get; } = [];

    public void Reach(SaveStep point)
    {
        Reached.Add(point);
        if (point == step)
        {
            throw new SimulatedCrash(point.ToString());
        }
    }
}

internal sealed class RecordingFaults : ISaveFaults
{
    public List<SaveStep> Reached { get; } = [];

    public Action<SaveStep>? Observe { get; set; }

    public void Reach(SaveStep point)
    {
        Reached.Add(point);
        Observe?.Invoke(point);
    }
}

internal sealed class RecordingJournal : ISaveJournal
{
    public List<string> Events { get; } = [];

    public ISaveJournal? Inner { get; init; }

    public Action<string>? Observe { get; set; }

    public void SaveIntent(string temp, string? backup, string target) => Record("intent", () => Inner?.SaveIntent(temp, backup, target));

    public void SaveBegin(string outputSha256, long outputLength) => Record("begin", () => Inner?.SaveBegin(outputSha256, outputLength));

    public void SaveCommitted() => Record("committed", () => Inner?.SaveCommitted());

    public void Clear() => Record("clear", () => Inner?.Clear());

    private void Record(string name, Action inner)
    {
        Observe?.Invoke(name);
        Events.Add(name);
        inner();
    }
}

internal sealed class OkEngineCheck : ISaveEngineCheck
{
    public int Calls { get; private set; }

    public Func<string, int, string?>? Verdict { get; set; }

    public Task<string?> CheckAsync(string path, int expectedPages, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Verdict?.Invoke(path, expectedPages));
    }
}

/// <summary>
/// The writer host's operations run in this process on the same library the host uses: the pipeline's decisions, ordering and file handling are
/// what these tests are about; the sandboxed host itself is exercised by <c>RealWriterHostSaveTests</c>.
/// </summary>
internal sealed class InProcessSaveHost : ISaveHost
{
    public List<string> Calls { get; } = [];

    /// <summary>Applied to the output of AppendAsync before it is written: how a test makes the host "misbehave".</summary>
    public Func<byte[], byte[]>? Corrupt { get; set; }

    /// <summary>Applied to the edits before AppendAsync uses them (for example, drop one).</summary>
    public Func<IReadOnlyList<AnnotationEdit>, IReadOnlyList<AnnotationEdit>>? TamperEdits { get; set; }

    public Func<HostResult?>? Override { get; set; }

    /// <summary>Stage 5's structure check answers "fine" without looking, to reach the checks behind it.</summary>
    public bool SkipValidate { get; set; }

    /// <summary>Awaited at the start of AppendAsync: a test holds the save here to look at what is going on around it.</summary>
    public Task? Gate { get; set; }

    public async Task<HostResult> AppendAsync(SafeFileHandle baseFile, ReadOnlyMemory<byte> blob, SafeFileHandle output, int pageCount, TimeSpan deadline, CancellationToken cancellationToken)
    {
        Calls.Add("append");
        if (Gate is not null)
        {
            await Gate.ConfigureAwait(false);
        }

        if (Override?.Invoke() is { } forced)
        {
            return forced;
        }

        if (!EditBlobCodec.TryDecode(blob, out var decoded, out _))
        {
            return new HostResult(false, "HostFailed");
        }

        var edits = TamperEdits is null ? decoded.Edits : TamperEdits(decoded.Edits);
        var opened = StrictDocument.Open(ReadAll(baseFile));
        if (opened.TryGetError(out var openError))
        {
            return Refused(openError);
        }

        var appended = AnnotationAppender.Append(opened.Value, pageCount, [.. edits.Select(ToChange)]);
        if (appended.TryGetError(out var error))
        {
            return Refused(error);
        }

        var bytes = Corrupt is null ? appended.Value.Output : Corrupt(appended.Value.Output);
        RandomAccess.Write(output, bytes, 0);
        RandomAccess.FlushToDisk(output);
        return new HostResult(true, null, appended.Value.AnnotationsAdded, appended.Value.AnnotationsRemoved, bytes.Length);
    }

    public Task<HostResult> RewriteAsync(SafeFileHandle baseFile, SafeFileHandle output, uint flags, TimeSpan deadline, CancellationToken cancellationToken)
    {
        Calls.Add("rewrite");
        if (Override?.Invoke() is { } forced)
        {
            return Task.FromResult(forced);
        }

        var opened = StrictDocument.Open(ReadAll(baseFile));
        if (opened.TryGetError(out var openError))
        {
            return Task.FromResult(Refused(openError));
        }

        var rewritten = DocumentRewriter.Rewrite(opened.Value, new RewriteOptions { FlattenForms = (flags & 1) != 0, FlattenComments = (flags & 2) != 0 });
        if (rewritten.TryGetError(out var error))
        {
            return Task.FromResult(Refused(error));
        }

        RandomAccess.Write(output, rewritten.Value.Output, 0);
        RandomAccess.FlushToDisk(output);
        return Task.FromResult(new HostResult(true, Bytes: rewritten.Value.Output.Length));
    }

    public Task<HostResult> ValidateAsync(SafeFileHandle file, TimeSpan deadline, CancellationToken cancellationToken)
    {
        Calls.Add("validate");
        if (SkipValidate)
        {
            return Task.FromResult(new HostResult(true));
        }

        var report = StructureValidator.Validate(ReadAll(file));
        return Task.FromResult(report.IsValid
            ? new HostResult(true)
            : new HostResult(false, "structure", ErrorCodes: [.. report.Issues.Where(static i => i.Severity == ValidationSeverity.Error).Select(static i => (uint)i.Code)]));
    }

    public Task<HostResult> VerifyAsync(SafeFileHandle baseFile, SafeFileHandle output, int pageCount, uint[] pages, uint[] added, uint[] removed, TimeSpan deadline, CancellationToken cancellationToken)
    {
        Calls.Add("verify");
        var before = Count(ReadAll(baseFile), pageCount, pages);
        var after = Count(ReadAll(output), pageCount, pages);
        if (before is null || after is null)
        {
            return Task.FromResult(new HostResult(false, "verify"));
        }

        var mismatches = pages.Where((_, i) => after[i] != before[i] + added[i] - removed[i]).ToList();
        return Task.FromResult(new HostResult(true, Mismatches: mismatches));
    }

    internal static int[]? Count(byte[] bytes, int pageCount, uint[] pages)
    {
        var opened = StrictDocument.Open(bytes);
        if (opened.TryGetError(out _))
        {
            return null;
        }

        var tree = PageTree.Enumerate(opened.Value);
        if (tree.TryGetError(out _) || tree.Value.Count != pageCount)
        {
            return null;
        }

        return [.. pages.Select(p =>
        {
            var annots = tree.Value[(int)p].Dictionary["Annots"];
            return annots is not null && opened.Value.Resolve(annots).Value is PdfArray array
                ? array.Count(a => opened.Value.Resolve(a).Value is PdfDictionary d && d.GetName("Subtype") is not ("Link" or "Popup" or "Widget"))
                : 0;
        })];
    }

    private static HostResult Refused(PdfError error) => new(false, WriterErrorCodes.NameOf((uint)error.Code));

    private static AnnotationChange ToChange(AnnotationEdit edit) => new()
    {
        Kind = (AnnotationEditKind)(int)edit.Operation,
        Page = (int)edit.Page,
        Annotation = (NewAnnotationKind)(int)edit.Kind,
        Rect = edit.Rect,
        Color = edit.Color,
        Opacity = edit.Opacity,
        Width = edit.Width,
        Contents = edit.Contents,
        Author = edit.Author,
        Name = edit.Name,
        Flags = edit.Flags,
        QuadPoints = edit.QuadPoints,
        Strokes = edit.Strokes,
        TargetObject = (int)edit.TargetObject,
        ExpectSubtype = edit.ExpectSubtype,
        ExpectRect = edit.ExpectRect,
    };

    internal static byte[] ReadAll(SafeFileHandle file)
    {
        var length = RandomAccess.GetLength(file);
        var bytes = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = RandomAccess.Read(file, bytes.AsSpan(read), read);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return bytes;
    }
}

/// <summary>The replace layer with scripted answers, so a test can make the POSIX rename fail the way SMB does, or a lock clear after a while.</summary>
internal sealed class ScriptedNative(IReplaceNative inner) : IReplaceNative
{
    public Queue<int> PosixAnswers { get; } = new();

    public int PosixCalls { get; private set; }

    public int ReplaceFileCalls { get; private set; }

    /// <summary>Runs in place of the real ReplaceFileW. Return the Win32 error, or throw to stand in for a kill part way through it.</summary>
    public Func<string, string, string?, int>? ReplaceFileOverride { get; set; }

    public int PosixRename(SafeFileHandle temp, string target)
    {
        PosixCalls++;
        return PosixAnswers.TryDequeue(out var answer) ? answer : inner.PosixRename(temp, target);
    }

    public int ReplaceFile(string target, string replacement, string? backup)
    {
        ReplaceFileCalls++;
        return ReplaceFileOverride is { } over ? over(target, replacement, backup) : inner.ReplaceFile(target, replacement, backup);
    }
}

/// <summary>A scratch directory on a local NTFS volume for one test, with the PDF fixtures it needs.</summary>
internal sealed class SaveWorkspace : IDisposable
{
    public SaveWorkspace()
    {
        Directory = Path.Combine(Path.GetTempPath(), "mikepdf-save-" + Guid.NewGuid().ToString("N")[..10]);
        System.IO.Directory.CreateDirectory(Directory);
        Journals = Path.Combine(Directory, "private", "journals");
    }

    public string Directory { get; }

    public string Journals { get; }

    public string Combine(string name) => Path.Combine(Directory, name);

    /// <summary>The folder the documents live in: a sub folder, so listing it shows only what a save left there.</summary>
    public string Documents
    {
        get
        {
            var folder = Combine("docs");
            System.IO.Directory.CreateDirectory(folder);
            return folder;
        }
    }

    public string Copy(string fixture, string name = "doc.pdf")
    {
        var path = Path.Combine(Documents, name);
        File.Copy(Fixture(fixture), path);
        return path;
    }

    public string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(Documents, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public static string Fixture(string name)
    {
        var integration = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        if (File.Exists(integration))
        {
            return integration;
        }

        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "MikePDF.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("MikePDF.slnx not found");
        }

        return Path.Combine(root, "tests", "MikePDF.PdfWriter.Tests", "Fixtures", "pdfium", name);
    }

    /// <summary>Every file beneath the documents folder, by name: what a save leaves behind.</summary>
    public string[] Listing() => [.. System.IO.Directory.EnumerateFileSystemEntries(Documents, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    public void Dispose()
    {
        try
        {
            foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(Directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Not ours to fix: the delete below reports it.
                }
            }

            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A scanner or a leftover handle: the temp folder is cleaned up eventually.
        }
    }
}

internal static class Soundness
{
    /// <summary>True when the output has no structure error that the baseline did not already have (real files come with faults; a save must not add one).</summary>
    public static bool NoNewErrors(byte[] output, byte[] baseline)
    {
        static Dictionary<ValidationCode, int> Errors(byte[] bytes) =>
            StructureValidator.Validate(bytes).Issues.Where(static i => i.Severity == ValidationSeverity.Error).GroupBy(static i => i.Code).ToDictionary(static g => g.Key, static g => g.Count());
        var allowed = Errors(baseline);
        return Errors(output).All(pair => pair.Value <= allowed.GetValueOrDefault(pair.Key));
    }
}

internal static class Commands
{
    public static EditCommand Highlight(string name = "h1", int page = 0, string contents = "check this") => EditCommand.Create("AddAnnotation", new
    {
        page,
        kind = "Highlight",
        name,
        rect = new[] { 10f, 20f, 110f, 40f },
        color = new[] { 1f, 0.9f, 0f },
        opacity = 0.5f,
        contents,
        author = "Mike",
        quads = new[] { 10f, 40f, 110f, 40f, 10f, 20f, 110f, 20f },
    });

    public static EditCommand Note(string name = "n1", int page = 0) => EditCommand.Create("AddAnnotation", new
    {
        page,
        kind = "Text",
        name,
        rect = new[] { 150f, 150f, 170f, 170f },
        color = new[] { 1f, 0.8f, 0.1f },
        contents = "a note",
    });
}

internal static class Native
{
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newFile, string existing, nint reserved);

    public static void HardLink(string newFile, string existing)
    {
        if (!CreateHardLink(newFile, existing, 0))
        {
            throw new IOException("CreateHardLink failed: " + Marshal.GetLastWin32Error());
        }
    }

    /// <summary>A read-only view of the file, the way an engine holds it.</summary>
    public static MemoryMappedViewAccessor MapReadOnly(SafeFileHandle file, long length)
    {
        var mapping = MemoryMappedFile.CreateFromFile(file, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        return mapping.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
    }
}
