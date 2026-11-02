using System.Text.RegularExpressions;

namespace MikePDF.Core.Settings;

public enum PolicyKind
{
    Dword,
    Text,
    TextList,
}

/// <summary>Whether a policy in the user's own hive (HKCU) counts.</summary>
public enum PolicyUserScope
{
    /// <summary>Only the machine hive counts; a user-level value is ignored and reported.</summary>
    None,

    /// <summary>A user-level value counts only if it is at least as strict as the machine value (or as the default when there is none): a person can tighten their own copy, never loosen the organisation's.</summary>
    StricterOnly,
}

/// <summary>What a policy does in this build.</summary>
public enum PolicyStatus
{
    Active,

    /// <summary>Read and shown now, used by a feature that ships later (AI).</summary>
    Reserved,

    /// <summary>Read and shown, but the build has nothing to apply it to (JavaScript, telemetry).</summary>
    NotInBuild,

    /// <summary>Read only so that About can say it is ignored (a weaker sandbox, an update channel).</summary>
    Ignored,
}

/// <summary>One enterprise policy value under <c>Software\Policies\MikePDF</c> (docs/features/settings-policies.md 3.4).</summary>
public sealed class PolicyDefinition
{
    public required string Name { get; init; }

    public required PolicyKind Kind { get; init; }

    /// <summary>The DWORD values that are allowed (when not a range).</summary>
    public IReadOnlyList<int>? Allowed { get; init; }

    public int Min { get; init; }

    public int Max { get; init; } = int.MaxValue;

    /// <summary>What the app does when the policy is absent (DWORD policies), or null when absence means "not configured".</summary>
    public int? AbsentValue { get; init; }

    /// <summary>The restrictive value an invalid value gives, for the policies marked "fail closed". Null means an invalid value is ignored.</summary>
    public int? FailClosedValue { get; init; }

    /// <summary>For a DWORD policy: a smaller number is the stricter one (0 is off, a low limit). False for the few where a larger one is (SaveMode 1).</summary>
    public bool StricterIsLower { get; init; } = true;

    public PolicyUserScope UserScope { get; init; } = PolicyUserScope.None;

    public PolicyStatus Status { get; init; } = PolicyStatus.Active;

    /// <summary>The setting this policy locks, if any (<see cref="SettingsRegistry"/>).</summary>
    public string? SettingKey { get; init; }

    /// <summary>Converts the policy's DWORD to the value of the setting it locks, or null when this value does not lock it (a switch that is "allowed" leaves the person's choice alone).</summary>
    public Func<int, object?>? ToSetting { get; init; }

    public int MaxLength { get; init; } = 128;

    public int MaxEntries { get; init; } = 16;

    public Func<string, bool>? EntryValid { get; init; }

    public bool FailClosed => FailClosedValue is not null;

    public string NameKey => "Policy_" + Name + "_Name";

    /// <summary>The words for a DWORD value ("Policy_{Name}_Value_{n}"), and for the others the effect with the value in {0} ("Policy_{Name}_Effect").</summary>
    public string ValueKey(int value) => "Policy_" + Name + "_Value_" + value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string EffectKey => "Policy_" + Name + "_Effect";

    public bool IsValidDword(int value) => Allowed is { } list ? list.Contains(value) : value >= Min && value <= Max;

    /// <summary>True when <paramref name="candidate"/> is as strict as, or stricter than, <paramref name="baseline"/>.</summary>
    public bool IsAtLeastAsStrict(int candidate, int baseline) => StricterIsLower ? candidate <= baseline : candidate >= baseline;
}

/// <summary>
/// The policies the app knows. The registry layout is the contract the ADMX templates (P2) will use, so a name here is never renamed. Every name has a row
/// in docs/admin/policies.md (a test checks the two lists are the same).
/// </summary>
public static class PolicyCatalog
{
    public const string RegistryPath = @"Software\Policies\MikePDF";

    public static IReadOnlyList<PolicyDefinition> All { get; } = Build();

