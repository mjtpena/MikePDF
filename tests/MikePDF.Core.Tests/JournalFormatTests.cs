using System.Text;
using MikePDF.Core.Editing;
using MikePDF.Core.Editing.Journal;

namespace MikePDF.Core.Tests;

/// <summary>SP-03 and SP-05 (save-pipeline 14): the journal's line format, its strict reader, its key and its fingerprint.</summary>
public sealed class JournalFormatTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 10, 30, 0, 123, TimeSpan.Zero);

    private static byte[] Journal(params JournalEntry[] entries)
    {
        var bytes = new List<byte>(JournalCodec.EncodeHeader(JournalSamples.Header()));
        foreach (var entry in entries)
        {
            bytes.AddRange(JournalCodec.EncodeEntry(entry));
        }

        return [.. bytes];
    }

    private static JournalEntry[] Sample() =>
    [
        new CommandEntry(1, T0, Edits.Add(name: "a", text: "caf\u00E9 \u2603 \"quoted\"")),
        new CommandEntry(2, T0, Edits.Field()),
        new UndoEntry(3, T0, 2),
        new RedoEntry(4, T0, 2),
        new SaveIntentEntry(5, T0, @"C:\x\.a.pdf.mikepdf-0a1b2c3d.tmp", @"C:\x\.a.pdf.mikepdf-0a1b2c3d.bak", @"C:\x\a.pdf"),
        new SaveBeginEntry(6, T0, new string('b', 64), 123999),
        new SaveCommittedEntry(7, T0),
        new DiscardEntry(8, T0),
    ];

    [Fact]
    public void EveryRecordKindRoundTripsAndTheFileIsAsciiLinesEndingInLf()
    {
        var bytes = Journal(Sample());

        var read = JournalCodec.Parse(bytes);

        Assert.Equal(JournalReadStatus.Ok, read.Status);
        Assert.Equal(8, read.ValidRecords);
        var expectedHeader = JournalSamples.Header();
        Assert.Equal((expectedHeader.DocPath, expectedHeader.Size, expectedHeader.Modified, expectedHeader.Fingerprint, expectedHeader.App), (read.Header!.DocPath, read.Header.Size, read.Header.Modified, read.Header.Fingerprint, read.Header.App));
        Assert.Empty(read.Header.Pages);
        var first = Assert.IsType<CommandEntry>(read.Entries[0]);
        Assert.Equal("AddAnnotation", first.Command.Cmd);
        Assert.Equal("caf\u00E9 \u2603 \"quoted\"", first.Command.Payload.GetProperty("contents").GetString());
        Assert.Equal(T0, first.Time);
        Assert.Equal(2, Assert.IsType<UndoEntry>(read.Entries[2]).Target);
        var intent = Assert.IsType<SaveIntentEntry>(read.Entries[4]);
        Assert.EndsWith(".tmp", intent.Temp, StringComparison.Ordinal);
        Assert.Equal(@"C:\x\a.pdf", intent.Target);
        Assert.Equal(123999, Assert.IsType<SaveBeginEntry>(read.Entries[5]).OutputLength);
        Assert.All(bytes, static b => Assert.True(b < 0x80));
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.Equal(9, bytes.Count(static b => b == (byte)'\n'));
    }

    [Fact]
    public void TheChecksumIsCrc32COfTheRecordWithoutItsCrcField()
    {
        // The CRC-32C check value ("123456789") is 0xE3069283.
        Assert.Equal(0xE3069283u, Crc32C.Compute("123456789"u8));

        var line = Encoding.ASCII.GetString(JournalCodec.EncodeEntry(new SaveCommittedEntry(7, T0))).TrimEnd('\n');
        var marker = line.LastIndexOf(",\"crc\":\"", StringComparison.Ordinal);
        var stated = line.Substring(marker + 8, 8);
        Assert.Equal(Crc32C.Compute(Encoding.ASCII.GetBytes(line[..marker])).ToString("x8", System.Globalization.CultureInfo.InvariantCulture), stated);
        Assert.Equal("{\"v\":1,\"seq\":7,\"t\":\"2026-11-02T10:30:00.123Z\",\"cmd\":\"SaveCommitted\"", line[..marker]);
    }

    [Fact]
    public void ATornLastLineIsSilentAndTheRestIsKept()
    {
        var bytes = Journal(Sample());
        var cut = bytes[..^20]; // the last record loses its tail, including the line end

        var read = JournalCodec.Parse(cut);

        Assert.Equal(JournalReadStatus.TornTail, read.Status);
        Assert.Equal(7, read.ValidRecords);
        Assert.True(read.IgnoredBytes > 0);
        Assert.Null(read.Problem);
    }

    [Fact]
    public void ACorruptRecordInTheMiddleYieldsTheValidPrefixAndAReport()
    {
        var bytes = Journal(Sample());
        var secondRecord = Array.IndexOf(bytes, (byte)'\n', Array.IndexOf(bytes, (byte)'\n') + 1) + 1;
        bytes[secondRecord + 10]++; // inside the second command record

        var read = JournalCodec.Parse(bytes);

        Assert.Equal(JournalReadStatus.Corrupt, read.Status);
        Assert.Equal(1, read.ValidRecords);
        Assert.NotNull(read.Problem);
        Assert.True(read.IgnoredBytes > 0);
    }

    [Fact]
    public void AGapOrARepeatInTheSequenceStopsReading()
    {
        var gap = JournalCodec.Parse(Journal(new CommandEntry(1, T0, Edits.Add()), new CommandEntry(3, T0, Edits.Add(name: "b"))));
        var repeat = JournalCodec.Parse(Journal(new CommandEntry(1, T0, Edits.Add()), new CommandEntry(1, T0, Edits.Add(name: "b"))));
        var late = JournalCodec.Parse(Journal(new CommandEntry(2, T0, Edits.Add())));

        Assert.Equal((JournalReadStatus.Corrupt, 1), (gap.Status, gap.ValidRecords));
        Assert.Equal((JournalReadStatus.Corrupt, 1), (repeat.Status, repeat.ValidRecords));
        Assert.Equal((JournalReadStatus.Corrupt, 0), (late.Status, late.ValidRecords));
    }

    [Fact]
    public void AnUnknownCommandWithAGoodChecksumIsSkippedAndCounted()
    {
        var bytes = new List<byte>(JournalCodec.EncodeHeader(JournalSamples.Header()));
        bytes.AddRange(JournalCodec.EncodeEntry(new CommandEntry(1, T0, Edits.Add())));
        bytes.AddRange(JournalSamples.RawRecord("{\"v\":1,\"seq\":2,\"t\":\"2026-11-02T10:30:00.123Z\",\"cmd\":\"FutureThing\",\"x\":[1,2]"));
        bytes.AddRange(JournalCodec.EncodeEntry(new CommandEntry(3, T0, Edits.Add(name: "c"))));

        var read = JournalCodec.Parse([.. bytes]);

        Assert.Equal(JournalReadStatus.Ok, read.Status);
        Assert.Equal(3, read.ValidRecords);
        Assert.Equal(1, read.UnknownCommands);
        Assert.Equal("FutureThing", Assert.IsType<UnknownEntry>(read.Entries[1]).Cmd);
    }

    [Theory]
    [InlineData("{\"v\":1,\"seq\":1,\"seq\":1,\"t\":\"2026-11-02T10:30:00.123Z\",\"cmd\":\"AddAnnotation\"")] // duplicate key
    [InlineData("{\"v\":2,\"seq\":1,\"t\":\"2026-11-02T10:30:00.123Z\",\"cmd\":\"AddAnnotation\"")] // another version
    [InlineData("{\"v\":1,\"seq\":1,\"t\":\"yesterday\",\"cmd\":\"AddAnnotation\"")] // time not in the one format
    [InlineData("{\"v\":1,\"seq\":1,\"t\":\"2026-11-02T10:30:00.123Z\"")] // no cmd
    [InlineData("{\"v\":1,\"seq\":1,\"t\":\"2026-11-02T10:30:00.123Z\",\"cmd\":\"AddAnnotation\",\"a\":{\"a\":1,\"a\":2}")] // duplicate key below the top
    [InlineData("{\"v\":1,\"seq\":1,\"t\":\"2026-11-02T10:30:00.123Z\",\"cmd\":\"Undo\",\"target\":5")] // undoes something that is not before it
    [InlineData("{\"v\":1,\"seq\":1,\"t\":\"2026-11-02T10:30:00.123Z\",\"cmd\":\"SaveBegin\",\"outputSha256\":\"nothex\",\"outputLength\":1")]
    [InlineData("{\"v\":1,\"seq\":1,\"t\":\"2026-11-02T10:30:00.123Z\",\"cmd\":\"SaveCommitted\",\"extra\":1")] // a control record with a field it does not have
    [InlineData("{\"v\":1,\"seq\":1,\"t\":\"2026-11-02T10:30:00.123Z\",\"cmd\":\"SaveIntent\",\"temp\":\"\",\"saveTarget\":\"C:\\\\a.pdf\"")]
    public void ARecordThatBreaksARuleStopsReadingEvenWithAGoodChecksum(string body)
    {
        var bytes = new List<byte>(JournalCodec.EncodeHeader(JournalSamples.Header()));
        bytes.AddRange(JournalSamples.RawRecord(body));

        var read = JournalCodec.Parse([.. bytes]);

        Assert.Equal(JournalReadStatus.Corrupt, read.Status);
        Assert.Equal(0, read.ValidRecords);
    }

    [Fact]
    public void HostileFilesNeverThrowAndNeverYieldRecords()
    {
        var header = JournalCodec.EncodeHeader(JournalSamples.Header());
        var deep = JournalSamples.RawRecord("{\"v\":1,\"seq\":1,\"t\":\"2026-11-02T10:30:00.123Z\",\"cmd\":\"AddAnnotation\",\"a\":" + new string('[', 40) + new string(']', 40));
        var notObject = JournalSamples.RawRecord("[1");
        var badUtf8 = JournalSamples.RawRecord("{\"v\":1,\"seq\":1,\"t\":\"2026-11-02T10:30:00.123Z\",\"cmd\":\"AddAnnotation\",\"a\":\"\u00ff");
        badUtf8[^20] = 0xFF;
        var noCrc = "{\"v\":1,\"seq\":1}\n"u8.ToArray();
        var wrongCrc = Encoding.ASCII.GetBytes("{\"v\":1,\"seq\":1,\"t\":\"2026-11-02T10:30:00.123Z\",\"cmd\":\"SaveCommitted\",\"crc\":\"00000000\"}\n");
        var lone = Encoding.ASCII.GetBytes("\n\n\n");

        foreach (var hostile in new[] { deep, notObject, badUtf8, noCrc, wrongCrc, lone })
        {
            var read = JournalCodec.Parse([.. header, .. hostile]);
            Assert.Equal(0, read.ValidRecords);
            Assert.NotEqual(JournalReadStatus.Ok, read.Status);
        }
    }

    [Fact]
    public void AnythingWithoutAValidHeaderIsUnreadableAndAnEmptyFileIsEmpty()
    {
        Assert.Equal(JournalReadStatus.Empty, JournalCodec.Parse([]).Status);
        Assert.Equal(JournalReadStatus.Unreadable, JournalCodec.Parse(Encoding.ASCII.GetBytes("not a journal\n")).Status);
        Assert.Equal(JournalReadStatus.Unreadable, JournalCodec.Parse(JournalCodec.EncodeEntry(new SaveCommittedEntry(1, T0))).Status);
        var header = JournalCodec.EncodeHeader(JournalSamples.Header());
        Assert.Equal(JournalReadStatus.Empty, JournalCodec.Parse(header.AsSpan(0, 10)).Status); // the header itself was torn: nothing was ever acknowledged
        Assert.Equal(JournalReadStatus.Ok, JournalCodec.Parse(header).Status);
    }

    [Fact]
    public void LimitsAreEnforcedOnReadAndOnWrite()
    {
        var limits = new JournalLimits { MaxRecordBytes = 300, MaxRecords = 3, MaxDocumentBytes = 5_000 };
        var big = EditCommand.Create("AddAnnotation", new { text = new string('x', 400) });

        Assert.Throws<JournalRecordTooLargeException>(() => JournalCodec.EncodeEntry(new CommandEntry(1, T0, big), limits));

        var bytes = new List<byte>(JournalCodec.EncodeHeader(JournalSamples.Header(), limits));
        for (var i = 1; i <= 4; i++)
        {
            bytes.AddRange(JournalCodec.EncodeEntry(new CommandEntry(i, T0, Edits.Add(name: "n" + i)), limits));
        }

        var read = JournalCodec.Parse([.. bytes], limits);
        Assert.Equal(JournalReadStatus.TooLarge, read.Status);
        Assert.Equal(3, read.ValidRecords);

        var oversized = JournalCodec.Parse(new byte[10_000_000], limits);
        Assert.Equal(JournalReadStatus.TooLarge, oversized.Status);
    }

    [Fact]
    public void DocKeysAreStablePerPathCaseInsensitiveAndDifferBetweenPaths()
    {
        var key = JournalKey.DocKey(@"C:\Users\a\Docs\Report.pdf");

        Assert.Equal(32, key.Length);
        Assert.Equal(key, JournalKey.DocKey(@"C:\Users\a\Docs\Report.pdf"));
        Assert.Equal(key, JournalKey.DocKey(@"c:\users\A\DOCS\report.PDF"));
        Assert.Equal(key, JournalKey.DocKey("C:/Users/a/Docs/Report.pdf"));
        Assert.NotEqual(key, JournalKey.DocKey(@"C:\Users\a\Docs\Report2.pdf"));
        Assert.Matches("^[0-9a-f]{32}$", key);
    }

    [Fact]
    public void TheFingerprintChangesWithTheStartTheEndOrTheSizeOfTheFile()
    {
        using var scratch = new ScratchDirectory();
        var data = new byte[300_000];
        new Random(5).NextBytes(data);
        var original = JournalKey.Fingerprint(scratch.Make("a.pdf", data));

        var head = (byte[])data.Clone();
        head[10]++;
        var tail = (byte[])data.Clone();
        tail[^10]++;
        Assert.NotEqual(original, JournalKey.Fingerprint(scratch.Make("head.pdf", head)));
        Assert.NotEqual(original, JournalKey.Fingerprint(scratch.Make("tail.pdf", tail)));
        Assert.NotEqual(original, JournalKey.Fingerprint(scratch.Make("longer.pdf", [.. data, 0])));
        Assert.Equal(original, JournalKey.Fingerprint(scratch.Make("same.pdf", data)));
        Assert.Equal(64, original.Length);
    }
}
