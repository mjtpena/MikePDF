using System.Security.AccessControl;
using System.Security.Principal;
using MikePDF.Broker;
using MikePDF.Core.Editing.Journal;

namespace MikePDF.Integration.Tests.Saving;

/// <summary>
/// save-pipeline 3.4 and OQ-SP-7: the journals hold what the user typed, so they live in the broker-private folder, which the sandboxed engine's token
/// matches no entry of (ADR-0004 amendment A2). A journals folder made beneath it inherits that, and so does every journal in it.
/// </summary>
public sealed class PrivateJournalTests : IDisposable
{
    private readonly SaveWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void AJournalInThePrivateFolderCanBeOpenedByTheUserAndSystemAndNobodyElse()
    {
        var privateFolder = _workspace.Combine("private");
        PrivateDirectory.Ensure(privateFolder);
        var document = _workspace.Write("doc.pdf", "%PDF-1.7\n"u8.ToArray());
        var store = new JournalStore(Path.Combine(privateFolder, "journals"));
        using var session = new EditSession(store, document, "1.0.0.0");

        session.Model.Apply(Commands.Highlight(contents: "a private comment"));

        var journal = Assert.Single(store.List()).Path;
        using var user = WindowsIdentity.GetCurrent();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        foreach (var path in new[] { journal, Path.GetDirectoryName(journal)! })
        {
            var security = File.GetAttributes(path).HasFlag(FileAttributes.Directory)
                ? new DirectoryInfo(path).GetAccessControl()
                : (FileSystemSecurity)new FileInfo(path).GetAccessControl();
            var identities = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Select(r => r.IdentityReference).Distinct().ToList();
            Assert.All(identities, id => Assert.True(id.Equals(user.User) || id.Equals(system), $"{id} can open {Path.GetFileName(path)}"));
            Assert.Contains(user.User, identities);
        }
    }
}
