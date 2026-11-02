using MikePDF.Perf.Measurement;

namespace MikePDF.Perf.Tests;

public sealed class ClockPlantTests
{
    [Theory]
    [InlineData("0.18", 0.18)]
    [InlineData("1", 1.0)]
    [InlineData("2", 2.0)]
    [InlineData("0", 0.0)]
    [InlineData("-0.1", 0.0)]
    [InlineData("3", 0.0)]
    [InlineData("fast", 0.0)]
    [InlineData("", 0.0)]
    [InlineData(null, 0.0)]
    public void ThePlantedSlowdownIsAFractionBetweenZeroAndTwoAndAnythingElseIsOff(string? text, double expected) =>
        Assert.Equal(expected, Clock.ReadPlant(text));

    [Fact]
    public void WithoutTheVariableTheClockIsNotPlanted()
    {
        // The test process does not set it; if this ever fails, a plant has leaked into the environment of the test run.
        Assert.Equal(0, Clock.PlantedSlowdown);
        Assert.InRange(Clock.MillisecondsSince(Clock.Now()), 0, 100); // nothing is added to a time that is not planted
    }

    [Fact]
    public void PercentIsTheSameOnEveryMachine()
    {
        Assert.Equal("12.3%", Fmt.Percent(0.1234, 1));
        Assert.Equal("10%", Fmt.Percent(0.1, 0));
        Assert.Equal("96.9%", Fmt.Percent(0.96875, 1));
        Assert.Equal("-4.5%", Fmt.Percent(-0.045, 1));
        Assert.Equal("0%", Fmt.Percent(-0.0002, 1)); // not "-0%"
        Assert.Equal("0%", Fmt.Percent(-0.004, 0));
    }

    [Fact]
    public void LengthenSpinsForTheFractionAndReportsTheSum()
    {
        var start = Clock.Now();
        var lengthened = Clock.Lengthen(20, 0.5);
        var realMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Assert.True(lengthened >= 30, "20 ms plus at least the 10 ms asked for"); // no upper bound: a descheduled spin may overshoot
        Assert.True(realMilliseconds >= 10, "the process really waited"); // and it was not just arithmetic
        Assert.Equal(20, Clock.Lengthen(20, 0));
        Assert.Equal(0, Clock.Lengthen(0, 0.5));
    }
}
