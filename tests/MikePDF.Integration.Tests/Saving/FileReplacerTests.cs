using System.Security.AccessControl;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MikePDF.Broker;
using MikePDF.Broker.Saving;

namespace MikePDF.Integration.Tests.Saving;

/// <summary>
/// SP-17, SP-20, SP-38 to SP-41 (save-pipeline 14): the replace step on a real NTFS volume, with the document held open the way the broker holds
/// it and mapped the way an engine maps it.
/// </summary>
public sealed class FileReplacerTests : IDisposable
{
    private static readonly byte[] OldBytes = Encoding.ASCII.GetBytes("the old file, held open by the broker and mapped by the engines");
    private static readonly byte[] NewBytes = Encoding.ASCII.GetBytes("the new file, validated and ready to be renamed over it");

    private readonly SaveWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static SafeFileHandle OpenLikeTheBroker(string path) => File.OpenHandle(path, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare);

    private static (SafeFileHandle Handle, string Path) NewTemp(string target, byte[] content)
    {
        var name = $".{Path.GetFileName(target)}.mikepdf-{Guid.NewGuid().ToString("N")[..8]}.tmp";
        var handle = FileReplacer.CreateTemporary(target, name, out var path);
        RandomAccess.Write(handle, content, 0);
        return (FileReplacer.ReopenForRename(handle, path), path); // as the save pipeline does once the output is written
    }

    private static string Sddl(string path) => new FileInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    private static byte[] ReadStream(string path, string stream)
    {
        using var handle = SaveNative.CreateFile(path + stream, SaveNative.GenericRead, SaveNative.FileShareAll, 0, SaveNative.OpenExisting, 0, 0);
        Assert.False(handle.IsInvalid, "the stream " + stream + " is missing");
        using var reader = new FileStream(handle, FileAccess.Read, 1);
        using var copy = new MemoryStream();
        reader.CopyTo(copy);
        return copy.ToArray();
    }

    private static void WriteStream(string path, string stream, byte[] content)
    {
        using var handle = SaveNative.CreateFile(path + stream, SaveNative.GenericWrite, 1, 0, 2, 0, 0);
        Assert.False(handle.IsInvalid);
        using var writer = new FileStream(handle, FileAccess.Write, 1);
        writer.Write(content);
    }

