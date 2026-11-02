using System.Collections.Frozen;

namespace MikePDF.Core.Commands;

/// <summary>A user's choice of shortcuts for one command, as stored: an empty list means unbound.</summary>
public sealed record KeyBinding(string Command, IReadOnlyList<string> Chords);

public enum BindingProblemKind
{
    UnknownCommand,
    InvalidChord,
    ReservedChord,
    TooManyChords,
    PlainKeyOutsideReader,
    Conflict,
    DuplicateCommand,
}

/// <summary>Something wrong with one binding. <see cref="Other"/> is the command that holds the chord, for a conflict.</summary>
public sealed record BindingProblem(BindingProblemKind Kind, string Command, string Text, string? Other = null);

/// <summary>A default chord that a newer version gave to a command the user never touched, but that the user had already given to another command.</summary>
public sealed record UpdateConflict(string Command, Chord Chord, string ClaimedBy);

/// <summary>The outcome of asking to bind chords to a command.</summary>
public sealed record BindOutcome(bool Applied, IReadOnlyList<BindingProblem> Problems)
{
    public bool HasConflicts => Problems.Any(p => p.Kind == BindingProblemKind.Conflict);
}

/// <summary>
/// The commands and the shortcuts they currently have: defaults from the catalogue, overridden by what the user chose. Only the overrides are stored, so a
/// newer version can change the default of a command the user never touched (docs/features/command-palette-shortcuts.md 3.7). Not thread-safe: the window
/// owns it and uses it from the UI thread.
/// </summary>
public sealed class CommandRegistry
{
    public const int MaxChordsPerCommand = 2;

    private readonly IReadOnlyList<CommandDefinition> _commands;
    private readonly FrozenDictionary<string, CommandDefinition> _byId;
    private Dictionary<string, List<Chord>> _overrides = new(StringComparer.Ordinal);
    private Dictionary<string, IReadOnlyList<Chord>> _effective = new(StringComparer.Ordinal);
    private Dictionary<Chord, List<CommandDefinition>> _byChord = [];
    private List<UpdateConflict> _updateConflicts = [];

    public CommandRegistry()
        : this(CommandCatalog.All)
    {
    }

    public CommandRegistry(IReadOnlyList<CommandDefinition> commands)
    {
        _commands = commands;
        _byId = commands.ToFrozenDictionary(c => c.Id, StringComparer.Ordinal);
        Recompute();
    }

    /// <summary>Raised after the effective shortcuts changed (a remap, a reset, an import, a load).</summary>
    public event Action? BindingsChanged;

    public IReadOnlyList<CommandDefinition> Commands => _commands;

    public CommandDefinition? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>The overrides to store, in command order. Empty when every shortcut is the default.</summary>
    public IReadOnlyList<KeyBinding> Overrides =>
        [.. _commands.Where(c => _overrides.ContainsKey(c.Id)).Select(c => new KeyBinding(c.Id, [.. _overrides[c.Id].Select(x => x.ToString())]))];

    public bool IsCustomised(string id) => _overrides.ContainsKey(id);

    public bool HasCustomShortcuts => _overrides.Count > 0;

    /// <summary>Default chords that were dropped because the user had already chosen them for another command.</summary>
    public IReadOnlyList<UpdateConflict> UpdateConflicts => _updateConflicts;

    /// <summary>The chords a command answers to now (at most two).</summary>
    public IReadOnlyList<Chord> EffectiveChords(string id) => _effective.GetValueOrDefault(id) ?? [];

    /// <summary>The commands that answer to a chord, in catalogue order (usually one; the dispatcher picks by scope).</summary>
    public IReadOnlyList<CommandDefinition> CommandsFor(Chord chord) => _byChord.TryGetValue(chord, out var list) ? list : [];

    public static bool ScopesOverlap(CommandScope a, CommandScope b)
    {
        if (a == CommandScope.Presentation || b == CommandScope.Presentation)
        {
            return a == b;
        }

        return a == b || a == CommandScope.Global || b == CommandScope.Global;
    }

    /// <summary>Other commands that answer to the chord in a scope that overlaps this command's.</summary>
    public IReadOnlyList<(CommandDefinition Command, Chord Chord)> FindConflicts(string id, Chord chord)
    {
        var command = Find(id);
        if (command is null)
        {
            return [];
        }

        return [.. CommandsFor(chord).Where(c => c.Id != id && ScopesOverlap(c.Scope, command.Scope)).Select(c => (c, chord))];
    }

