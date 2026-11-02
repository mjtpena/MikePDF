using System.Globalization;
using System.Text;

namespace MikePDF.Core.Settings;

public enum PolicyState
{
    /// <summary>No policy value is in effect: the setting follows the person.</summary>
    NotConfigured,

    /// <summary>A valid value is in effect, or an invalid one gave its restrictive result (a fail-closed policy).</summary>
    /// <remarks>A hive that could not be read is <see cref="ReadFailed"/>, not this.</remarks>
    InEffect,

    /// <summary>The registry could not be read. A fail-closed policy gives its restrictive result; the others are not configured.</summary>
    ReadFailed,
}

/// <summary>What became of one value found in the registry.</summary>
public enum ObservationOutcome
{
    InEffect,

    /// <summary>Not a valid value for this policy; ignored.</summary>
    Invalid,

    /// <summary>Not a valid value for a fail-closed policy; the most restrictive result applies instead.</summary>
    InvalidClosed,

    /// <summary>A user-level value that is looser than the machine value or the default; ignored.</summary>
    NotStricter,

    /// <summary>This policy does not take user-level values.</summary>
    UserScopeNotAllowed,

    /// <summary>A machine value that a stricter user-level value replaced.</summary>
    Overridden,
}

public sealed record PolicyObservation(PolicyScope Scope, RawPolicyValue Raw, ObservationOutcome Outcome);

/// <summary>A policy after resolution: what is in effect, where from, and every value that was found and what happened to it.</summary>
public sealed class PolicyEntry
{
    public required PolicyDefinition Definition { get; init; }

    public PolicyState State { get; init; }

    /// <summary>The scope whose value is in effect, if any.</summary>
    public PolicyScope? Scope { get; init; }

    /// <summary>For a DWORD policy: the value in effect, or its absent meaning when not configured (null when absence means "not configured" too).</summary>
    public int? Value { get; init; }

    public string? Text { get; init; }

    public IReadOnlyList<string>? Lines { get; init; }

    public IReadOnlyList<PolicyObservation> Observations { get; init; } = [];

    /// <summary>True when the policy decides: a setting it controls is locked.</summary>
    public bool IsConfigured => State == PolicyState.InEffect || (State == PolicyState.ReadFailed && Definition.FailClosed);
}

/// <summary>The resolved policies at one moment. Immutable.</summary>
public sealed class PolicySnapshot
{
    public static PolicySnapshot Unconfigured { get; } = new(PolicyCatalog.All.ToDictionary(
        d => d.Name,
        d => new PolicyEntry { Definition = d, State = PolicyState.NotConfigured, Value = d.AbsentValue },
        StringComparer.OrdinalIgnoreCase));

    private readonly Dictionary<string, PolicyEntry> _entries;

    public PolicySnapshot(Dictionary<string, PolicyEntry> entries) => _entries = entries;

    public IEnumerable<PolicyEntry> Entries => PolicyCatalog.All.Select(d => _entries[d.Name]);

    public PolicyEntry this[string name] => _entries[name];

    /// <summary>Any value was found, valid or not (the "Managed by your organisation" section appears).</summary>
    public bool AnyPresent => _entries.Values.Any(e => e.Observations.Count > 0 || e.State == PolicyState.ReadFailed);

    /// <summary>How many policies decide something (the number About shows).</summary>
    public int InEffectCount => _entries.Values.Count(e => e.IsConfigured);

    /// <summary>The setting a policy locks, if one does: the policy entry and the value for the setting.</summary>
    public (PolicyEntry Entry, object Value)? LockFor(string settingKey)
    {
        foreach (var entry in _entries.Values)
        {
            var definition = entry.Definition;
            if (definition.SettingKey == settingKey && entry.IsConfigured)
            {
                object? value = definition.Kind switch
                {
                    PolicyKind.Dword when entry.Value is { } v => definition.ToSetting?.Invoke(v),
                    PolicyKind.Text => entry.Text,
                    _ => null,
                };
                if (value is not null)
                {
                    return (entry, value);
                }
            }
        }

        return null;
    }

    public bool IsOn(string name) => this[name].Value is not 0;

    public bool ImportExportAllowed => IsOn("SettingsImportExportEnabled");

    public bool PrintingAllowed => IsOn("PrintingEnabled");

    /// <summary>The Job Object memory limit for each engine, in megabytes.</summary>
    public int EngineMemoryLimitMb => this["EngineMemoryLimitMB"].Value ?? 2048;

