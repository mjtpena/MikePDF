using System.Text;
using MikePDF.Core.Editing;
using MikePDF.Core.Editing.Journal;

namespace MikePDF.Core.Tests;

/// <summary>SP-04 and SP-08 (save-pipeline 14): the journal's durability order, its failure policy, its limits and what it may store.</summary>
public sealed class JournalWriterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 10, 30, 0, 123, TimeSpan.Zero);

    [Fact]
    public void EveryCommandIsWrittenAndFlushedBeforeItIsAcknowledged()
    {
        var files = new RecordingJournalFiles();
        var writer = JournalWriter.Create(files, "j.jsonl", JournalSamples.Header());
        using var session = new EditSession(writer);
        files.Log.Clear();

        session.Model.Apply(Edits.Add(name: "a"));
        var afterAck = files.Log.ToList();
        session.Model.Apply(Edits.Add(name: "b"));
        session.Model.Undo();

        Assert.Equal(["append", "flush"], afterAck);
        Assert.Equal(["append", "flush", "append", "flush", "append", "flush"], files.Log);
        var read = JournalCodec.Parse(files.Content.ToArray());
        Assert.Equal(JournalReadStatus.Ok, read.Status);
        Assert.Equal(3, read.ValidRecords);
        Assert.IsType<UndoEntry>(read.Entries[2]);
    }

    [Fact]
    public void TheHeaderIsFlushedWhenTheJournalIsCreated()
    {
        var files = new RecordingJournalFiles();

        using var writer = JournalWriter.Create(files, "j.jsonl", JournalSamples.Header());

        Assert.Equal(["create", "append", "flush"], files.Log);
    }

    [Fact]
    public void AFailedFlushInDegradedModeKeepsEditingAndSaysTheChangesAreNotBackedUp()
    {
        var files = new RecordingJournalFiles();
        using var session = new EditSession(JournalWriter.Create(files, "j.jsonl", JournalSamples.Header()), JournalFailureMode.Degraded);
        files.FailFlush = true;

        session.Model.Apply(Edits.Add());

        Assert.False(session.BackedUp);
        Assert.True(session.Model.IsDirty);
        Assert.Equal(JournalHealth.Unavailable, session.Journal!.Health);

        files.FailFlush = false;
        session.Model.Apply(Edits.Add(name: "b"));
        Assert.Equal(2, session.Model.PendingCount); // still editing, and no record is written after the failed one
    }

    [Fact]
    public void AFailedFlushInStrictModeRefusesTheCommand()
    {
        var files = new RecordingJournalFiles();
        using var session = new EditSession(JournalWriter.Create(files, "j.jsonl", JournalSamples.Header()), JournalFailureMode.Strict);
        files.FailFlush = true;

        Assert.Throws<JournalUnavailableException>(() => session.Model.Apply(Edits.Add()));

        Assert.False(session.Model.IsDirty);
    }

    [Fact]
    public void TheDocumentLimitWarnsAtNinetyPercentThenStopsCommandsButKeepsRoomForSaveRecords()
    {
        var files = new RecordingJournalFiles();
        var limits = new JournalLimits { MaxDocumentBytes = 2_000, ControlReserveBytes = 400 };
        var writer = JournalWriter.Create(files, "j.jsonl", JournalSamples.Header(), limits);
        var changes = 0;
        writer.HealthChanged += (_, _) => changes++;
        var states = new List<JournalHealth>();

        for (var i = 0; i < 40; i++)
        {
            try
            {
                writer.Command(Edits.Add(name: "n" + i));
                states.Add(writer.Health);
            }
            catch (JournalUnavailableException)
            {
                break;
            }
        }

        Assert.Contains(JournalHealth.NearLimit, states);
        Assert.Equal(JournalHealth.Unavailable, writer.Health);
        Assert.Equal(2, changes);
        Assert.True(files.Content.Length <= 2_000);
        Assert.Throws<JournalUnavailableException>(() => writer.Command(Edits.Add(name: "late")) );
    }

    [Fact]
    public void SaveControlRecordsStillFitInTheReserveWhenCommandsNoLongerDo()
    {
        var files = new RecordingJournalFiles();
        var limits = new JournalLimits { MaxDocumentBytes = 1_500, ControlReserveBytes = 600 };
        var writer = JournalWriter.Create(files, "j.jsonl", JournalSamples.Header(), limits);
        while (true)
        {
            try
            {
                writer.Command(Edits.Add(name: "n" + writer.LastSequence));
            }
            catch (JournalUnavailableException)
            {
                break;
            }
        }

        // The journal is "full" for commands, but a save must still be able to say what it is about to create.
        var seq = writer.SaveIntent(@"C:\x\.a.pdf.mikepdf-00000000.tmp", null, @"C:\x\a.pdf");

        Assert.True(seq > 0);
        var read = JournalCodec.Parse(files.Content.ToArray(), limits);
        Assert.Equal(JournalReadStatus.Ok, read.Status);
        Assert.IsType<SaveIntentEntry>(read.Entries[^1]);
    }

    [Fact]
    public void TheTotalOfAllJournalsIsCappedToo()
    {
        var budget = new JournalBudget(1_200);
        var first = JournalWriter.Create(new RecordingJournalFiles(), "a", JournalSamples.Header(), budget: budget);
        var second = JournalWriter.Create(new RecordingJournalFiles(), "b", JournalSamples.Header(), budget: budget);

        Assert.Throws<JournalUnavailableException>(() =>
        {
            for (var i = 0; i < 30; i++)
            {
                first.Command(Edits.Add(name: "n" + i));
                second.Command(Edits.Add(name: "m" + i));
            }
        });
        Assert.True(budget.Used <= 1_200);
    }

    [Fact]
    public void PasswordValuesAreNeverJournalledButTheModelKeepsThem()
    {
        var files = new RecordingJournalFiles();
        using var session = new EditSession(JournalWriter.Create(files, "j.jsonl", JournalSamples.Header()));

        session.Model.Apply(EditCommand.Create("SetFieldValue", new { field = "pin", fieldType = "password", value = "hunter2-SECRET" }));
        session.Model.Apply(EditCommand.Create("SetFieldValue", new { field = "pw2", password = true, value = "other-SECRET" }));
        session.Model.Apply(EditCommand.Create("SetFieldValue", new { field = "name", value = "Ada Lovelace" }));

        var bytes = files.Content.ToArray();
        Assert.False(JournalPrivacy.ContainsText(bytes, "SECRET"));
        Assert.True(JournalPrivacy.ContainsText(bytes, "Ada Lovelace"));
        Assert.True(JournalPrivacy.ContainsText(bytes, "\"field\":\"pin\""));
        Assert.Equal("hunter2-SECRET", session.Model.PendingCommands[0].Payload.GetProperty("value").GetString()); // it is saved into the file
    }

    [Fact]
    public void ExcerptsOfDocumentTextAreCappedAtTwoHundredCharactersAndNothingElseOfThePageIsStored()
    {
        var files = new RecordingJournalFiles();
        using var session = new EditSession(JournalWriter.Create(files, "j.jsonl", JournalSamples.Header()));
        var pageText = string.Join(' ', Enumerable.Repeat("BODYTEXT-SENTINEL", 100));

        session.Model.Apply(EditCommand.Create("AddAnnotation", new { page = 3, kind = "Highlight", contents = "my note", excerpt = pageText }));

        var read = JournalCodec.Parse(files.Content.ToArray());
        var stored = Assert.IsType<CommandEntry>(read.Entries[0]).Command.Payload.GetProperty("excerpt").GetString()!;
        Assert.Equal(JournalPrivacy.MaxExcerptChars, stored.Length);
        Assert.StartsWith("BODYTEXT-SENTINEL", stored, StringComparison.Ordinal);
        var occurrences = Encoding.UTF8.GetString(files.Content.ToArray()).Split("BODYTEXT-SENTINEL").Length - 1;
        Assert.True(occurrences <= 12, "only the capped excerpt may carry document text");
    }

    [Fact]
    public void AnExcerptIsNotCutInTheMiddleOfASurrogatePair()
    {
        var text = new string('a', 199) + "\U0001F600" + "tail";

        var capped = JournalPrivacy.Cap(text, 200);

        Assert.Equal(199, capped.Length);
    }
}
