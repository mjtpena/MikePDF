using System.Security.AccessControl;
using System.Security.Principal;

namespace MikePDF.Broker;

/// <summary>
/// A directory only the signed-in user and SYSTEM can open: what the broker keeps to itself (the recent-files list, last positions).
/// <para>
/// Why it is needed. The sandboxed engine of a packaged app carries one capability, the package's own, and the package's data folders grant that
/// capability full control (ADR-0004 amendment A2). Anything the broker writes into LocalState is therefore readable and writable by a compromised
/// engine unless the directory has its own DACL. This one is <i>protected</i> (nothing is inherited from LocalState) and names no AppContainer SID and
/// no capability, so the engine's token matches no entry. Measured on real installs (x64 and ARM64): it cannot read a file in this directory and cannot
/// rename the directory (probably because opening it needs attribute-read access that only an entry could grant). See docs/spikes/package-sandbox.md.
/// </para>
/// </summary>
public static class PrivateDirectory
{
    private const InheritanceFlags Inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>Creates the directory if needed and makes its DACL exactly: protected, current user and SYSTEM with full control. Idempotent; repairs a widened DACL.</summary>
    public static void Ensure(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Directory.CreateDirectory(path);
        var info = new DirectoryInfo(path);
        if (IsExactlyUserAndSystem(info))
        {
            return;
        }

        using var user = WindowsIdentity.GetCurrent();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, Inherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    /// <summary>True when the DACL is protected and has no entry but full control for the current user and for SYSTEM.</summary>
    public static bool IsExactlyUserAndSystem(DirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        using var user = WindowsIdentity.GetCurrent();
        var security = directory.GetAccessControl();
        if (!security.AreAccessRulesProtected)
        {
            return false;
        }

        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        return rules.Count == 2
            && rules.All(r => r.AccessControlType == AccessControlType.Allow && !r.IsInherited && r.FileSystemRights.HasFlag(FileSystemRights.FullControl))
            && rules.Any(r => r.IdentityReference.Equals(user.User))
            && rules.Any(r => r.IdentityReference.Equals(system));
    }
}
