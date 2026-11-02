using MikePDF.Core.Settings;

namespace MikePDF.Core.Tests;

/// <summary>A clock that stands still unless told, whose timers never fire by themselves: a test calls Flush() to make the store write.</summary>
internal sealed class ManualTime : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero).Ticks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Add(ref _ticks, TimeSpan.TicksPerMillisecond), TimeSpan.Zero);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new NoTimer();

    private sealed class NoTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>A scratch folder that is deleted afterwards, and the settings objects built in it.</summary>
internal sealed class SettingsFixture : IDisposable
{
    public SettingsFixture()
    {
        Directory = Path.Combine(Path.GetTempPath(), "mikepdf-settings-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(Directory);
        Policy = new FixedPolicySource();
    }

    public string Directory { get; }

    public FixedPolicySource Policy { get; }

    public string SettingsPath => Path.Combine(Directory, "settings.json");

    public ManualTime Time { get; } = new();

    public PolicyProvider Provider => _provider ??= new PolicyProvider(Policy);

    private PolicyProvider? _provider;

    public SettingsStore Open() => new(SettingsPath, Provider, Time);

    public static RawPolicyValue Dword(string name, int value) => new(name, RawPolicyKind.Dword, value);

    public static RawPolicyValue Text(string name, string value) => new(name, RawPolicyKind.Text, Text: value);

    public static RawPolicyValue Lines(string name, params string[] lines) => new(name, RawPolicyKind.TextList, Lines: lines);

    /// <summary>Puts a policy in force and lets the provider see it.</summary>
    public void SetPolicy(PolicyScope scope, RawPolicyValue value)
    {
        Policy.Set(scope, value);
        Provider.Refresh();
    }

    public void ClearPolicies()
    {
        Policy.Clear(PolicyScope.Machine);
        Policy.Clear(PolicyScope.User);
        Provider.Refresh();
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A scratch folder left in %TEMP% is not worth failing a test for.
        }
    }
}
