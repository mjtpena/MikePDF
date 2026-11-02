using MikePDF.Perf.Comparison;
using MikePDF.Perf.Metrics;
using MikePDF.Perf.Results;

namespace MikePDF.Perf.Tests;

public sealed class AbRunnerTests
{
    private static readonly MetricDefinition Slow = MetricCatalog.SearchFull;
    private static readonly MetricDefinition Fast = MetricCatalog.LargeOpen;

    /// <summary>A fake machine: each call returns the next value for the side and metric, and records the order of the calls.</summary>
    private sealed class Machine(Func<AbSide, int, MetricDefinition, double> value)
    {
        private readonly Dictionary<(AbSide, string), int> _calls = [];

        public List<(AbSide Side, IReadOnlySet<string>? Only)> Calls { get; } = [];

        public Task<PerfResults> Measure(AbSide side, IReadOnlySet<string>? only)
        {
            Calls.Add((side, only));
            var results = new List<MetricResult>();
            foreach (var metric in new[] { Slow, Fast })
            {
                if (only is not null && !only.Contains(metric.Id))
                {
                    continue;
                }

                var n = _calls.GetValueOrDefault((side, metric.Id));
                _calls[(side, metric.Id)] = n + 1;
                results.Add(Make.Metric(metric, value(side, n, metric)));
            }

            return Task.FromResult(Make.Results("ci-x64", [.. results]));
        }
    }

    [Fact]
    public async Task TheSidesAreMeasuredBackToBackAndWhichGoesFirstAlternates()
    {
        var machine = new Machine((_, _, m) => m == Slow ? 1000 : 100);
        await AbRunner.RunAsync(machine.Measure, rounds: 4);
        Assert.Equal([AbSide.Base, AbSide.Head, AbSide.Head, AbSide.Base, AbSide.Base, AbSide.Head, AbSide.Head, AbSide.Base], machine.Calls.Select(c => c.Side));
        Assert.All(machine.Calls, c => Assert.Null(c.Only));
    }

    [Fact]
    public async Task AnUnchangedHeadPassesWithoutAnyExtraRound()
    {
        var machine = new Machine((_, _, m) => m == Slow ? 1000 : 100);
        var outcome = await AbRunner.RunAsync(machine.Measure, rounds: 8);
        Assert.False(outcome.Report.Failed);
        Assert.Equal(8, outcome.Rounds.Count);
        Assert.Equal(16, machine.Calls.Count);
        Assert.Empty(outcome.Confirmed);
    }

    [Fact]
    public async Task ARegressionIsMeasuredAgainOnItsOwnAndFailsWhenItHolds()
    {
        var machine = new Machine((side, _, m) => m == Slow ? (side == AbSide.Head ? 1300 : 1000) : 100);
        var outcome = await AbRunner.RunAsync(machine.Measure, rounds: 6, confirmRounds: 3);
        Assert.True(outcome.Report.Failed);
        Assert.Equal([Slow.Id], outcome.Confirmed);
        Assert.Equal(9, outcome.Rounds.Count);
        Assert.Equal(18, machine.Calls.Count);
        Assert.All(machine.Calls.Skip(12), c => Assert.Equal(new HashSet<string> { Slow.Id }, c.Only)); // only the suspect, only after the planned rounds
        Assert.Equal(9, outcome.Report.Items.Single(i => i.Id == Slow.Id).Pairs);
        Assert.Equal(6, outcome.Report.Items.Single(i => i.Id == Fast.Id).Pairs);
        Assert.Equal(1300, outcome.Head.Find(Slow.Id)!.Median);
        Assert.Equal(1000, outcome.Base.Find(Slow.Id)!.Median);
    }

    [Fact]
    public async Task AFlukeInTheFirstRoundsIsDilutedByTheConfirmationAndPasses()
    {
        // The head looks 30% slower in all of the first six rounds (something on the machine, wrongly blamed on the head), then is the same as the base.
        var machine = new Machine((side, n, m) => m == Slow ? (side == AbSide.Head && n < 6 ? 1300 : 1000) : 100);
        var outcome = await AbRunner.RunAsync(machine.Measure, rounds: 6, confirmRounds: 6);
        Assert.Equal([Slow.Id], outcome.Confirmed);
        Assert.False(outcome.Report.Failed);
    }

    [Fact]
    public async Task ASlowdownTheFirstRoundsCouldNotQuiteProveIsMeasuredAgainAndThenFails()
    {
        // 13% slower in five of the first six rounds and equal in one: the interval reaches down to 0%, so the first verdict is "unconfirmed". Six more rounds, all 13%, settle it.
        var machine = new Machine((side, n, m) => m == Slow ? (side == AbSide.Head && n != 2 ? 1130 : 1000) : 100);
        var outcome = await AbRunner.RunAsync(machine.Measure, rounds: 6, confirmRounds: 6);
        Assert.Equal([Slow.Id], outcome.Confirmed);
        Assert.True(outcome.Report.Failed);
        Assert.Equal(12, outcome.Report.Items.Single(i => i.Id == Slow.Id).Pairs);
    }

    [Fact]
    public async Task WithNoConfirmationRoundsTheFirstVerdictStands()
    {
        var machine = new Machine((side, _, m) => m == Slow ? (side == AbSide.Head ? 1300 : 1000) : 100);
        var outcome = await AbRunner.RunAsync(machine.Measure, rounds: 6, confirmRounds: 0);
        Assert.True(outcome.Report.Failed);
        Assert.Equal(6, outcome.Rounds.Count);
    }

    [Fact]
    public async Task ASlowTrendThatBothSidesShareDoesNotFailAnything()
    {
        // The machine gets 3% slower with every pair (base and head alike): the alternating order cancels the trend in the ratios.
        var calls = 0;
        var machine = new Machine((_, _, m) => (m == Slow ? 1000 : 100) * (1 + (0.03 * Interlocked.Increment(ref calls) / 2)));
        var outcome = await AbRunner.RunAsync(machine.Measure, rounds: 8);
        Assert.False(outcome.Report.Failed);
    }

    [Fact]
    public async Task RoundsMustBePositive()
    {
        var machine = new Machine((_, _, _) => 1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => AbRunner.RunAsync(machine.Measure, rounds: 0));
    }
}
