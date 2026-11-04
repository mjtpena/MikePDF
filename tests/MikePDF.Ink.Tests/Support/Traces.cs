namespace MikePDF.Ink.Tests.Support;

/// <summary>One synthetic centreline with a pressure profile.</summary>
internal sealed record PenTrace(string Name, InkSample[] Samples);

/// <summary>
/// Deterministic synthetic pen traces (seeded <see cref="Random"/>, test only): lines, arcs, waves, spirals, loops, hand-like
/// random curves, and zigzags and polylines with sharp turns. Heading is integrated so every kind is a legal pen path.
/// </summary>
internal static class Traces
{
    public const int Kinds = 8;

    public static IReadOnlyList<PenTrace> Build(int count, int seed = 20261104, bool sharpOnly = false)
    {
        var list = new List<PenTrace>(count);
        for (var i = 0; i < count; i++)
        {
            var kind = sharpOnly ? 4 + (i % 2 * 3) : i % Kinds; // 4 (zigzag) or 7 (polyline) when sharpOnly
            list.Add(One(kind, new Random(unchecked(seed + (i * 7919))), i));
        }

        return list;
    }

    /// <summary>Traces without sharp corners (a low-pass filter cannot follow a corner by definition): kinds 0, 1, 2, 3, 5, 6.</summary>
    public static IReadOnlyList<PenTrace> BuildSmooth(int count, int seed = 20261104, double maxStep = 2.0)
    {
        var kinds = new[] { 0, 1, 2, 3, 5, 6 };
        var list = new List<PenTrace>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(One(kinds[i % kinds.Length], new Random(unchecked(seed + (i * 7919))), i, maxStep));
        }

        return list;
    }

    public static PenTrace One(int kind, Random rng, int index, double maxStep = 2.0)
    {
        var n = rng.Next(40, 101);
        var step = 0.4 + (rng.NextDouble() * (maxStep - 0.4));
        var x = 100.0 + (rng.NextDouble() * 50);
        var y = 100.0 + (rng.NextDouble() * 50);
        var heading = rng.NextDouble() * 2 * Math.PI;
        var samples = new InkSample[n];
        var rate = 0.0;
        var radius = 30 + (rng.NextDouble() * 170);
        var curvatureSign = rng.Next(2) == 0 ? -1.0 : 1.0;
        var amplitude = 0.02 + (rng.NextDouble() * 0.03);
        var period = 20 + (rng.NextDouble() * 30);
        var legLength = rng.Next(6, 14);
        var phase = rng.NextDouble() * 6.28;
        for (var i = 0; i < n; i++)
        {
            var p = Pressure(i, n, phase, rng.Next(4) == 0);
            samples[i] = new InkSample((float)x, (float)y, (float)p);
            double dh = 0;
            switch (kind)
            {
                case 0: // straight line
                    break;
                case 1: // arc
                    dh = curvatureSign * step / radius;
                    break;
                case 2: // wave
                    dh = amplitude * Math.Cos(i / period * 2 * Math.PI) * step * 0.5;
                    break;
                case 3: // spiral, radius shrinking from 150 to about 20
                    dh = curvatureSign * step / (150.0 - (110.0 * i / n));
                    break;
                case 4: // zigzag with sharp turns
                    if (i > 0 && i % legLength == 0)
                    {
                        dh = (rng.Next(2) == 0 ? -1 : 1) * (1.8 + (rng.NextDouble() * 1.2));
                    }

                    break;
                case 5: // hand-like curve: bounded random curvature
                    rate = (rate * 0.92) + ((rng.NextDouble() - 0.5) * 0.02);
                    dh = rate * step;
                    break;
                case 6: // closed loop
                    dh = curvatureSign * 2 * Math.PI / n;
                    break;
                default: // polyline with random corners, 30 to 150 degrees
                    if (i > 0 && i % legLength == 0)
                    {
                        dh = (rng.Next(2) == 0 ? -1 : 1) * (0.5 + (rng.NextDouble() * 2.1));
                    }

                    break;
            }

            heading += dh;
            x += step * Math.Cos(heading);
            y += step * Math.Sin(heading);
        }

        return new PenTrace($"{index}-kind{kind}", samples);
    }

    private static double Pressure(int i, int n, double phase, bool quantised)
    {
        var t = (double)i / Math.Max(1, n - 1);
        var p = 0.5 + (0.45 * Math.Sin((t * 5.0) + phase));
        if (i < 2 || i > n - 3)
        {
            p = 0.0; // zero pressure at stroke start and end (ink-pen.md 7)
        }

        return quantised ? Math.Round(p * 8) / 8 : Math.Clamp(p, 0, 1);
    }
}
