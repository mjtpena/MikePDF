using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace MikePDF.Core.Settings;

public enum PolicyScope
{
    /// <summary>HKEY_LOCAL_MACHINE: set by an administrator, can't be changed by a standard user.</summary>
    Machine,

    /// <summary>HKEY_CURRENT_USER: a user-level policy (Intune user scope, Group Policy user settings). Stricter-only.</summary>
    User,
}

public enum RawPolicyKind
{
    Dword,
    Text,
    TextList,

    /// <summary>Any other registry type (binary, expandable string, qword, none).</summary>
    Other,
}

/// <summary>A policy value as the registry has it, before it is checked against the policy's rules. Capped: a value that is too long is flagged and its data never read.</summary>
public sealed record RawPolicyValue(string Name, RawPolicyKind Kind, int Dword = 0, string? Text = null, IReadOnlyList<string>? Lines = null, bool TooLarge = false);

/// <summary>What one hive holds. <see cref="Failed"/> means the registry could not be read at all (not the same as an empty or missing key).</summary>
public sealed record PolicyRead(IReadOnlyList<RawPolicyValue> Values, bool Failed)
{
    public static PolicyRead Empty { get; } = new([], false);
}

/// <summary>Where policy values come from. The real one reads the registry; tests supply their own.</summary>
public interface IPolicySource
{
    PolicyRead Read(PolicyScope scope, IReadOnlyCollection<string> names);
}

/// <summary>A source with fixed contents, for tests and for running without a registry.</summary>
public sealed class FixedPolicySource : IPolicySource
{
    public Dictionary<PolicyScope, List<RawPolicyValue>> Values { get; } = new() { [PolicyScope.Machine] = [], [PolicyScope.User] = [] };

    public HashSet<PolicyScope> Failing { get; } = [];

    public PolicyRead Read(PolicyScope scope, IReadOnlyCollection<string> names) =>
        Failing.Contains(scope) ? new PolicyRead([], true) : new PolicyRead([.. Values[scope].Where(v => names.Contains(v.Name, StringComparer.OrdinalIgnoreCase))], false);

    public void Set(PolicyScope scope, RawPolicyValue value)
    {
        Values[scope].RemoveAll(v => string.Equals(v.Name, value.Name, StringComparison.OrdinalIgnoreCase));
        Values[scope].Add(value);
    }

    public void Clear(PolicyScope scope) => Values[scope].Clear();
}

/// <summary>
/// Reads <c>Software\Policies\MikePDF</c> from a machine hive and a user hive. Only the names the catalogue knows are asked for (the registry compares names
/// without regard to case), and a value is read only if its size is within the cap for its type, so a hostile 10 MB string or 100,000-entry list costs a size
/// query and nothing more. Never writes. The roots are parameters so tests read a throwaway key under HKCU and never touch HKLM.
/// </summary>
public sealed class RegistryPolicySource : IPolicySource
{
    public const int MaxStringBytes = 8 * 1024;
    public const int MaxMultiStringBytes = 64 * 1024;

    private readonly RegistryKey _machineHive;
    private readonly string _machinePath;
    private readonly RegistryKey _userHive;
    private readonly string _userPath;

    public RegistryPolicySource(RegistryKey machineHive, string machinePath, RegistryKey userHive, string userPath)
    {
        _machineHive = machineHive;
        _machinePath = machinePath;
        _userHive = userHive;
        _userPath = userPath;
    }

    /// <summary>The real thing: HKLM and HKCU, <c>Software\Policies\MikePDF</c>.</summary>
    public static RegistryPolicySource ForSystem() => new(Registry.LocalMachine, PolicyCatalog.RegistryPath, Registry.CurrentUser, PolicyCatalog.RegistryPath);

    public PolicyRead Read(PolicyScope scope, IReadOnlyCollection<string> names)
    {
        try
        {
            using var key = (scope == PolicyScope.Machine ? _machineHive : _userHive).OpenSubKey(scope == PolicyScope.Machine ? _machinePath : _userPath, writable: false);
            if (key is null)
            {
                return PolicyRead.Empty;
            }

            var values = new List<RawPolicyValue>();
            foreach (var name in names)
            {
                if (ReadValue(key, name) is { } value)
                {
                    values.Add(value);
                }
            }

            return new PolicyRead(values, false);
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException or ObjectDisposedException)
        {
            return new PolicyRead([], true);
        }
    }

    private static RawPolicyValue? ReadValue(RegistryKey key, string name)
    {
        uint size = 0;
        var handle = key.Handle.DangerousGetHandle();
        var status = RegQueryValueExW(handle, name, IntPtr.Zero, out var type, null, ref size);
        if (status == 2)
        {
            return null; // ERROR_FILE_NOT_FOUND: the value is not there
        }

        if (status != 0 && status != 234)
        {
            throw new IOException("RegQueryValueEx failed with " + status.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var cap = type switch { 1 => MaxStringBytes, 7 => MaxMultiStringBytes, _ => 8 };
        if (size > cap)
        {
            return new RawPolicyValue(name, PolicyValueParser.KindOf(type), TooLarge: true);
        }

        var data = new byte[size];
        status = RegQueryValueExW(handle, name, IntPtr.Zero, out type, data, ref size);
        if (status != 0)
        {
            // The value changed between the two calls, or went away: treat it as absent this round; the next refresh sees it.
            return null;
        }

        return PolicyValueParser.Parse(name, type, data.AsSpan(0, (int)size));
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegQueryValueExW(IntPtr key, string name, IntPtr reserved, out uint type, byte[]? data, ref uint size);
}

/// <summary>Reads the bytes of a registry value of a given type into a <see cref="RawPolicyValue"/>. Pure, so the "fake registry" tests can feed it raw bytes.</summary>
public static class PolicyValueParser
{
    public const int MaxEntriesRead = 64;

    public static RawPolicyKind KindOf(uint type) => type switch
    {
        4 => RawPolicyKind.Dword,
        1 => RawPolicyKind.Text,
        7 => RawPolicyKind.TextList,
        _ => RawPolicyKind.Other,
    };

    public static RawPolicyValue Parse(string name, uint type, ReadOnlySpan<byte> data)
    {
        switch (type)
        {
            case 4 when data.Length == 4:
                return new RawPolicyValue(name, RawPolicyKind.Dword, BitConverter.ToInt32(data));
            case 1:
                return new RawPolicyValue(name, RawPolicyKind.Text, Text: Decode(data).TrimEnd('\0'));
            case 7:
                var lines = new List<string>();
                var all = Decode(data);
                var start = 0;
                for (var i = 0; i < all.Length && lines.Count <= MaxEntriesRead; i++)
                {
                    if (all[i] == '\0')
                    {
                        if (i == start)
                        {
                            break; // the empty string that ends the list
                        }

                        lines.Add(all[start..i]);
                        start = i + 1;
                    }
                }

                if (start < all.Length && lines.Count <= MaxEntriesRead && all[start..].TrimEnd('\0') is { Length: > 0 } last)
                {
                    lines.Add(last);
                }

                return new RawPolicyValue(name, RawPolicyKind.TextList, Lines: lines, TooLarge: lines.Count > MaxEntriesRead);
            default:
                return new RawPolicyValue(name, RawPolicyKind.Other);
        }
    }

    private static string Decode(ReadOnlySpan<byte> data) => Encoding.Unicode.GetString(data[..(data.Length & ~1)]);
}
