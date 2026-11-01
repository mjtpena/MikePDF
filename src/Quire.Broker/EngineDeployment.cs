using System.Security.AccessControl;
using System.Security.Principal;

namespace Quire.Broker;

/// <summary>
/// File-system access for the sandbox. An AppContainer can read nothing it was not granted, and it must be able to read and
/// execute the engine's own files.
/// <para>
/// <b>Unpackaged development and test runs only.</b> A packaged build gets this for free from the package directory ACLs
/// (the AppContainer of the package family can read its own package); calling this on an installed package directory would
/// be both unnecessary and a change to a location Quire does not own.
/// </para>
/// </summary>
public static class EngineDeployment
{
    private const FileSystemRights Rights = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;

    /// <summary>Grants the AppContainer read and execute on <paramref name="directory"/> and everything below it (ADR-0004 decision 2: that directory only).</summary>
    public static void EnsureAppContainerAccess(string directory, AppContainerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        EnsureAppContainerAccess(directory, profile.Sid);
    }

    public static void EnsureAppContainerAccess(string directory, SecurityIdentifier appContainerSid)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(appContainerSid);
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(appContainerSid, Rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    /// <summary>
    /// Makes <paramref name="directory"/> a read-only deployment for the AppContainer: inheritance from the parent is cut and the DACL is
    /// the current user, SYSTEM and Administrators (full control) plus <paramref name="appContainerSid"/> (read and execute only). Call it on
    /// an empty directory, before the files are copied in, so that they inherit exactly this.
    /// <para>
    /// Needed when the broker is packaged: the sandbox child is then a child container of the package and carries the package's own
    /// capability, which the package's data folders (LocalState) grant full control. A directory below LocalState that kept the inherited
    /// ACEs would be writable by the engine.
    /// </para>
    /// </summary>
    public static void EnsureReadOnlyDeployment(string directory, SecurityIdentifier appContainerSid)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(appContainerSid);
        var info = new DirectoryInfo(directory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        using var user = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(appContainerSid, Rights, inherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    /// <summary>Removes what <see cref="EnsureAppContainerAccess(string, SecurityIdentifier)"/> granted. Idempotent.</summary>
    public static void RevokeAppContainerAccess(string directory, SecurityIdentifier appContainerSid)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(appContainerSid);
        var info = new DirectoryInfo(directory);
        if (!info.Exists)
        {
            return;
        }

        var security = info.GetAccessControl();
        security.RemoveAccessRuleAll(new FileSystemAccessRule(appContainerSid, Rights, AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}