    /// <summary>A string that changes when anything a person could see changes, so a refresh can tell whether to raise an event.</summary>
    public string Signature()
    {
        var text = new StringBuilder();
        foreach (var entry in Entries)
        {
            text.Append(entry.Definition.Name).Append('|').Append((int)entry.State).Append('|').Append(entry.Scope).Append('|').Append(entry.Value)
                .Append('|').Append(entry.Text).Append('|').Append(entry.Lines is null ? null : string.Join('\u0001', entry.Lines));
            foreach (var o in entry.Observations)
            {
                text.Append('|').Append(o.Scope).Append(o.Outcome).Append(o.Raw.Kind).Append(o.Raw.Dword).Append(o.Raw.Text).Append(o.Raw.TooLarge);
            }

            text.Append('\n');
        }

        return text.ToString();
    }
}

/// <summary>
/// Reads the policies, resolves them (machine over user, user only stricter), and tells whoever listens when something changed. It never writes. Call
/// <see cref="Refresh"/> on a timer: the window does so every two seconds, which keeps a registry change inside the five seconds the spec allows. Reading
/// is cheap (a size query per known name), so polling does not need a registry notification, which would not work for a key that does not exist yet.
/// </summary>
public sealed class PolicyProvider
{
    private readonly IPolicySource _source;
    private readonly IReadOnlyCollection<string> _names = [.. PolicyCatalog.All.Select(p => p.Name)];
    private PolicySnapshot _current = PolicySnapshot.Unconfigured;
    private string _signature = string.Empty;

    public PolicyProvider(IPolicySource source)
    {
        _source = source;
        Refresh();
    }

    /// <summary>A provider with no policies, for tests and tools.</summary>
    public static PolicyProvider None() => new(new FixedPolicySource());

    public PolicySnapshot Current => Volatile.Read(ref _current);

    /// <summary>Raised on the thread that called <see cref="Refresh"/>, after <see cref="Current"/> changed.</summary>
    public event Action? Changed;

    /// <summary>Reads the registry again. Returns true if the resolved policies changed.</summary>
    public bool Refresh()
    {
        var machine = Read(PolicyScope.Machine);
        var user = Read(PolicyScope.User);
        var snapshot = Resolve(machine, user);
        var signature = snapshot.Signature();
        if (signature == _signature)
        {
            return false;
        }

        _signature = signature;
        Volatile.Write(ref _current, snapshot);
        Changed?.Invoke();
        return true;
    }

    private PolicyRead Read(PolicyScope scope)
    {
        try
        {
            return _source.Read(scope, _names);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new PolicyRead([], true);
        }
    }

    private static PolicySnapshot Resolve(PolicyRead machine, PolicyRead user)
    {
        var entries = new Dictionary<string, PolicyEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in PolicyCatalog.All)
        {
            entries[definition.Name] = ResolveOne(
                definition,
                machine.Values.FirstOrDefault(v => string.Equals(v.Name, definition.Name, StringComparison.OrdinalIgnoreCase)),
                user.Values.FirstOrDefault(v => string.Equals(v.Name, definition.Name, StringComparison.OrdinalIgnoreCase)),
                machine.Failed);
        }