    public static PolicyDefinition? Find(string name) => All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static readonly Regex SchemePattern = new("^[a-z][a-z0-9+.-]{0,31}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static PolicyDefinition Flag(string name, int absent, int? failClosed, PolicyStatus status = PolicyStatus.Active, string? setting = null, PolicyUserScope scope = PolicyUserScope.StricterOnly, Func<int, object?>? toSetting = null) => new()
    {
        Name = name,
        Kind = PolicyKind.Dword,
        Allowed = [0, 1],
        AbsentValue = absent,
        FailClosedValue = failClosed,
        Status = status,
        SettingKey = setting,
        ToSetting = toSetting,
        UserScope = scope,
    };

    private static List<PolicyDefinition> Build() =>
    [
        Flag("JavaScriptEnabled", absent: 0, failClosed: 0, PolicyStatus.NotInBuild),
        new()
        {
            Name = "ExternalLinks",
            Kind = PolicyKind.Dword,
            Allowed = [0, 1, 2],
            AbsentValue = 1,
            FailClosedValue = 0,
            UserScope = PolicyUserScope.StricterOnly,
            SettingKey = "links.behaviour",
            ToSetting = v => v switch { 0 => "Block", 2 => "Confirm", _ => "ShowAddress" },
        },
        new()
        {
            Name = "AllowedLinkSchemes",
            Kind = PolicyKind.TextList,
            EntryValid = s => SchemePattern.IsMatch(s),
            MaxLength = 32,
        },
        new()
        {
            Name = "AiMode",
            Kind = PolicyKind.Dword,
            Allowed = [0, 1, 2],
            FailClosedValue = 0,
            UserScope = PolicyUserScope.StricterOnly,
            Status = PolicyStatus.Reserved,
        },
        new()
        {
            Name = "AiAllowedEndpoints",
            Kind = PolicyKind.TextList,
            Status = PolicyStatus.Reserved,
            MaxLength = 1024,
            EntryValid = s => Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps,
        },
        Flag("TelemetryEnabled", absent: 0, failClosed: 0, PolicyStatus.NotInBuild),
        new()
        {
            Name = "SaveMode",
            Kind = PolicyKind.Dword,
            Allowed = [0, 1],
            AbsentValue = 0,
            StricterIsLower = false,
            UserScope = PolicyUserScope.StricterOnly,
            SettingKey = "save.mode",
            ToSetting = v => v == 1 ? "AlwaysSaveAsCopy" : "InPlace",
        },
        new()
        {
            Name = "EngineMemoryLimitMB",
            Kind = PolicyKind.Dword,
            Min = 256,
            Max = 8192,
            AbsentValue = 2048,
            UserScope = PolicyUserScope.StricterOnly,
        },
        new()
        {
            Name = "MaxLiveTabs",
            Kind = PolicyKind.Dword,
            Min = 1,
            Max = 32,
            AbsentValue = 6,
            UserScope = PolicyUserScope.StricterOnly,
            SettingKey = "engines.maxLiveTabs",
            ToSetting = v => v,
        },
        Flag("PrintingEnabled", absent: 1, failClosed: null),
        // 0 switches the handler off and locks the setting off; 1 only says "allowed", so the person's own choice stands (docs/features/shell-extensions.md 3.5).
        Flag("ShellThumbnailsEnabled", absent: 1, failClosed: 0, setting: "shell.thumbnails", toSetting: v => v == 0 ? false : null),
        Flag("ShellPreviewEnabled", absent: 1, failClosed: 0, setting: "shell.preview", toSetting: v => v == 0 ? false : null),
        new()
        {
            Name = "ShellMaxEngines",
            Kind = PolicyKind.Dword,
            Min = 1,
            Max = 6,
            AbsentValue = 3,
            UserScope = PolicyUserScope.StricterOnly,
            SettingKey = "shell.maxEngines",
            ToSetting = v => v,
        },
        Flag("DefaultAppPromptEnabled", absent: 1, failClosed: 0, scope: PolicyUserScope.None),
        Flag("SettingsImportExportEnabled", absent: 1, failClosed: 0),
        new()
        {
            Name = "AnnotationAuthorName",
            Kind = PolicyKind.Text,
            SettingKey = "markup.authorName",
        },
        new()
        {
            Name = "AllowWeakerSandbox",
            Kind = PolicyKind.Dword,
            Allowed = [0],
            AbsentValue = 0,
            Status = PolicyStatus.Ignored,
        },
        new()
        {
            Name = "UpdateChannel",
            Kind = PolicyKind.Dword,
            Allowed = [0],
            Status = PolicyStatus.Ignored,
        },
    ];
}
