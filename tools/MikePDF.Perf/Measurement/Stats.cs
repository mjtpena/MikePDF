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

/// <summary>A stopwatch in milliseconds with sub-microsecond resolution (Stopwatch ticks are not milliseconds on every machine).</summary>
public static class Clock
{
    public static long Now() => System.Diagnostics.Stopwatch.GetTimestamp();

    public static double MillisecondsSince(long start) => System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    public static double Milliseconds(long start, long end) => System.Diagnostics.Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;
}
