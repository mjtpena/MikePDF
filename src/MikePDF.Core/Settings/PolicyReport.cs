using System.Globalization;
using System.Text;

namespace MikePDF.Core.Settings;

/// <summary>One line of the "Managed by your organisation" report: a value found in the registry, in words.</summary>
public sealed record PolicyReportLine(string PolicyName, string FriendlyName, string Received, string Effect, string Source, bool InEffect)
{
    /// <summary>The line as one sentence for the screen and the clipboard.</summary>
    public string Text(Func<string, string> text) =>
        string.Format(
            CultureInfo.CurrentCulture,
            text("About_Policy_Line"),
            FriendlyName,
            string.Format(CultureInfo.CurrentCulture, text("About_Policy_Received"), Received),
            string.Format(CultureInfo.CurrentCulture, text("About_Policy_EffectSource"), Effect, Source));
}

/// <summary>
/// What About and Settings show about policy (docs/features/settings-policies.md 3.6): for each value present its friendly name, the value as received, the
/// effect in words and where it came from. Built here, from resource keys, so the output is a unit test and the dialog only displays it.
/// </summary>
public sealed class PolicyReport
{
    private PolicyReport(IReadOnlyList<PolicyReportLine> lines, int inEffect, bool anyPresent)
    {
        Lines = lines;
        InEffectCount = inEffect;
        AnyPresent = anyPresent;
    }

    public IReadOnlyList<PolicyReportLine> Lines { get; }

    /// <summary>How many policies decide something.</summary>
    public int InEffectCount { get; }

    /// <summary>At least one value (valid or not) was found, or the registry could not be read: the managed section is shown.</summary>
    public bool AnyPresent { get; }

    public static PolicyReport Build(PolicySnapshot snapshot, Func<string, string> text)
    {
        var lines = new List<PolicyReportLine>();
        foreach (var entry in snapshot.Entries)
        {
            var definition = entry.Definition;
            foreach (var observation in entry.Observations)
            {
                lines.Add(new PolicyReportLine(
                    definition.Name,
                    text(definition.NameKey),
                    PolicyProvider.Received(observation.Raw),
                    Effect(entry, observation, text),
                    text(observation.Scope == PolicyScope.Machine ? "Policy_Source_Machine" : "Policy_Source_User"),
                    observation.Outcome is ObservationOutcome.InEffect or ObservationOutcome.InvalidClosed));
            }

            if (entry.State == PolicyState.ReadFailed)
            {
                var words = entry.IsConfigured && entry.Value is { } closed ? " " + Words(definition, closed, text) : string.Empty;
                lines.Add(new PolicyReportLine(definition.Name, text(definition.NameKey), "?", text("About_Policy_ReadFailed") + words, text("Policy_Source_Machine"), entry.IsConfigured));
            }
        }

        return new PolicyReport(lines, snapshot.InEffectCount, snapshot.AnyPresent);
    }

    /// <summary>The whole report as plain text for support: the header lines, then one line per value. No paths, no document names, no user name.</summary>
    public string ToPlainText(IEnumerable<string> header, Func<string, string> text)
    {
        var builder = new StringBuilder();
        foreach (var line in header)
        {
            builder.AppendLine(line);
        }

        if (Lines.Count > 0)
        {
            builder.AppendLine().AppendLine(text("About_Managed_Header"));
            foreach (var line in Lines)
            {
                builder.AppendLine(line.Text(text));
            }
        }

        return builder.ToString();
    }

    private static string Effect(PolicyEntry entry, PolicyObservation observation, Func<string, string> text)
    {
        var definition = entry.Definition;
        switch (observation.Outcome)
        {
            case ObservationOutcome.Invalid:
                return text("About_Policy_Invalid");
            case ObservationOutcome.NotStricter:
                return text("About_Policy_UserNotStricter");
            case ObservationOutcome.UserScopeNotAllowed:
                return text("About_Policy_UserScopeNotAllowed");
            case ObservationOutcome.Overridden:
                return text("About_Policy_Overridden");
            case ObservationOutcome.InvalidClosed:
                return text("About_Policy_InvalidClosed") + " " + Words(definition, definition.FailClosedValue ?? 0, text);
        }

        var effect = definition.Kind switch
        {
            PolicyKind.Dword => Words(definition, observation.Raw.Dword, text),
            PolicyKind.Text => string.Format(CultureInfo.CurrentCulture, text(definition.EffectKey), entry.Text ?? string.Empty),
            _ => string.Format(CultureInfo.CurrentCulture, text(definition.EffectKey), string.Join(", ", entry.Lines ?? [])),
        };
        return definition.Status switch
        {
            PolicyStatus.Reserved => effect + ". " + text("About_Policy_Reserved"),
            PolicyStatus.Ignored => text("About_Policy_Ignored"),
            _ => effect,
        };
    }

    // A policy with a short list of values has words for each; one with a range says its effect with the number in it.
    private static string Words(PolicyDefinition definition, int value, Func<string, string> text) =>
        definition.Allowed is not null ? text(definition.ValueKey(value)) : string.Format(CultureInfo.CurrentCulture, text(definition.EffectKey), value);
}
