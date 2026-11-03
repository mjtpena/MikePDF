namespace MikePDF.Perf.Measurement;

/// <summary>
/// The median of a handful of paired ratios with a distribution-free confidence interval. Each ratio is one round of the same-runner A/B run (head over
/// base, measured back to back), so a slow minute on the shared machine moves both halves of a pair and cancels. The interval is the sign-test one: the
/// k-th smallest to the k-th largest of n ratios contains the true median with probability 1 - 2 * P(Binomial(n, 1/2) &lt;= k - 1), whatever the
/// distribution of the ratios. No normality is assumed (hosted-runner noise is skewed, with long tails), at the price of an interval that, for few rounds,
/// is close to the range of the ratios.
/// </summary>
public static class PairedStats
{
    /// <summary>The estimate (median), the interval, the confidence the interval really has (the closest the discrete rule gets to the one asked for) and the number of ratios.</summary>
    public sealed record Interval(double Estimate, double Lower, double Upper, double Confidence, int Count);

    /// <summary>The fewest ratios with which a 95% interval exists: the whole range of 6 ratios covers the median with probability 96.9%, of 5 only 93.8%.</summary>
    public const int MinimumFor95 = 6;

    public static Interval MedianInterval(IReadOnlyList<double> ratios, double confidence = 0.95)
    {
        ArgumentNullException.ThrowIfNull(ratios);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(confidence, 0.0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(confidence, 1.0);
        if (ratios.Count == 0)
        {
            throw new ArgumentException("At least one ratio is needed.", nameof(ratios));
        }

        var sorted = ratios.Order().ToArray();
        var n = sorted.Length;
        var k = 1;
        for (var candidate = n / 2; candidate >= 1; candidate--)
        {
            if (Coverage(n, candidate) >= confidence)
            {
                k = candidate;
                break;
            }
        }

        return new Interval(Stats.Median(sorted), sorted[k - 1], sorted[n - k], Coverage(n, k), n);
    }

    /// <summary>The probability that [x(k), x(n+1-k)] contains the median of the distribution the n values were drawn from.</summary>
    public static double Coverage(int n, int k)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);
        if (2 * k > n + 1)
        {
            return 0;
        }

        // P(Binomial(n, 1/2) <= k - 1), built from C(n, i) / 2^n without overflow.
        var tail = 0.0;
        var term = Math.Pow(0.5, n); // i = 0
        for (var i = 0; i < k; i++)
        {
            tail += term;
            term = term * (n - i) / (i + 1);
        }

        return Math.Max(0, 1 - (2 * tail));
    }
}
