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
