using System.Security.Principal;
using System.Text.RegularExpressions;
using MikePDF.Broker.Native;
using Windows.Win32;
using Windows.Win32.Security;

namespace MikePDF.Broker;

/// <summary>
/// An AppContainer profile and its SID (ADR-0004). The SID is derived from the profile name, so the same name always
/// gives the same SID, which is also what an ACL granted in an earlier run refers to.
/// </summary>
public sealed partial class AppContainerProfile
{
    private AppContainerProfile(string name, SecurityIdentifier sid, bool createdByThisCall)
    {
        Name = name;
        Sid = sid;
        CreatedByThisCall = createdByThisCall;
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        SidBytes = bytes;
    }

    public string Name { get; }

    public SecurityIdentifier Sid { get; }

    /// <summary>False when the profile already existed (a leftover from an earlier run, or another process using the same name).</summary>
    public bool CreatedByThisCall { get; }

    internal byte[] SidBytes { get; }

    /// <summary>Creates the profile, or derives its SID if it already exists. Throws if neither works: there is no fallback to a weaker sandbox.</summary>
    public static unsafe AppContainerProfile CreateOrOpen(string name, string displayName = "MikePDF engine", string description = "MikePDF sandboxed engine (zero capabilities)")
    {
        ValidateName(name);
        PSID sid;
        var created = true;
        var hr = (int)PInvoke.CreateAppContainerProfile(name, displayName, description, default, out sid);
        if (hr == Win32.HResultAlreadyExists)
        {
            created = false;
            hr = (int)PInvoke.DeriveAppContainerSidFromAppContainerName(name, out sid);
            Win32.ThrowIfFailed(hr, "DeriveAppContainerSidFromAppContainerName");
        }
        else
        {
            Win32.ThrowIfFailed(hr, "CreateAppContainerProfile");
        }

        try
        {
            return new AppContainerProfile(name, new SecurityIdentifier((nint)sid.Value), created);
        }
        finally
        {
            PInvoke.FreeSid(sid);
        }
    }

    /// <summary>Deletes the profile (and its folder under %LOCALAPPDATA%\Packages). A profile that does not exist is not an error.</summary>
    public static void Delete(string name)
    {
        ValidateName(name);
        var hr = (int)PInvoke.DeleteAppContainerProfile(name);
        if (hr < 0 && hr != unchecked((int)0x80070002)) // HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND)
        {
            Win32.ThrowIfFailed(hr, "DeleteAppContainerProfile");
        }
    }

    public void Delete() => Delete(Name);

    private static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!NamePattern().IsMatch(name))
        {
            throw new ArgumentException("An AppContainer name is 2 to 64 characters of letters, digits, '.', '-' or '_'.", nameof(name));
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{2,64}$")]
    private static partial Regex NamePattern();
}