    /// <summary>
    /// Whether a person may bind this chord to this command at all: not reserved, and an unmodified printable key only in the Reader scope. Conflicts are
    /// a separate question (<see cref="FindConflicts"/>), because the person can resolve them.
    /// </summary>
    public BindingProblem? CheckBindable(string id, Chord chord)
    {
        var command = Find(id);
        if (command is null)
        {
            return new BindingProblem(BindingProblemKind.UnknownCommand, id, id);
        }

        if (ReservedChords.IsReserved(chord))
        {
            return new BindingProblem(BindingProblemKind.ReservedChord, id, chord.ToString());
        }

        if (!chord.HasControl && !chord.HasAlt && ChordKeys.IsPrintable(chord.Key) && command.Scope != CommandScope.Reader)
        {
            return new BindingProblem(BindingProblemKind.PlainKeyOutsideReader, id, chord.ToString());
        }

        return null;
    }

    /// <summary>
    /// Gives a command these chords (an empty list unbinds it). With a conflict and <paramref name="reassign"/> false nothing changes and the conflicts are
    /// returned; with it true the chord is taken from the other command, which keeps the rest of its shortcuts.
    /// </summary>
    public BindOutcome SetChords(string id, IReadOnlyList<Chord> chords, bool reassign)
    {
        if (Find(id) is null)
        {
            return new BindOutcome(false, [new BindingProblem(BindingProblemKind.UnknownCommand, id, id)]);
        }

        var distinct = chords.Distinct().ToList();
        var problems = new List<BindingProblem>();
        if (distinct.Count > MaxChordsPerCommand)
        {
            problems.Add(new BindingProblem(BindingProblemKind.TooManyChords, id, distinct.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        foreach (var chord in distinct)
        {
            if (CheckBindable(id, chord) is { } problem)
            {
                problems.Add(problem);
            }
            else
            {
                problems.AddRange(FindConflicts(id, chord).Select(c => new BindingProblem(BindingProblemKind.Conflict, id, chord.ToString(), c.Command.Id)));
            }
        }

        var blocking = problems.Where(p => p.Kind != BindingProblemKind.Conflict).ToList();
        if (blocking.Count > 0 || (problems.Count > 0 && !reassign))
        {
            return new BindOutcome(false, problems);
        }

        foreach (var conflict in problems.Where(p => p.Kind == BindingProblemKind.Conflict))
        {
            var other = conflict.Other!;
            var chord = Chord.FromText(conflict.Text)!.Value;
            SetOverride(other, [.. EffectiveChords(other).Where(c => c != chord)]);
            Recompute(); // the next conflict may be with the same command, which now has fewer chords
        }

        SetOverride(id, distinct);
        Recompute();
        BindingsChanged?.Invoke();
        return new BindOutcome(true, problems);
    }

    /// <summary>Puts one command back to its default shortcuts.</summary>
    public void Reset(string id)
    {
        if (_overrides.Remove(id))
        {
            Recompute();
            BindingsChanged?.Invoke();
        }
    }

    /// <summary>Puts every command back to its default shortcuts.</summary>
    public void ResetAll()
    {
        if (_overrides.Count > 0)
        {
            _overrides = new Dictionary<string, List<Chord>>(StringComparer.Ordinal);
            Recompute();
            BindingsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Replaces the overrides with stored ones. A stored override is the user's choice, so it wins over another command's default (that default is dropped and
    /// listed in <see cref="UpdateConflicts"/>); entries that cannot be read are dropped and returned as problems.
    /// </summary>
    public IReadOnlyList<BindingProblem> LoadOverrides(IEnumerable<KeyBinding> stored)
    {
        var result = ValidateOverrides(stored, strict: false);
        _overrides = new Dictionary<string, List<Chord>>(StringComparer.Ordinal);
        foreach (var (command, chords) in result.Accepted)
        {
            _overrides[command] = chords;
        }

        Recompute();
        BindingsChanged?.Invoke();
        return result.Problems;
    }

    /// <summary>
    /// Checks a list of overrides without applying it. Strict (an import): a chord that another accepted entry already holds, or that a command the file does
    /// not mention has by default, is a conflict and the entry is skipped. Not strict (loading what this app stored): only the first of two overrides for one
    /// chord stands.
    /// </summary>
    public BindingValidation ValidateOverrides(IEnumerable<KeyBinding> entries, bool strict)
    {
        var accepted = new List<(string Command, List<Chord> Chords)>();
        var problems = new List<BindingProblem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var claimed = new List<(CommandDefinition Command, Chord Chord)>();
        var mentioned = new HashSet<string>(StringComparer.Ordinal);
        var list = entries.ToList();
        foreach (var entry in list)
        {
            if (entry.Command is not null)
            {
                mentioned.Add(entry.Command);
            }
        }

        foreach (var entry in list)
        {
            var id = entry.Command ?? string.Empty;
            var command = Find(id);
            if (command is null)
            {
                problems.Add(new BindingProblem(BindingProblemKind.UnknownCommand, id, id));
                continue;
            }

            if (!seen.Add(id))
            {
                problems.Add(new BindingProblem(BindingProblemKind.DuplicateCommand, id, id));
                continue;
            }

            if ((entry.Chords?.Count ?? 0) > MaxChordsPerCommand)
            {
                problems.Add(new BindingProblem(BindingProblemKind.TooManyChords, id, entry.Chords!.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                continue;
            }

            var chords = new List<Chord>();
            foreach (var text in entry.Chords ?? [])
            {
                if (!ChordParser.TryParse(text, out var chord, out _))
                {
                    problems.Add(new BindingProblem(BindingProblemKind.InvalidChord, id, text ?? string.Empty));
                    continue;
                }

                if (CheckBindable(id, chord) is { } bindable)
                {
                    problems.Add(bindable);
                    continue;
                }

                var holder = claimed.FirstOrDefault(c => c.Chord == chord && ScopesOverlap(c.Command.Scope, command.Scope)).Command;
                if (holder is null && strict)
                {
                    holder = _commands.FirstOrDefault(c => c.Id != id && !mentioned.Contains(c.Id) && ScopesOverlap(c.Scope, command.Scope) && c.DefaultChords.Contains(chord));
                }

                if (holder is not null)
                {
                    problems.Add(new BindingProblem(BindingProblemKind.Conflict, id, chord.ToString(), holder.Id));
                    continue;
                }

                if (!chords.Contains(chord))
                {
                    chords.Add(chord);
                    claimed.Add((command, chord));
                }
            }

            if (chords.Count == 0 && (entry.Chords?.Count ?? 0) > 0)
            {
                continue; // every chord in the entry was bad: keep the command's shortcuts rather than unbinding it
            }

            accepted.Add((id, chords));
        }

        return new BindingValidation(accepted, problems);
    }

    private void SetOverride(string id, IReadOnlyList<Chord> chords)
    {
        var command = _byId[id];
        if (chords.SequenceEqual(command.DefaultChords))
        {
            _overrides.Remove(id);
        }
        else
        {
            _overrides[id] = [.. chords];
        }
    }

    private void Recompute()
    {
        var effective = new Dictionary<string, IReadOnlyList<Chord>>(StringComparer.Ordinal);
        var updateConflicts = new List<UpdateConflict>();
        foreach (var command in _commands)
        {
            if (_overrides.TryGetValue(command.Id, out var chosen))
            {
                effective[command.Id] = [.. chosen.Take(MaxChordsPerCommand)];
                continue;
            }

            var kept = new List<Chord>();
            foreach (var chord in command.DefaultChords)
            {
                var claimant = _commands.FirstOrDefault(other =>
                    other.Id != command.Id && _overrides.TryGetValue(other.Id, out var theirs) && theirs.Contains(chord) && ScopesOverlap(other.Scope, command.Scope));
                if (claimant is null)
                {
                    kept.Add(chord);
                }
                else
                {
                    updateConflicts.Add(new UpdateConflict(command.Id, chord, claimant.Id));
                }
            }

            effective[command.Id] = kept;
        }

        var byChord = new Dictionary<Chord, List<CommandDefinition>>();
        foreach (var command in _commands)
        {
            foreach (var chord in effective[command.Id])
            {
                if (!byChord.TryGetValue(chord, out var list))
                {
                    byChord[chord] = list = [];
                }

                list.Add(command);
            }
        }

        _effective = effective;
        _byChord = byChord;
        _updateConflicts = updateConflicts;
    }
}

/// <summary>The result of checking overrides: the ones that stand, and what was wrong with the others.</summary>
public sealed record BindingValidation(IReadOnlyList<(string Command, List<Chord> Chords)> Accepted, IReadOnlyList<BindingProblem> Problems);
