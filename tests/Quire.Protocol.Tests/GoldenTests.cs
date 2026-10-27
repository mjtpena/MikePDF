using System.Text;
using Quire.ProtocolGen;

namespace Quire.Protocol.Tests;

/// <summary>
/// Golden frames shared with the C++ tests. Run with QUIRE_UPDATE_GOLDEN=1 to rewrite tests/Quire.Protocol.Tests/golden
/// after an intentional protocol change; otherwise the files must match what the C# encoder produces today.
/// </summary>
public sealed class GoldenTests
{
    private static readonly string Dir = Path.Combine(Generator.FindRoot(AppContext.BaseDirectory), "tests", "Quire.Protocol.Tests", "golden");

    private static IEnumerable<(string File, string Kind, string Expect, byte[] Frame)> All()
    {
        foreach (var r in Samples.Requests)
            yield return (r.Name + ".bin", "request", "Ok", ProtocolCodec.EncodeRequestFrame(r.RequestId, r.DocumentId, r.DeadlineMs, r.Payload).Value);
        foreach (var r in Samples.Responses)
            yield return (r.Name + ".bin", "response", "Ok", ProtocolCodec.EncodeResponseFrame(r.RequestId, r.Status, r.Type, r.Payload).Value);
        foreach (var v in GoldenVectors.InvalidFrames)
            yield return (v.Name + ".bin", v.IsRequest ? "request" : "response", v.Expected.ToString(), v.Frame);
    }

    // One line per file: name, request|response, the status both decoders must produce.
    private static string Manifest() => string.Concat(All().Select(a => $"{a.File} {a.Kind} {a.Expect}\n"));

    [Fact]
    public void Golden_files_are_current()
    {
        if (Environment.GetEnvironmentVariable("QUIRE_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Dir);
            foreach (var old in Directory.EnumerateFiles(Dir)) File.Delete(old);
            foreach (var (file, _, _, frame) in All()) File.WriteAllBytes(Path.Combine(Dir, file), frame);
            File.WriteAllText(Path.Combine(Dir, "manifest.txt"), Manifest(), new UTF8Encoding(false));
        }

        // git may check text out with CRLF; the manifest is parsed line by line, so compare without line endings.
        Assert.Equal(Manifest(), File.ReadAllText(Path.Combine(Dir, "manifest.txt")).Replace("\r\n", "\n", StringComparison.Ordinal));
        foreach (var (file, _, _, frame) in All())
            Assert.True(frame.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Dir, file))), file + " differs; run with QUIRE_UPDATE_GOLDEN=1 if the change is intended");
        Assert.Equal(All().Count() + 1, Directory.GetFiles(Dir).Length);
    }

    public static TheoryData<string> ValidFiles => [.. All().Where(a => a.Expect == "Ok").Select(a => a.File)];

    public static TheoryData<string> InvalidFiles => [.. All().Where(a => a.Expect != "Ok").Select(a => a.File)];

    [Theory]
    [MemberData(nameof(ValidFiles))]
    public void Valid_golden_files_decode_and_re_encode_to_identical_bytes(string file)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Dir, file));
        if (file.StartsWith("req_", StringComparison.Ordinal))
        {
            var sample = Samples.Requests.Single(r => r.Name + ".bin" == file);
            var e = ProtocolCodec.DecodeRequestFrame(bytes).Value;
            Assert.Equivalent(sample.Payload, e.Payload, strict: true);
            Assert.Equal(bytes, ProtocolCodec.EncodeRequestFrame(e.RequestId, e.DocumentId, e.DeadlineMs, e.Payload).Value);
        }
        else
        {
            var sample = Samples.Responses.Single(r => r.Name + ".bin" == file);
            var e = ProtocolCodec.DecodeResponseFrame(bytes).Value;
            Assert.Equal(sample.Status, e.Status);
            Assert.Equivalent(sample.Payload, e.Payload, strict: true);
            Assert.Equal(bytes, ProtocolCodec.EncodeResponseFrame(e.RequestId, e.Status, e.MessageType, e.Payload).Value);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidFiles))]
    public void Invalid_golden_files_are_rejected_with_the_expected_status(string file)
    {
        var vector = GoldenVectors.InvalidFrames.Single(v => v.Name + ".bin" == file);
        var bytes = File.ReadAllBytes(Path.Combine(Dir, file));
        var status = vector.IsRequest ? ProtocolCodec.DecodeRequestFrame(bytes).Status : ProtocolCodec.DecodeResponseFrame(bytes).Status;
        Assert.Equal(vector.Expected, status);
    }

    [Fact]
    public void Every_malformed_vector_is_malformed_in_the_other_direction_too()
    {
        // A request frame is never a valid response and vice versa, so a mix-up cannot slip through.
        foreach (var r in Samples.Requests)
            Assert.False(ProtocolCodec.DecodeResponseFrame(ProtocolCodec.EncodeRequestFrame(r.RequestId, r.DocumentId, r.DeadlineMs, r.Payload).Value).IsOk, r.Name);
        foreach (var r in Samples.Responses)
            Assert.False(ProtocolCodec.DecodeRequestFrame(ProtocolCodec.EncodeResponseFrame(r.RequestId, r.Status, r.Type, r.Payload).Value).IsOk, r.Name);
    }
}