        return new PolicySnapshot(entries);
    }

    private sealed record Checked(bool Present, bool Valid, int Dword, string? Text, IReadOnlyList<string>? Lines);

    private static Checked Check(PolicyDefinition definition, RawPolicyValue? raw)
    {
        if (raw is null)
        {
            return new Checked(false, false, 0, null, null);
        }

        var invalid = new Checked(true, false, 0, null, null);
        if (raw.TooLarge)
        {
            return invalid;
        }

        switch (definition.Kind)
        {
            case PolicyKind.Dword:
                return raw.Kind == RawPolicyKind.Dword && definition.IsValidDword(raw.Dword) ? new Checked(true, true, raw.Dword, null, null) : invalid;
            case PolicyKind.Text:
                var text = raw.Text?.Trim();
                return raw.Kind == RawPolicyKind.Text && text is { Length: > 0 } && text.Length <= definition.MaxLength ? new Checked(true, true, 0, text, null) : invalid;
            default:
                if (raw.Kind != RawPolicyKind.TextList || raw.Lines is null || raw.Lines.Count > definition.MaxEntries)
                {
                    return invalid;
                }

                var lines = new List<string>();
                foreach (var line in raw.Lines.Select(l => l.Trim()))
                {
                    if (line.Length == 0 || line.Length > definition.MaxLength)
                    {
                        return invalid;
                    }

                    var entry = definition.Name == "AllowedLinkSchemes" ? line.ToLowerInvariant() : line;
                    if (definition.EntryValid is { } valid && !valid(entry))
                    {
                        return invalid;
                    }

                    if (!(definition.Name == "AllowedLinkSchemes" && entry == "file"))
                    {
                        lines.Add(entry);
                    }
                }

                return new Checked(true, true, 0, null, lines);
        }
    }

    private static PolicyEntry ResolveOne(PolicyDefinition definition, RawPolicyValue? machineRaw, RawPolicyValue? userRaw, bool machineFailed)
    {
        var m = Check(definition, machineRaw);
        var u = Check(definition, userRaw);
        var observations = new List<PolicyObservation>();
        PolicyScope? scope = null;
        Checked? chosen = null;
        var state = PolicyState.NotConfigured;
        int? closed = null;

        if (machineFailed && definition.FailClosed)
        {
            // The machine hive could not be read: the restrictive result, so that an unreadable policy never loosens anything.
            state = PolicyState.ReadFailed;
            closed = definition.FailClosedValue;
            scope = PolicyScope.Machine;
        }
        else if (machineFailed)
        {
            state = PolicyState.ReadFailed;
        }

        var machineIndex = -1;
        if (m.Present)
        {
            if (m.Valid)
            {
                chosen = m;
                scope = PolicyScope.Machine;
                state = PolicyState.InEffect;
                closed = null;
                observations.Add(new PolicyObservation(PolicyScope.Machine, machineRaw!, ObservationOutcome.InEffect));
            }
            else if (definition.FailClosed)
            {
                closed = definition.FailClosedValue;
                scope = PolicyScope.Machine;
                state = PolicyState.InEffect;
                observations.Add(new PolicyObservation(PolicyScope.Machine, machineRaw!, ObservationOutcome.InvalidClosed));
            }
            else
            {
                observations.Add(new PolicyObservation(PolicyScope.Machine, machineRaw!, ObservationOutcome.Invalid));
            }

            machineIndex = observations.Count - 1;
        }

        if (u.Present)
        {
            if (definition.UserScope == PolicyUserScope.None)
            {
                observations.Add(new PolicyObservation(PolicyScope.User, userRaw!, ObservationOutcome.UserScopeNotAllowed));
            }
            else if (!u.Valid)
            {
                if (definition.FailClosed && scope is null)
                {
                    closed = definition.FailClosedValue;
                    scope = PolicyScope.User;
                    state = PolicyState.InEffect;
                    observations.Add(new PolicyObservation(PolicyScope.User, userRaw!, ObservationOutcome.InvalidClosed));
                }
                else
                {
                    observations.Add(new PolicyObservation(PolicyScope.User, userRaw!, ObservationOutcome.Invalid));
                }
            }
            else
            {
                // A valid user-level DWORD counts only if it is at least as strict as what is in effect, or as the default when nothing is.
                var inEffect = chosen?.Dword ?? closed ?? definition.AbsentValue;
                var baseline = inEffect ?? (definition.StricterIsLower ? definition.Allowed?.Max() ?? definition.Max : definition.Allowed?.Min() ?? definition.Min);
                if (definition.IsAtLeastAsStrict(u.Dword, baseline))
                {
                    if (scope is null || (inEffect is { } now && u.Dword != now))
                    {
                        if (machineIndex >= 0 && observations[machineIndex].Outcome == ObservationOutcome.InEffect)
                        {
                            observations[machineIndex] = observations[machineIndex] with { Outcome = ObservationOutcome.Overridden };
                        }

                        chosen = u;
                        closed = null;
                        scope = PolicyScope.User;
                        state = PolicyState.InEffect;
                    }

                    observations.Add(new PolicyObservation(PolicyScope.User, userRaw!, ObservationOutcome.InEffect));
                }
                else
                {
                    observations.Add(new PolicyObservation(PolicyScope.User, userRaw!, ObservationOutcome.NotStricter));
                }
            }
        }

        var value = definition.Kind == PolicyKind.Dword ? closed ?? (chosen is not null ? chosen.Dword : definition.AbsentValue) : (int?)null;
        return new PolicyEntry
        {
            Definition = definition,
            State = state,
            Scope = scope,
            Value = value,
            Text = chosen?.Text,
            Lines = chosen?.Lines,
            Observations = observations,
        };
    }

    /// <summary>The value as the person who set it wrote it, short enough to print.</summary>
    public static string Received(RawPolicyValue raw) => raw switch
    {
        { TooLarge: true } => "(too large)",
        { Kind: RawPolicyKind.Dword } => raw.Dword.ToString(CultureInfo.InvariantCulture),
        { Kind: RawPolicyKind.Text } => Shorten(raw.Text ?? string.Empty),
        { Kind: RawPolicyKind.TextList } => Shorten(string.Join("; ", raw.Lines ?? [])),
        _ => "(not a number or text)",
    };

    private static string Shorten(string text) => text.Length <= 120 ? text : text[..120] + "...";
}
