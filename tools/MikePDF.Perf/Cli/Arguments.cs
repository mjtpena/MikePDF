using System.Globalization;

namespace MikePDF.Perf.Cli;

/// <summary>A command line of the form <c>command --name value --name value --flag</c>. No abbreviations, no guessing: an unknown option is an error.</summary>
public sealed class Arguments
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    private Arguments(string command)
    {
        Command = command;
    }

    public string Command { get; }

    public static Arguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0 || args[0].StartsWith('-'))
        {
            throw new UsageException("A command is required.");
        }

        var parsed = new Arguments(args[0]);
        for (var i = 1; i < args.Count; i++)
        {
            var name = args[i];
            if (!name.StartsWith("--", StringComparison.Ordinal) || name.Length == 2)
            {
                throw new UsageException($"Unexpected argument '{name}'.");
            }

            name = name[2..];
            var value = "true";
            if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            if (!parsed._values.TryGetValue(name, out var list))
            {
                parsed._values[name] = list = [];
            }

            list.Add(value);
        }

        return parsed;
    }

    public string? Get(string name)
    {
        _used.Add(name);
        return _values.TryGetValue(name, out var list) ? list[^1] : null;
    }

    public IReadOnlyList<string> GetAll(string name)
    {
        _used.Add(name);
        return _values.TryGetValue(name, out var list) ? list : [];
    }

    public bool Flag(string name) => Get(name) is { } value && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    public string Require(string name) => Get(name) ?? throw new UsageException($"--{name} is required.");

    public double GetDouble(string name, double fallback)
    {
        var text = Get(name);
        return text is null ? fallback : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : throw new UsageException($"--{name} must be a number, not '{text}'.");
    }

    public int GetInt(string name, int fallback)
    {
        var text = Get(name);
        return text is null ? fallback : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : throw new UsageException($"--{name} must be a whole number, not '{text}'.");
    }

    /// <summary>Throws if an option was given that nothing asked for (a typo must not silently change what is measured).</summary>
    public void RejectUnused()
    {
        var unknown = _values.Keys.Where(k => !_used.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            throw new UsageException("Unknown option(s): " + string.Join(", ", unknown.Select(u => "--" + u)));
        }
    }
}

public sealed class UsageException(string message) : Exception(message);
