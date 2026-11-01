using System.Security.AccessControl;
using System.Security.Principal;
using Quire.Broker;

namespace Quire.Integration.Tests;

/// <summary>The directory the broker keeps to itself (recent files, positions): protected, user and SYSTEM only.</summary>
public sealed class PrivateDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quire-private-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static List<FileSystemAccessRule> Rules(string path) =>
        [.. new DirectoryInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()];

    [Fact]
    public void TheDirectoryIsProtectedAndNamesOnlyTheUserAndSystem()
    {
        var path = Path.Combine(_root, "private");
        PrivateDirectory.Ensure(path);

        Assert.True(new DirectoryInfo(path).GetAccessControl().AreAccessRulesProtected);
        var rules = Rules(path);
        Assert.Equal(2, rules.Count);
        Assert.All(rules, r => Assert.False(r.IsInherited));
        Assert.All(rules, r => Assert.True(r.FileSystemRights.HasFlag(FileSystemRights.FullControl)));
        using var me = WindowsIdentity.GetCurrent();
        Assert.Contains(rules, r => r.IdentityReference.Equals(me.User));
        Assert.Contains(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.True(PrivateDirectory.IsExactlyUserAndSystem(new DirectoryInfo(path)));
    }

    [Fact]
    public void FilesCreatedInsideInheritThatAndNothingElse()
    {
        var path = Path.Combine(_root, "private");
        PrivateDirectory.Ensure(path);
        var file = Path.Combine(path, "documents.json");
        File.WriteAllText(file, "{}");

        var rules = new FileInfo(file).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.Equal(2, rules.Count);
        Assert.All(rules, r => Assert.True(r.IsInherited));
    }

    [Fact]
    public void AWidenedAclIsRepairedAndAnExistingFileKeepsWorking()
    {
        var path = Path.Combine(_root, "private");
        PrivateDirectory.Ensure(path);
        File.WriteAllText(Path.Combine(path, "a.txt"), "x");

        // Someone (or something running as the user) grants "Everyone" read, or turns inheritance back on.
        var info = new DirectoryInfo(path);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
        Assert.False(PrivateDirectory.IsExactlyUserAndSystem(info));

        PrivateDirectory.Ensure(path);
        Assert.True(PrivateDirectory.IsExactlyUserAndSystem(new DirectoryInfo(path)));
        Assert.Equal("x", File.ReadAllText(Path.Combine(path, "a.txt")));
    }

    [Fact]
    public void AnUnprotectedDirectoryIsNotAccepted()
    {
        Directory.CreateDirectory(_root);
        Assert.False(PrivateDirectory.IsExactlyUserAndSystem(new DirectoryInfo(_root)));
    }

    [Fact]
    public void EnsureIsIdempotent()
    {
        var path = Path.Combine(_root, "deeper", "private");
        PrivateDirectory.Ensure(path);
        PrivateDirectory.Ensure(path);
        Assert.True(PrivateDirectory.IsExactlyUserAndSystem(new DirectoryInfo(path)));
    }
}
