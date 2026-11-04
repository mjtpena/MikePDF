using System.Buffers.Binary;

namespace MikePDF.Ink;

/// <summary>
/// The journal encoding of a stroke (ink-pen.md 9): little-endian packed samples, each x f32, y f32, pressure u8 (9 bytes),
/// then base64. Pressure is quantised to 1/255. Decoding is hostile-input safe: it never throws and never allocates more than
/// <see cref="StrokeLimits.MaxSamplesPerStroke"/> samples (INK-28).
/// </summary>
public static class StrokeCodec
{
    private const int MaxBytes = StrokeLimits.MaxSamplesPerStroke * StrokeLimits.BytesPerSample;

    // Base64 text of the largest legal stroke, plus slack for padding and line breaks the decoder tolerates.
    private const int MaxBase64Chars = ((MaxBytes + 2) / 3 * 4) + 64;

    /// <summary>Encodes samples to the packed binary form. Throws <see cref="ArgumentException"/> for too many samples or invalid coordinates.</summary>
    public static byte[] Encode(IReadOnlyList<InkSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count > StrokeLimits.MaxSamplesPerStroke)
        {
            throw new ArgumentException($"At most {StrokeLimits.MaxSamplesPerStroke} samples can be encoded.", nameof(samples));
        }

        var data = new byte[samples.Count * StrokeLimits.BytesPerSample];
        for (var i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            if (!InkStroke.IsValidCoordinate(s.X) || !InkStroke.IsValidCoordinate(s.Y))
            {
                throw new ArgumentException($"Sample {i} has a non-finite or out-of-range coordinate.", nameof(samples));
            }

            var span = data.AsSpan(i * StrokeLimits.BytesPerSample, StrokeLimits.BytesPerSample);
            BinaryPrimitives.WriteSingleLittleEndian(span, s.X);
            BinaryPrimitives.WriteSingleLittleEndian(span[4..], s.Y);
            span[8] = (byte)Math.Round(InkStroke.ClampPressure(s.Pressure) * 255f, MidpointRounding.AwayFromZero);
        }

        return data;
    }

    /// <summary>Encodes samples to base64 text (the journal <c>data</c> field).</summary>
    public static string ToBase64(IReadOnlyList<InkSample> samples) => Convert.ToBase64String(Encode(samples));

    /// <summary>
    /// Decodes packed samples. Returns false (with a message) for empty data, a length that is not a multiple of 9, more than
    /// 20,000 samples, a non-finite coordinate or one beyond 10,000,000 in magnitude. Never throws.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, out InkSample[] samples, out string error)
    {
        samples = [];
        if (data.Length == 0)
        {
            error = "The stroke data is empty.";
            return false;
        }

        if (data.Length % StrokeLimits.BytesPerSample != 0)
        {
            error = "The stroke data length is not a multiple of 9 bytes (truncated).";
            return false;
        }

        var count = data.Length / StrokeLimits.BytesPerSample;
        if (count > StrokeLimits.MaxSamplesPerStroke)
        {
            error = $"The stroke has more than {StrokeLimits.MaxSamplesPerStroke} samples.";
            return false;
        }

        var result = new InkSample[count];
        for (var i = 0; i < count; i++)
        {
            var span = data.Slice(i * StrokeLimits.BytesPerSample, StrokeLimits.BytesPerSample);
            var x = BinaryPrimitives.ReadSingleLittleEndian(span);
            var y = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
            if (!InkStroke.IsValidCoordinate(x) || !InkStroke.IsValidCoordinate(y))
            {
                error = $"Sample {i} has a non-finite or out-of-range coordinate.";
                return false;
            }

            result[i] = new InkSample(x, y, span[8] / 255f);
        }

        samples = result;
        error = string.Empty;
        return true;
    }

    /// <summary>Decodes base64 text produced by <see cref="ToBase64"/>. Never throws; see <see cref="TryDecode"/> for the rejected inputs.</summary>
    public static bool TryFromBase64(string text, out InkSample[] samples, out string error)
    {
        samples = [];
        if (string.IsNullOrEmpty(text))
        {
            error = "The stroke text is empty.";
            return false;
        }

        if (text.Length > MaxBase64Chars)
        {
            error = "The stroke text is longer than the largest legal stroke.";
            return false;
        }

        var buffer = new byte[(text.Length / 4 * 3) + 3];
        if (!Convert.TryFromBase64String(text, buffer, out var written))
        {
            error = "The stroke text is not valid base64.";
            return false;
        }

        return TryDecode(buffer.AsSpan(0, written), out samples, out error);
    }
}