    /// <summary>An original with everything a replace must keep: an explicit protected DACL, the Mark of the Web and another stream, attributes, an old creation time.</summary>
    private string MakeRichOriginal()
    {
        var path = _workspace.Write("rich.pdf", OldBytes);
        WriteStream(path, ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.org/a.pdf\r\n"u8.ToArray());
        WriteStream(path, ":mikepdf-test", "custom stream"u8.ToArray());
        var security = new FileSecurity();
        using (var user = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null), FileSystemRights.Read, AccessControlType.Allow));
        }

        new FileInfo(path).SetAccessControl(security);
        File.SetAttributes(path, FileAttributes.Archive | FileAttributes.NotContentIndexed);
        File.SetCreationTimeUtc(path, new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        return path;
    }

    // ---- SP-38: open flags ----

    [Fact]
    public void WhileADocumentIsOpenAnotherProcessCanRenameItButNotWriteToIt()
    {
        var path = _workspace.Write("doc.pdf", OldBytes);
        using var held = OpenLikeTheBroker(path);

        Assert.ThrowsAny<IOException>(() => File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
        Assert.ThrowsAny<IOException>(() => File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete).Dispose());
        Assert.ThrowsAny<IOException>(() => File.OpenHandle(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());

        var renamed = _workspace.Combine("docs\\renamed.pdf");
        File.Move(path, renamed); // the path is not protected...
        Assert.Equal(OldBytes, ReadAllFrom(held)); // ...and the contents the broker holds are
    }

    [Fact]
    public void TheHandleAndAMappedViewReadTheOriginalBytesAfterAnExternalReplace()
    {
        var path = _workspace.Write("doc.pdf", OldBytes);
        using var held = OpenLikeTheBroker(path);
        using var view = Native.MapReadOnly(held, OldBytes.Length);

        var (temp, tempPath) = NewTemp(path, NewBytes);
        new FileReplacer().Replace(path, temp, tempPath, FileReplacer.IdentityOf(held), backup: null);

        Assert.Equal(NewBytes, File.ReadAllBytes(path));
        Assert.Equal(OldBytes, ReadAllFrom(held));
        var mapped = new byte[OldBytes.Length];
        view.ReadArray(0, mapped, 0, mapped.Length);
        Assert.Equal(OldBytes, mapped);
    }

    // ---- SP-17: the replace keeps what the user's file had ----

    [Fact]
    public void ThePosixRenameKeepsTheDescriptorTheStreamsTheAttributesAndTheCreationTime()
    {
        var path = MakeRichOriginal();
        var sddl = Sddl(path);
        using var held = OpenLikeTheBroker(path);
        using var view = Native.MapReadOnly(held, OldBytes.Length);
        var (temp, tempPath) = NewTemp(path, NewBytes);

        var outcome = new FileReplacer().Replace(path, temp, tempPath, FileReplacer.IdentityOf(held), backup: null);

        Assert.Equal(ReplaceMechanism.PosixRename, outcome.Mechanism);
        AssertKept(path, sddl);
    }

    [Fact]
    public void TheReplaceFileFallbackKeepsTheSameThings()
    {
        var path = MakeRichOriginal();
        var sddl = Sddl(path);
        var backup = Path.Combine(_workspace.Documents, ".rich.pdf.mikepdf-feedbeef.bak");
        var native = new ScriptedNative(new WindowsReplaceNative());
        native.PosixAnswers.Enqueue(87); // what SMB answers
        using var held = OpenLikeTheBroker(path);
        var (temp, tempPath) = NewTemp(path, NewBytes);

        var outcome = new FileReplacer(native).Replace(path, temp, tempPath, FileReplacer.IdentityOf(held), backup);

        Assert.Equal(ReplaceMechanism.ReplaceFile, outcome.Mechanism);
        Assert.Equal(1, native.PosixCalls);
        Assert.Equal(1, native.ReplaceFileCalls);
        Assert.Equal(backup, outcome.BackupPath);
        Assert.Equal(OldBytes, File.ReadAllBytes(backup)); // the original's bytes are in the recorded backup
        AssertKept(path, sddl);
        Assert.False(File.Exists(tempPath));
    }

    private static void AssertKept(string path, string sddl)
    {
        Assert.Equal(NewBytes, File.ReadAllBytes(path));
        Assert.Equal(sddl, Sddl(path));
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://example.org/a.pdf\r\n"u8.ToArray(), ReadStream(path, ":Zone.Identifier"));
        Assert.Equal("custom stream"u8.ToArray(), ReadStream(path, ":mikepdf-test"));
        var attributes = File.GetAttributes(path);
        Assert.True((attributes & FileAttributes.Archive) != 0);
        Assert.True((attributes & FileAttributes.NotContentIndexed) != 0);
        Assert.True((attributes & FileAttributes.Hidden) == 0, "the temporary file's Hidden attribute must not reach the user's file");
        Assert.Equal(new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc), File.GetCreationTimeUtc(path));
    }

    [Fact]
    public void AFileTheUserHidThemselvesStaysHidden()
    {
        var path = _workspace.Write("secret.pdf", OldBytes);
        File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.Archive);
        var (temp, tempPath) = NewTemp(path, NewBytes);

        new FileReplacer().Replace(path, temp, tempPath, expected: null, backup: null);

        Assert.True((File.GetAttributes(path) & FileAttributes.Hidden) != 0);
    }

    [Fact]
    public void ANewTargetGetsAVisibleFileWithTheFoldersInheritedPermissions()
    {
        var target = Path.Combine(_workspace.Documents, "copy.pdf");
        var (temp, tempPath) = NewTemp(target, NewBytes);
        Assert.True((File.GetAttributes(tempPath) & FileAttributes.Hidden) != 0);

        var outcome = new FileReplacer().Replace(target, temp, tempPath, expected: null, backup: null);

        Assert.Equal(NewBytes, File.ReadAllBytes(target));
        Assert.False(File.GetAttributes(target).HasFlag(FileAttributes.Hidden));
        Assert.Equal(ReplaceMechanism.PosixRename, outcome.Mechanism);
        Assert.Equal(["copy.pdf"], _workspace.Listing());
    }

    // ---- SP-39: the path no longer names the file that was opened ----

    [Fact]
    public void APathReplacedByAnotherFileIsRefusedForConfirmationAndNothingIsChanged()
    {
        var path = _workspace.Write("doc.pdf", OldBytes);
        using var held = OpenLikeTheBroker(path);
        var identity = FileReplacer.IdentityOf(held);
        File.Move(path, _workspace.Combine("docs\\aside.pdf"));
        File.WriteAllBytes(path, "someone else's file"u8.ToArray());
        var (temp, tempPath) = NewTemp(path, NewBytes);

        var error = Assert.Throws<ReplaceException>(() => new FileReplacer().Replace(path, temp, tempPath, identity, backup: null));

        Assert.Equal(ReplaceFailure.ChangedOnDisk, error.Failure);
        Assert.Equal("someone else's file"u8.ToArray(), File.ReadAllBytes(path));
    }

    [Fact]
    public void APathThatWasDeletedIsReportedAsGoneWithSaveAsToOffer()
    {
        var path = _workspace.Write("doc.pdf", OldBytes);
        using var held = OpenLikeTheBroker(path);
        var identity = FileReplacer.IdentityOf(held);
        File.Delete(path);
        var (temp, tempPath) = NewTemp(path, NewBytes);

        var error = Assert.Throws<ReplaceException>(() => new FileReplacer().Replace(path, temp, tempPath, identity, backup: null));

        Assert.Equal(ReplaceFailure.TargetGone, error.Failure);
        Assert.False(File.Exists(path));
        Assert.Equal(OldBytes, ReadAllFrom(held)); // the open document still reads
    }

    [Fact]
    public void ASameFileIsRecognisedWhateverItsNameNow()
    {
        var path = _workspace.Write("doc.pdf", OldBytes);
        using var held = OpenLikeTheBroker(path);
        var before = FileReplacer.IdentityOf(held);
        var renamed = _workspace.Combine("docs\\renamed.pdf");
        File.Move(path, renamed);

        Assert.Equal(before, FileReplacer.IdentityOfPath(renamed));
        Assert.NotEqual(before, FileReplacer.IdentityOfPath(_workspace.Write("other.pdf", OldBytes)));
    }

    // ---- SP-40: links and the files a replace would damage ----

    [Fact]
    public void AFileWithAnotherHardLinkIsRefusedBecauseTheOtherNameWouldKeepTheOldContent()
    {
        var path = _workspace.Write("doc.pdf", OldBytes);
        Native.HardLink(_workspace.Combine("docs\\second-name.pdf"), path);

        var info = FileReplacer.Inspect(path);

        Assert.Equal(TargetProblem.HardLinks, info.Problem);
        Assert.Equal(2u, info.LinkCount);
        Assert.Equal(TargetProblem.None, FileReplacer.Inspect(_workspace.Write("alone.pdf", OldBytes)).Problem);
    }

    [Fact]
    public void AReadOnlyFileIsRefused()
    {
        var path = _workspace.Write("doc.pdf", OldBytes);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        Assert.Equal(TargetProblem.ReadOnly, FileReplacer.Inspect(path).Problem);
    }

    [Theory]
    [InlineData(FileAttributes.Encrypted, 1u, TargetProblem.Encrypted)]
    [InlineData(FileAttributes.Encrypted | FileAttributes.Archive, 3u, TargetProblem.Encrypted)]
    [InlineData(FileAttributes.ReadOnly | FileAttributes.Encrypted, 1u, TargetProblem.ReadOnly)]
    [InlineData(FileAttributes.Archive, 2u, TargetProblem.HardLinks)]
    [InlineData(FileAttributes.Archive | FileAttributes.Hidden, 1u, TargetProblem.None)]
    public void EachProblemHasItsOwnAnswer(FileAttributes attributes, uint links, TargetProblem expected)
    {
        // EFS is decided on the attribute alone. Encrypting a real file would create an encryption certificate in the user's store, which a test must not do.
        Assert.Equal(expected, FileReplacer.ProblemFor(attributes, links));
    }

    [Fact]
    public void ASymbolicLinkIsFollowedSoTheLinkSurvivesAndTheRealFileIsUpdated()
    {
        var real = _workspace.Write("real.pdf", OldBytes);
        var link = Path.Combine(_workspace.Documents, "link.pdf");
        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("creating a symbolic link needs Developer Mode or elevation here: " + e.Message);
        }

        using var held = OpenLikeTheBroker(link);
        var (temp, tempPath) = NewTemp(link, NewBytes);

        var outcome = new FileReplacer().Replace(link, temp, tempPath, FileReplacer.IdentityOf(held), backup: null);

        Assert.Equal(real, outcome.FinalPath, ignoreCase: true);
        Assert.NotNull(new FileInfo(link).LinkTarget); // still a link
        Assert.Equal(NewBytes, File.ReadAllBytes(real));
        Assert.Equal(NewBytes, File.ReadAllBytes(link));
    }

    // ---- SP-20 and SP-41: retries and the fallback ----

    [Fact]
    public void ALockThatClearsWithinTheRetriesIsRetriedUntilTheReplaceSucceeds()
    {
        var path = _workspace.Write("doc.pdf", OldBytes);
        var native = new ScriptedNative(new WindowsReplaceNative());
        native.PosixAnswers.Enqueue(SaveNative.ErrorSharingViolation);
        native.PosixAnswers.Enqueue(SaveNative.ErrorAccessDenied);
        native.PosixAnswers.Enqueue(SaveNative.ErrorUnableToRemoveReplaced);
        var (temp, tempPath) = NewTemp(path, NewBytes);

        var outcome = new FileReplacer(native, attempts: 5, delay: TimeSpan.FromMilliseconds(5)).Replace(path, temp, tempPath, expected: null, backup: null);

        Assert.Equal(4, outcome.Attempts);
        Assert.Equal(NewBytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void ALockThatDoesNotClearFailsCleanlyAfterFiveAttemptsAndTheOriginalIsIntact()
    {
        var path = _workspace.Write("doc.pdf", OldBytes);
        var native = new ScriptedNative(new WindowsReplaceNative());
        for (var i = 0; i < 20; i++)
        {
            native.PosixAnswers.Enqueue(SaveNative.ErrorSharingViolation);
        }

        var (temp, tempPath) = NewTemp(path, NewBytes);

        var error = Assert.Throws<ReplaceException>(() => new FileReplacer(native, attempts: 5, delay: TimeSpan.FromMilliseconds(5)).Replace(path, temp, tempPath, expected: null, backup: null));

        Assert.Equal((ReplaceFailure.InUse, SaveNative.ErrorSharingViolation), (error.Failure, error.Win32Error));
        Assert.Equal(5, native.PosixCalls);
        Assert.Equal(OldBytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void AnErrorThatIsNotALockIsNotRetried()
    {
        var path = _workspace.Write("doc.pdf", OldBytes);
        var native = new ScriptedNative(new WindowsReplaceNative());
        native.PosixAnswers.Enqueue(1117); // an I/O device error
        var (temp, tempPath) = NewTemp(path, NewBytes);

        var error = Assert.Throws<ReplaceException>(() => new FileReplacer(native, attempts: 5, delay: TimeSpan.FromMilliseconds(5)).Replace(path, temp, tempPath, expected: null, backup: null));

        Assert.Equal(ReplaceFailure.Native, error.Failure);
        Assert.Equal(1, native.PosixCalls);
    }

    [Theory]
    [InlineData(87)]
    [InlineData(50)]
    [InlineData(1)]
    public void AnUnsupportedRenameFallsBackToReplaceFileOnceAndLeavesNothingElseBehind(int unsupported)
    {
        var path = _workspace.Write("doc.pdf", OldBytes);
        var backup = Path.Combine(_workspace.Documents, ".doc.pdf.mikepdf-00000001.bak");
        var native = new ScriptedNative(new WindowsReplaceNative());
        native.PosixAnswers.Enqueue(unsupported);
        var (temp, tempPath) = NewTemp(path, NewBytes);

        var outcome = new FileReplacer(native).Replace(path, temp, tempPath, expected: null, backup);
        File.Delete(outcome.BackupPath!); // the coordinator removes it after the commit

        Assert.Equal(ReplaceMechanism.ReplaceFile, outcome.Mechanism);
        Assert.Equal(1, native.PosixCalls);
        Assert.Equal(NewBytes, File.ReadAllBytes(path));
        Assert.Equal(["doc.pdf"], _workspace.Listing());
    }

    [Fact]
    public void ANewTargetOnAVolumeWithoutThePosixRenameIsMovedIn()
    {
        var target = Path.Combine(_workspace.Documents, "copy.pdf");
        var native = new ScriptedNative(new WindowsReplaceNative());
        native.PosixAnswers.Enqueue(87);
        var (temp, tempPath) = NewTemp(target, NewBytes);

        var outcome = new FileReplacer(native).Replace(target, temp, tempPath, expected: null, backup: null);

        Assert.Equal(ReplaceMechanism.Move, outcome.Mechanism);
        Assert.Equal(["copy.pdf"], _workspace.Listing());
    }

    // ---- a ReplaceFileW that fails after it has started (the fallback must never leave the document missing) ----

    private static string[] Sorted(params string[] names) => [.. names.Order(StringComparer.Ordinal)];

    /// <summary>A native layer whose POSIX rename is unsupported (so the fallback runs) and whose ReplaceFileW runs <paramref name="replaceFile"/> in place of the real one.</summary>
    private static ScriptedNative FallbackWith(Func<string, string, string?, int> replaceFile)
    {
        var native = new ScriptedNative(new WindowsReplaceNative());
        native.PosixAnswers.Enqueue(87);
        native.ReplaceFileOverride = replaceFile;
        return native;
    }

    private static int MovedTheOriginalAsideThenFailed(string target, string backup, int error)
    {
        File.Move(target, backup); // what ReplaceFileW did before it failed to move the replacement into place
        return error;
    }

    [Fact]
    public void WhenReplaceFileMovesTheOriginalAsideAndFailsTheOriginalIsPutBackAtOnceAndTheTemporaryFileIsKept()
    {
        var path = _workspace.Write("doc.pdf", OldBytes);
        var backup = Path.Combine(_workspace.Documents, ".doc.pdf.mikepdf-00000001.bak");
        var native = FallbackWith((target, _, back) => MovedTheOriginalAsideThenFailed(target, back!, 1177)); // ERROR_UNABLE_TO_MOVE_REPLACEMENT_2
        var (temp, tempPath) = NewTemp(path, NewBytes);

        var error = Assert.Throws<ReplaceException>(() => new FileReplacer(native, attempts: 5, delay: TimeSpan.FromMilliseconds(5)).Replace(path, temp, tempPath, expected: null, backup));

        Assert.Equal((ReplaceFailure.Native, 1177), (error.Failure, error.Win32Error));
        Assert.Equal(OldBytes, File.ReadAllBytes(path)); // the user's file is where it was, with its own bytes
        Assert.Equal(1, native.MoveCalls);
        Assert.Equal(1, native.ReplaceFileCalls); // not a lock: no retry
        Assert.Equal(NewBytes, File.ReadAllBytes(tempPath)); // the caller deletes the temporary file; the replacer does not
        Assert.Equal(Sorted("doc.pdf", Path.GetFileName(tempPath)), _workspace.Listing());
    }

    [Fact]
    public void WhenReplaceFileFailsToMoveTheReplacementButLeavesBothFilesNamedAsTheyWereNothingIsMoved()
    {
        // ERROR_UNABLE_TO_MOVE_REPLACEMENT (1176), with a backup name: the documented outcome is that both files keep their names. Checked here, not assumed.
        var path = _workspace.Write("doc.pdf", OldBytes);
        var backup = Path.Combine(_workspace.Documents, ".doc.pdf.mikepdf-00000001.bak");
        var native = FallbackWith((_, _, _) => 1176);
        var (temp, tempPath) = NewTemp(path, NewBytes);

        var error = Assert.Throws<ReplaceException>(() => new FileReplacer(native, attempts: 5, delay: TimeSpan.FromMilliseconds(5)).Replace(path, temp, tempPath, expected: null, backup));

        Assert.Equal((ReplaceFailure.Native, 1176), (error.Failure, error.Win32Error));
        Assert.Equal(OldBytes, File.ReadAllBytes(path));
        Assert.Equal(0, native.MoveCalls); // the target is there, so there is nothing to put back
        Assert.Equal(Sorted("doc.pdf", Path.GetFileName(tempPath)), _workspace.Listing());
    }

    [Fact]
    public void WhenReplaceFileFailsAndTheOriginalCannotBePutBackNothingIsDeletedAndTheErrorSaysWhereItIs()
    {
        var path = _workspace.Write("doc.pdf", OldBytes);
        var backup = Path.Combine(_workspace.Documents, ".doc.pdf.mikepdf-00000001.bak");
        var native = FallbackWith((target, _, back) => MovedTheOriginalAsideThenFailed(target, back!, 1177));
        native.MoveAnswers.Enqueue(1117); // the put-back fails with an error that is not a lock
        var (temp, tempPath) = NewTemp(path, NewBytes);

        var error = Assert.Throws<ReplaceException>(() => new FileReplacer(native, attempts: 5, delay: TimeSpan.FromMilliseconds(5)).Replace(path, temp, tempPath, expected: null, backup));

        Assert.Equal((ReplaceFailure.RestorePending, 1117), (error.Failure, error.Win32Error));
        Assert.Equal(backup, error.OriginalAt);
        Assert.False(File.Exists(path));
        Assert.Equal(OldBytes, File.ReadAllBytes(backup)); // the original, untouched, under the name the journal records
        Assert.Equal(NewBytes, File.ReadAllBytes(tempPath));
        Assert.Equal(1, native.MoveCalls);
        Assert.Equal(Sorted(Path.GetFileName(backup), Path.GetFileName(tempPath)), _workspace.Listing());
    }

    [Fact]
    public void ALockOnThePutBackIsRetriedAndWhenItClearsTheOriginalIsBack()
    {
        var path = _workspace.Write("doc.pdf", OldBytes);
        var backup = Path.Combine(_workspace.Documents, ".doc.pdf.mikepdf-00000001.bak");
        var native = FallbackWith((target, _, back) => MovedTheOriginalAsideThenFailed(target, back!, 1177));
        native.MoveAnswers.Enqueue(SaveNative.ErrorSharingViolation);
        native.MoveAnswers.Enqueue(SaveNative.ErrorAccessDenied);
        var (temp, tempPath) = NewTemp(path, NewBytes);

        var error = Assert.Throws<ReplaceException>(() => new FileReplacer(native, attempts: 5, delay: TimeSpan.FromMilliseconds(5)).Replace(path, temp, tempPath, expected: null, backup));

        Assert.Equal(ReplaceFailure.Native, error.Failure);
        Assert.Equal(3, native.MoveCalls);
        Assert.Equal(OldBytes, File.ReadAllBytes(path));
        Assert.Equal(Sorted("doc.pdf", Path.GetFileName(tempPath)), _workspace.Listing());
    }

    [Fact]
    public void APutBackThatStaysLockedForEveryAttemptIsPendingNotLost()
    {
        var path = _workspace.Write("doc.pdf", OldBytes);
        var backup = Path.Combine(_workspace.Documents, ".doc.pdf.mikepdf-00000001.bak");
        var native = FallbackWith((target, _, back) => MovedTheOriginalAsideThenFailed(target, back!, 1177));
        for (var i = 0; i < 20; i++)
        {
            native.MoveAnswers.Enqueue(SaveNative.ErrorSharingViolation);
        }

        var (temp, tempPath) = NewTemp(path, NewBytes);

        var error = Assert.Throws<ReplaceException>(() => new FileReplacer(native, attempts: 5, delay: TimeSpan.FromMilliseconds(5)).Replace(path, temp, tempPath, expected: null, backup));

        Assert.Equal(ReplaceFailure.RestorePending, error.Failure);
        Assert.Equal(5, native.MoveCalls);
        Assert.Equal(OldBytes, File.ReadAllBytes(backup));
        Assert.Equal(Sorted(Path.GetFileName(backup), Path.GetFileName(tempPath)), _workspace.Listing());
    }

    private static byte[] ReadAllFrom(SafeFileHandle file)
    {
        var bytes = new byte[RandomAccess.GetLength(file)];
        _ = RandomAccess.Read(file, bytes, 0);
        return bytes;
    }
}
