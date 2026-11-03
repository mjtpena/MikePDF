namespace MikePDF.Perf.Measurement;

/// <summary>Summary of a list of samples. Percentiles use the nearest-rank rule, so p95 of 20 samples is the 19th smallest and is always a value that was measured.</summary>
public sealed record SampleSummary(int Count, double Min, double Median, double Mean, double P95, double Max, double StdDev)
{
    /// <summary>Standard deviation as a fraction of the mean; 0 when the mean is 0.</summary>
    public double CoefficientOfVariation => Mean == 0 ? 0 : StdDev / Math.Abs(Mean);
}

public static class Stats
{
    public static SampleSummary Summarize(IReadOnlyList<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            throw new ArgumentException("At least one sample is needed.", nameof(samples));
        }

        var sorted = samples.Order().ToArray();
        var mean = sorted.Average();
        var variance = sorted.Length > 1 ? sorted.Sum(s => (s - mean) * (s - mean)) / (sorted.Length - 1) : 0;
        return new SampleSummary(sorted.Length, sorted[0], MedianOfSorted(sorted), mean, Percentile(sorted, 0.95), sorted[^1], Math.Sqrt(variance));
    }

    public static double Median(IReadOnlyList<double> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            throw new ArgumentException("At least one sample is needed.", nameof(samples));
        }

        return MedianOfSorted(samples.Order().ToArray());
    }

    /// <summary>The nearest-rank percentile of already sorted samples: the smallest value with at least <paramref name="fraction"/> of the samples at or below it.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double fraction)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Count == 0)
        {
            throw new ArgumentException("At least one sample is needed.", nameof(sorted));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(fraction);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fraction, 1.0);
        var rank = (int)Math.Ceiling(fraction * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    private static double MedianOfSorted(double[] sorted)
    {
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }
}

/// <summary>Numbers as text for reports: the same on every machine.</summary>
public static class Fmt
{
    /// <summary>A fraction as a percentage with <paramref name="decimals"/> decimals: 0.1234 is "12.3%".</summary>
    public static string Percent(double fraction, int decimals = 1)
    {
        var value = Math.Round(fraction * 100, decimals);
        return (value == 0 ? 0.0 : value).ToString(decimals == 0 ? "0'%'" : "0." + new string('#', decimals) + "'%'", System.Globalization.CultureInfo.InvariantCulture); // no "-0%"
    }
}

/// <summary>
/// A stopwatch in milliseconds with sub-microsecond resolution (Stopwatch ticks are not milliseconds on every machine).
/// <para>
/// <b>Planted slowdown (test only).</b> With the environment variable <see cref="PlantVariable"/> set to a fraction (0.18), every elapsed time this class
/// returns is lengthened by that fraction of itself: the call spins for the extra time (so the process really is that much slower than the stopwatch saw)
/// and returns the elapsed time including it. It exists to prove that the A/B gate catches a slowdown of a known size on a noisy runner. It lives in this
/// tool, which is not part of any package, and is off unless the variable is set; a result made with it says so in its notes. It does not touch the
/// memory and size metrics, which are not times.
/// </para>
/// </summary>
public static class Clock
{
    public const string PlantVariable = "MIKEPDF_PERF_PLANT_SLOWDOWN";

    /// <summary>The planted fraction (0 when off). Read once, from the environment of this process.</summary>
    public static double PlantedSlowdown { get; } = ReadPlant(System.Environment.GetEnvironmentVariable(PlantVariable));

    public static long Now() => System.Diagnostics.Stopwatch.GetTimestamp();

    public static double MillisecondsSince(long start) => Planted(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);

    public static double Milliseconds(long start, long end) => Planted(System.Diagnostics.Stopwatch.GetElapsedTime(start, end).TotalMilliseconds);

    /// <summary>Parses the variable: a fraction between 0 and 2, anything else (or nothing) is off.</summary>
    public static double ReadPlant(string? text) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && value is > 0 and <= 2 ? value : 0;

    private static double Planted(double milliseconds) => PlantedSlowdown <= 0 ? milliseconds : Lengthen(milliseconds, PlantedSlowdown);

    /// <summary>Spins for <paramref name="fraction"/> of <paramref name="milliseconds"/> and returns the elapsed time plus the time really spent spinning.</summary>
    internal static double Lengthen(double milliseconds, double fraction)
    {
        if (fraction <= 0 || milliseconds <= 0)
        {
            return milliseconds;
        }

        var spinStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var extra = milliseconds * fraction;
        double spun;
        do
        {
            spun = System.Diagnostics.Stopwatch.GetElapsedTime(spinStart).TotalMilliseconds;
        }
        while (spun < extra);
        return milliseconds + spun;
    }
}
