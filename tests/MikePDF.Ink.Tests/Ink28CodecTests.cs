using System.Buffers.Binary;

namespace MikePDF.Ink.Tests;

/// <summary>INK-28: stroke decoding from the journal base64 rejects truncated and oversized data without crashing.</summary>
public sealed class Ink28CodecTests
{
    private static InkSample[] Samples(int n, int seed = 1)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, n).Select(_ => new InkSample((float)((rng.NextDouble() * 2000) - 1000), (float)((rng.NextDouble() * 2000) - 1000), (float)rng.NextDouble())).ToArray();
    }

    private static byte[] Raw(float x, float y, byte p)
    {
        var data = new byte[9];
        BinaryPrimitives.WriteSingleLittleEndian(data, x);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), y);
        data[8] = p;
        return data;
    }

    [Fact]
    public void Ink28_RoundTripKeepsCoordinatesExactlyAndPressureToOneLevel()
    {
        var samples = Samples(500);
        var data = StrokeCodec.Encode(samples);
        Assert.Equal(500 * 9, data.Length);
        Assert.True(StrokeCodec.TryDecode(data, out var decoded, out var error), error);
        Assert.Equal(500, decoded.Length);
        for (var i = 0; i < samples.Length; i++)
        {
            Assert.Equal(samples[i].X, decoded[i].X);
            Assert.Equal(samples[i].Y, decoded[i].Y);
            Assert.InRange(Math.Abs(samples[i].Pressure - decoded[i].Pressure), 0f, (1f / 255f / 2f) + 1e-6f);
        }

        Assert.True(StrokeCodec.TryFromBase64(StrokeCodec.ToBase64(samples), out var viaText, out _));
        Assert.Equal(decoded, viaText);
    }

    [Fact]
    public void Ink28_TheLayoutIsLittleEndianFloatsThenAPressureByte()
    {
        var data = StrokeCodec.Encode([new InkSample(1f, -2f, 1f)]);
        Assert.Equal([0x00, 0x00, 0x80, 0x3F, 0x00, 0x00, 0x00, 0xC0, 0xFF], data);
        Assert.Equal(0, StrokeCodec.Encode([new InkSample(0, 0, 0)])[8]);
        Assert.Equal(255, StrokeCodec.Encode([new InkSample(0, 0, 9f)])[8]);
        Assert.Equal(255, StrokeCodec.Encode([new InkSample(0, 0, float.NaN)])[8]);
    }

    [Fact]
    public void Ink28_TruncatedDataIsRejectedAtEveryLength()
    {
        var data = StrokeCodec.Encode(Samples(40));
        for (var length = 0; length < data.Length; length++)
        {
            var ok = StrokeCodec.TryDecode(data.AsSpan(0, length), out var samples, out var error);
            if (length % 9 == 0 && length > 0)
            {
                Assert.True(ok); // a whole number of samples is a shorter stroke, valid on its own
            }
            else
            {
                Assert.False(ok);
                Assert.NotEmpty(error);
                Assert.Empty(samples);
            }
        }
    }

    [Fact]
    public void Ink28_TruncatedBase64IsRejected()
    {
        var text = StrokeCodec.ToBase64(Samples(40));
        for (var cut = 1; cut < 12; cut++)
        {
            var ok = StrokeCodec.TryFromBase64(text[..^cut], out _, out var error);
            if (ok)
            {
                Assert.Fail($"accepted base64 cut by {cut}");
            }

            Assert.NotEmpty(error);
        }
    }

    [Fact]
    public void Ink28_TheSampleCountLimitIsExactlyTwentyThousand()
    {
        var max = Samples(StrokeLimits.MaxSamplesPerStroke);
        Assert.True(StrokeCodec.TryFromBase64(StrokeCodec.ToBase64(max), out var decoded, out var error), error);
        Assert.Equal(StrokeLimits.MaxSamplesPerStroke, decoded.Length);

        var over = new byte[(StrokeLimits.MaxSamplesPerStroke + 1) * 9];
        Assert.False(StrokeCodec.TryDecode(over, out var none, out error));
        Assert.Empty(none);
        Assert.Contains("20000", error, StringComparison.Ordinal);
        Assert.False(StrokeCodec.TryFromBase64(Convert.ToBase64String(over), out _, out _));
        Assert.Throws<ArgumentException>(() => StrokeCodec.Encode(Samples(StrokeLimits.MaxSamplesPerStroke + 1)));
    }

    [Fact]
    public void Ink28_HugeTextIsRejectedWithoutDecodingIt()
    {
        var huge = new string('A', 50_000_000);
        Assert.False(StrokeCodec.TryFromBase64(huge, out var samples, out var error));
        Assert.Empty(samples);
        Assert.NotEmpty(error);
        Assert.False(StrokeCodec.TryFromBase64(new string('A', 4_000_000) + "=", out _, out _));
    }

    [Fact]
    public void Ink28_BadBase64EmptyAndNullAreRejected()
    {
        foreach (var text in new[] { "", "!!!!", "AAAA====", "A", "AAA", "AAAAAAAAAAAAé", "%%%%AAAA", "AAAA AAAA AAAA AAAA AAAA" })
        {
            Assert.False(StrokeCodec.TryFromBase64(text, out var samples, out var error), text);
            Assert.Empty(samples);
            Assert.NotEmpty(error);
        }

        Assert.False(StrokeCodec.TryFromBase64(null!, out _, out var nullError));
        Assert.NotEmpty(nullError);
        Assert.False(StrokeCodec.TryDecode([], out _, out _));
    }

    [Fact]
    public void Ink28_NonFiniteAndOutOfRangeCoordinatesAreRejected()
    {
        foreach (var bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 1e7f * 1.01f, -2e7f, float.MaxValue })
        {
            Assert.False(StrokeCodec.TryDecode(Raw(bad, 0, 10), out _, out _), $"x={bad}");
            Assert.False(StrokeCodec.TryDecode(Raw(0, bad, 10), out _, out _), $"y={bad}");
        }

        Assert.True(StrokeCodec.TryDecode(Raw(1e7f, -1e7f, 255), out var edge, out _));
        Assert.Equal(1f, edge[0].Pressure);
        Assert.Throws<ArgumentException>(() => StrokeCodec.Encode([new InkSample(float.NaN, 0, 1)]));
        Assert.Throws<ArgumentException>(() => StrokeCodec.Encode([new InkSample(0, 2e7f, 1)]));
    }

    [Fact]
    public void Ink28_SeededFuzzNeverThrowsAndOnlyAcceptsValidData()
    {
        var rng = new Random(2026);
        var valid = StrokeCodec.Encode(Samples(60, 3));
        for (var iteration = 0; iteration < 3000; iteration++)
        {
            byte[] data;
            switch (iteration % 3)
            {
                case 0: // random bytes of random length
                    data = new byte[rng.Next(0, 400)];
                    rng.NextBytes(data);
                    break;
                case 1: // a valid encoding with some bytes flipped
                    data = (byte[])valid.Clone();
                    for (var k = 0; k < 1 + rng.Next(8); k++)
                    {
                        data[rng.Next(data.Length)] ^= (byte)(1 << rng.Next(8));
                    }

                    break;
                default: // a valid encoding with a random length
                    data = valid[..rng.Next(0, valid.Length + 1)];
                    break;
            }

            if (StrokeCodec.TryDecode(data, out var samples, out var error))
            {
                Assert.True(samples.Length >= 1 && samples.Length <= StrokeLimits.MaxSamplesPerStroke);
                Assert.All(samples, s => Assert.True(float.IsFinite(s.X) && float.IsFinite(s.Y) && Math.Abs(s.X) <= 1e7 && Math.Abs(s.Y) <= 1e7 && s.Pressure is >= 0f and <= 1f));
            }
            else
            {
                Assert.NotEmpty(error);
                Assert.Empty(samples);
            }

            // The same bytes as text, and as random text.
            StrokeCodec.TryFromBase64(Convert.ToBase64String(data), out _, out _);
            var chars = new char[rng.Next(0, 300)];
            for (var c = 0; c < chars.Length; c++)
            {
                chars[c] = (char)rng.Next(32, 400);
            }

            Assert.False(StrokeCodec.TryFromBase64(new string(chars), out var fromText, out _) && fromText.Length == 0);
        }
    }

    [Fact]
    public void Ink28_AStrokeBuiltFromDecodedDataIsValid()
    {
        Assert.True(StrokeCodec.TryFromBase64(StrokeCodec.ToBase64(Samples(10)), out var decoded, out _));
        var stroke = new InkStroke("id", decoded);
        Assert.Equal(10, stroke.Samples.Count);
    }

    [Fact]
    public void Ink28_StrokeConstructorRejectsBadInput()
    {
        Assert.Throws<ArgumentException>(() => new InkStroke("", [new InkSample(0, 0, 1)]));
        Assert.Throws<ArgumentException>(() => new InkStroke("a", []));
        Assert.Throws<ArgumentException>(() => new InkStroke("a", [new InkSample(float.NaN, 0, 1)]));
        Assert.Throws<ArgumentException>(() => new InkStroke("a", [new InkSample(0, 1e9f, 1)]));
        Assert.Throws<ArgumentException>(() => new InkStroke("a", new InkSample[StrokeLimits.MaxSamplesPerStroke + 1]));
        Assert.Throws<ArgumentNullException>(() => new InkStroke("a", null!));
        var clamped = new InkStroke("a", [new InkSample(0, 0, 4f), new InkSample(0, 0, -1f), new InkSample(0, 0, float.NaN)]);
        Assert.Equal([1f, 0f, 1f], clamped.Samples.Select(s => s.Pressure));

        // The stroke is an immutable copy.
        var source = new[] { new InkSample(1, 2, 0.5f) };
        var copy = new InkStroke("c", source);
        source[0] = new InkSample(9, 9, 9);
        Assert.Equal(new InkSample(1, 2, 0.5f), copy.Samples[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<InkSample>)copy.Samples)[0] = default);
    }
}
