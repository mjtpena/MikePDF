using System.Buffers;
using System.Text;
using System.Text.Json;

namespace MikePDF.Core.Editing.Journal;

/// <summary>What happens to a command when the journal cannot take it (save-pipeline OQ-SP-1).</summary>
public enum JournalFailureMode
{
    /// <summary>The command applies and the chip says changes can't be backed up. The proposed default.</summary>
    Degraded,

    /// <summary>The command is refused. What "flushed before the command is acknowledged" says literally.</summary>
    Strict,
}

/// <summary>What the journal is allowed to keep of a command (save-pipeline 3.4 "Privacy").</summary>
public static class JournalPrivacy
{
    public const int MaxExcerptChars = 200;

    /// <summary>
    /// The copy of a command that goes into the journal: a field the command marks as a password has no value, and an <c>excerpt</c> of document
    /// text (the recovery summary shows it) is cut to 200 characters. Everything else is what the user added and is kept as it is.
    /// </summary>
    public static EditCommand ForJournal(EditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var payload = command.Payload;
        var isPassword = (payload.TryGetProperty("password", out var flag) && flag.ValueKind == JsonValueKind.True)
            || (payload.TryGetProperty("fieldType", out var type) && type.ValueKind == JsonValueKind.String && string.Equals(type.GetString(), "password", StringComparison.OrdinalIgnoreCase));
        var needsExcerptCap = payload.TryGetProperty("excerpt", out var excerpt) && excerpt.ValueKind == JsonValueKind.String && (excerpt.GetString()?.Length ?? 0) > MaxExcerptChars;
        if (!isPassword && !needsExcerptCap)
        {
            return command;
        }

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in payload.EnumerateObject())
            {
                if (isPassword && property.Name is "value" or "values")
                {
                    continue;
                }

                if (property.Name == "excerpt" && property.Value.ValueKind == JsonValueKind.String)
                {
                    writer.WriteString("excerpt", Cap(property.Value.GetString()!, MaxExcerptChars));
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return new EditCommand(command.Cmd, document.RootElement);
    }

    /// <summary>At most <paramref name="max"/> UTF-16 units, never splitting a surrogate pair.</summary>
    public static string Cap(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var end = max;
        if (char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        return text[..end];
    }

    /// <summary>The bytes of a journal never hold a field that must not be there: used by the tests and the privacy review.</summary>
    public static bool ContainsText(ReadOnlySpan<byte> journal, string text) => journal.IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;
}

/// <summary>
/// One document's edits: the <see cref="EditModel"/>, its journal and the failure policy. <see cref="EditModel"/> asks this for the journal, so
/// a command reaches the model only after its record is on disk (save-pipeline 3.4, 3.7).
/// </summary>
public sealed class EditSession : IEditJournal, IDisposable
{
    private long _localSeq;

    public EditSession(JournalWriter? journal, JournalFailureMode mode = JournalFailureMode.Degraded)
    {
        Journal = journal;
        FailureMode = mode;
        _localSeq = journal?.LastSequence ?? 0;
        Model = new EditModel(this);
    }

    public EditModel Model { get; }

    public JournalWriter? Journal { get; }

    public JournalFailureMode FailureMode { get; }

    /// <summary>False once a command could not be journalled in <see cref="JournalFailureMode.Degraded"/> mode: "Changes can't be backed up right now."</summary>
    public bool BackedUp { get; private set; } = true;

    public long Command(EditCommand command)
    {
        if (Journal is not null && BackedUp)
        {
            try
            {
                var seq = Journal.Command(JournalPrivacy.ForJournal(command));
                _localSeq = seq;
                return seq;
            }
            catch (JournalUnavailableException) when (FailureMode == JournalFailureMode.Degraded)
            {
                BackedUp = false;
            }
        }

        return ++_localSeq;
    }

    public void Undo(long target) => Record(j => j.Undo(target));

    public void Redo(long target) => Record(j => j.Redo(target));

    private void Record(Action<JournalWriter> write)
    {
        if (Journal is null || !BackedUp)
        {
            return;
        }

        try
        {
            write(Journal);
        }
        catch (JournalUnavailableException) when (FailureMode == JournalFailureMode.Degraded)
        {
            BackedUp = false;
        }
    }

    public void Dispose() => Journal?.Dispose();
}
