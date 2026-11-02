using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MikePDF.Core.Editing.Journal;

/// <summary>A record is longer than <see cref="JournalLimits.MaxRecordBytes"/>.</summary>
public sealed class JournalRecordTooLargeException(string message) : InvalidOperationException(message);

/// <summary>
/// The journal's line format (save-pipeline 3.4), the writer's half and the strict parser that reads it back.
/// <para>
/// A line is UTF-8 JSON, ASCII only (non-ASCII is escaped), ending in LF. It ends with <c>,"crc":"hhhhhhhh"}</c> where the eight lower-case hex
/// digits are the CRC-32C of every byte before that suffix, that is, of the record text without its <c>crc</c> field and without the closing
/// brace. The reader never trusts a line it cannot check: length, checksum, JSON syntax and depth, duplicate keys, every field's type and range,
/// and the sequence number (1, 2, 3, ...) are verified, and reading stops at the first record that fails.
/// </para>
/// </summary>
public static class JournalCodec
{
    public const int Version = 1;
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    private static readonly byte[] CrcMarker = ",\"crc\":\""u8.ToArray();
    private const int SuffixLength = 8 + 8 + 2; // ,"crc":"  + hex + "}

    public static byte[] EncodeHeader(JournalHeader header, JournalLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        limits ??= JournalLimits.Default;
        return Finish(limits.MaxHeaderBytes, writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            writer.WriteString("type", "header");
            writer.WriteString("docPath", header.DocPath);
            if (header.FileId is not null)
            {
                writer.WriteString("fileId", header.FileId);
            }

            writer.WriteNumber("size", header.Size);
            if (header.Modified is not null)
            {
                writer.WriteString("mtime", header.Modified);
            }

            writer.WriteString("fingerprint", header.Fingerprint);
            writer.WriteString("app", header.App);
            if (header.Pages.Count > 0)
            {
                writer.WriteStartArray("pages");
                foreach (var page in header.Pages)
                {
                    writer.WriteStringValue(page);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        });
    }

    public static byte[] EncodeEntry(JournalEntry entry, JournalLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        limits ??= JournalLimits.Default;
        return Finish(limits.MaxRecordBytes, writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            writer.WriteNumber("seq", entry.Seq);
            writer.WriteString("t", entry.Time.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture));
            switch (entry)
            {
                case CommandEntry command:
                    writer.WriteString("cmd", command.Command.Cmd);
                    foreach (var property in command.Command.Payload.EnumerateObject())
                    {
                        property.WriteTo(writer);
                    }

                    break;
                case UndoEntry undo:
                    writer.WriteString("cmd", EditCommandCatalog.Undo);
                    writer.WriteNumber("target", undo.Target);
                    break;
                case RedoEntry redo:
                    writer.WriteString("cmd", EditCommandCatalog.Redo);
                    writer.WriteNumber("target", redo.Target);
                    break;
                case SaveIntentEntry intent:
                    writer.WriteString("cmd", EditCommandCatalog.SaveIntent);
                    writer.WriteString("temp", intent.Temp);
                    if (intent.Backup is not null)
                    {
                        writer.WriteString("backup", intent.Backup);
                    }

                    writer.WriteString("saveTarget", intent.Target);
                    break;
                case SaveBeginEntry begin:
                    writer.WriteString("cmd", EditCommandCatalog.SaveBegin);
                    writer.WriteString("outputSha256", begin.OutputSha256);
                    writer.WriteNumber("outputLength", begin.OutputLength);
                    break;
                case SaveCommittedEntry:
                    writer.WriteString("cmd", EditCommandCatalog.SaveCommitted);
                    break;
                case DiscardEntry:
                    writer.WriteString("cmd", EditCommandCatalog.Discard);
                    break;
                default:
                    throw new ArgumentException("An unknown record cannot be written.", nameof(entry));
            }

            writer.WriteEndObject();
        });
    }

    private static byte[] Finish(int maxBytes, Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        // Drop the closing brace: the checksum covers everything before the suffix.
        var body = buffer.WrittenSpan[..^1];
        if (body.Length + SuffixLength + 1 > maxBytes)
        {
            throw new JournalRecordTooLargeException("A journal record is over its size limit.");
        }

        var line = new byte[body.Length + SuffixLength + 1];
        body.CopyTo(line);
        var crc = Crc32C.Compute(body);
        CrcMarker.CopyTo(line, body.Length);
        var hex = crc.ToString("x8", CultureInfo.InvariantCulture);
        Encoding.ASCII.GetBytes(hex, line.AsSpan(body.Length + CrcMarker.Length));
        line[^3] = (byte)'"';
        line[^2] = (byte)'}';
        line[^1] = (byte)'\n';
        return line;
    }

    /// <summary>Reads a whole journal. Never throws for bad content; the result says what was readable and why reading stopped.</summary>
    public static JournalReadResult Parse(ReadOnlySpan<byte> file, JournalLimits? limits = null)
    {
        limits ??= JournalLimits.Default;
        if (file.Length == 0)
        {
            return new JournalReadResult(JournalReadStatus.Empty, null, [], 0, 0, null);
        }

        if (file.Length > limits.MaxDocumentBytes + limits.ControlReserveBytes)
        {
            return new JournalReadResult(JournalReadStatus.TooLarge, null, [], 0, file.Length, "The journal is over its size limit.");
        }

        var position = 0;
        var firstLine = NextLine(file, ref position, out var firstHasEnd);
        if (!TryParseHeader(firstLine, limits, out var header, out var headerProblem))
        {
            // A header that is a torn first line is an empty journal in effect: nothing was ever acknowledged.
            var tornHeader = !firstHasEnd;
            return new JournalReadResult(tornHeader ? JournalReadStatus.Empty : JournalReadStatus.Unreadable, null, [], 0, file.Length, tornHeader ? null : headerProblem);
        }

        var entries = new List<JournalEntry>();
        var unknown = 0;
        long expected = 1;
        long total = file.Length;
        while (position < file.Length)
        {
            var lineStart = position;
            var line = NextLine(file, ref position, out var hasEnd);
            if (entries.Count >= limits.MaxRecords)
            {
                return Stop(JournalReadStatus.TooLarge, "The journal has too many records.");
            }

            if (!TryParseEntry(line, expected, limits, out var entry, out var problem))
            {
                // A line cut off by the crash has no line end and is the last thing in the file: normal, silent.
                return !hasEnd && position >= file.Length ? Stop(JournalReadStatus.TornTail, null) : Stop(JournalReadStatus.Corrupt, problem);
            }

            if (entry is UnknownEntry)
            {
                unknown++;
            }

            entries.Add(entry);
            expected++;

            JournalReadResult Stop(JournalReadStatus status, string? why) => new(status, header, entries, unknown, total - lineStart, why);
        }

        return new JournalReadResult(JournalReadStatus.Ok, header, entries, unknown, 0, null);
    }

    private static ReadOnlySpan<byte> NextLine(ReadOnlySpan<byte> file, ref int position, out bool hasEnd)
    {
        var rest = file[position..];
        var end = rest.IndexOf((byte)'\n');
        if (end < 0)
        {
            position = file.Length;
            hasEnd = false;
            return rest;
        }

        position += end + 1;
        hasEnd = true;
        return rest[..end];
    }

    private static bool TryParseHeader(ReadOnlySpan<byte> line, JournalLimits limits, out JournalHeader header, out string problem)
    {
        header = null!;
        if (!TryOpen(line, limits.MaxHeaderBytes, limits, out var document, out problem))
        {
            return false;
        }

        using (document)
        {
            try
            {
                var root = document.RootElement;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                string? docPath = null, fileId = null, mtime = null, fingerprint = null, app = null;
                long? size = null;
                var pages = new List<string>();
                var sawType = false;
                var sawVersion = false;
                foreach (var property in root.EnumerateObject())
                {
                    seen.Add(property.Name);
                    switch (property.Name)
                    {
                        case "v":
                            sawVersion = property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var v) && v == Version;
                            break;
                        case "type":
                            sawType = property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == "header";
                            break;
                        case "docPath":
                            docPath = Text(property.Value, limits.MaxPathChars);
                            break;
                        case "fileId":
                            fileId = Text(property.Value, 128);
                            break;
                        case "size":
                            size = property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var s) && s >= 0 ? s : null;
                            break;
                        case "mtime":
                            mtime = Text(property.Value, 64);
                            break;
                        case "fingerprint":
                            fingerprint = IsHex(property.Value, 64);
                            break;
                        case "app":
                            app = Text(property.Value, 64);
                            break;
                        case "pages":
                            if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() > limits.MaxPageIdentities)
                            {
                                problem = "The page list is invalid.";
                                return false;
                            }

                            foreach (var page in property.Value.EnumerateArray())
                            {
                                pages.Add(Text(page, 128) ?? throw new FormatException("page identity"));
                            }

                            break;
                        default:
                            problem = "The header has an unknown field.";
                            return false;
                    }
                }

                if (!sawVersion || !sawType || string.IsNullOrEmpty(docPath) || size is null || fingerprint is null || string.IsNullOrEmpty(app))
                {
                    problem = "The header is incomplete or from another version.";
                    return false;
                }

                header = new JournalHeader(docPath, fileId, size.Value, mtime, fingerprint, app, pages);
                problem = string.Empty;
                return true;
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException or OverflowException)
            {
                problem = "The header is invalid.";
                return false;
            }
        }
    }

    private static bool TryParseEntry(ReadOnlySpan<byte> line, long expectedSeq, JournalLimits limits, out JournalEntry entry, out string problem)
    {
        entry = null!;
        if (!TryOpen(line, limits.MaxRecordBytes, limits, out var document, out problem))
        {
            return false;
        }

        using (document)
        {
            try
            {
                var root = document.RootElement;
                long? seq = null;
                DateTimeOffset? time = null;
                string? cmd = null;
                var sawVersion = false;
                foreach (var property in root.EnumerateObject())
                {
                    switch (property.Name)
                    {
                        case "v":
                            sawVersion = property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var v) && v == Version;
                            break;
                        case "seq":
                            seq = property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var s) ? s : null;
                            break;
                        case "t":
                            time = property.Value.ValueKind == JsonValueKind.String
                                && DateTimeOffset.TryParseExact(property.Value.GetString(), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
                                ? t : null;
                            break;
                        case "cmd":
                            cmd = Text(property.Value, EditCommand.MaxCommandNameLength);
                            break;
                    }
                }

                if (!sawVersion || seq is null || time is null || string.IsNullOrEmpty(cmd))
                {
                    problem = "A record has no valid envelope.";
                    return false;
                }

                if (seq.Value != expectedSeq)
                {
                    problem = "The sequence numbers are not continuous.";
                    return false;
                }

                if (!TryBuildEntry(root, cmd, seq.Value, time.Value, limits, out var built))
                {
                    problem = "A record's fields are invalid.";
                    return false;
                }

                entry = built!;
                problem = string.Empty;
                return true;
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException or OverflowException)
            {
                problem = "A record is invalid.";
                return false;
            }
        }
    }

    private static bool TryBuildEntry(JsonElement root, string cmd, long seq, DateTimeOffset time, JournalLimits limits, out JournalEntry? entry)
    {
        entry = null;
        switch (cmd)
        {
            case EditCommandCatalog.Undo or EditCommandCatalog.Redo:
                if (!OnlyFields(root, "v", "seq", "t", "cmd", "target") || !root.TryGetProperty("target", out var target)
                    || target.ValueKind != JsonValueKind.Number || !target.TryGetInt64(out var targetSeq) || targetSeq < 1 || targetSeq >= seq)
                {
                    return false;
                }

                entry = cmd == EditCommandCatalog.Undo ? new UndoEntry(seq, time, targetSeq) : new RedoEntry(seq, time, targetSeq);
                return true;
            case EditCommandCatalog.SaveIntent:
                if (!OnlyFields(root, "v", "seq", "t", "cmd", "temp", "backup", "saveTarget"))
                {
                    return false;
                }

                var temp = root.TryGetProperty("temp", out var tempValue) ? Text(tempValue, limits.MaxPathChars) : null;
                var saveTarget = root.TryGetProperty("saveTarget", out var targetValue) ? Text(targetValue, limits.MaxPathChars) : null;
                var backup = root.TryGetProperty("backup", out var backupValue) ? Text(backupValue, limits.MaxPathChars) : null;
                if (string.IsNullOrEmpty(temp) || string.IsNullOrEmpty(saveTarget) || (root.TryGetProperty("backup", out _) && string.IsNullOrEmpty(backup)))
                {
                    return false;
                }

                entry = new SaveIntentEntry(seq, time, temp, backup, saveTarget);
                return true;
            case EditCommandCatalog.SaveBegin:
                if (!OnlyFields(root, "v", "seq", "t", "cmd", "outputSha256", "outputLength") || !root.TryGetProperty("outputSha256", out var hash)
                    || IsHex(hash, 64) is not { } sha || !root.TryGetProperty("outputLength", out var length)
                    || length.ValueKind != JsonValueKind.Number || !length.TryGetInt64(out var outputLength) || outputLength < 0)
                {
                    return false;
                }

                entry = new SaveBeginEntry(seq, time, sha, outputLength);
                return true;
            case EditCommandCatalog.SaveCommitted:
                if (!OnlyFields(root, "v", "seq", "t", "cmd"))
                {
                    return false;
                }

                entry = new SaveCommittedEntry(seq, time);
                return true;
            case EditCommandCatalog.Discard:
                if (!OnlyFields(root, "v", "seq", "t", "cmd"))
                {
                    return false;
                }

                entry = new DiscardEntry(seq, time);
                return true;
            default:
                if (!EditCommandCatalog.IsEditCommand(cmd))
                {
                    // Valid envelope and checksum; a command this version does not know. Its fields are not interpreted.
                    entry = new UnknownEntry(seq, time, cmd);
                    return true;
                }

                var fields = new ArrayBufferWriter<byte>(256);
                using (var writer = new Utf8JsonWriter(fields))
                {
                    writer.WriteStartObject();
                    foreach (var property in root.EnumerateObject())
                    {
                        if (!EditCommandCatalog.ReservedFields.Contains(property.Name))
                        {
                            property.WriteTo(writer);
                        }
                    }

                    writer.WriteEndObject();
                }

                using (var payload = JsonDocument.Parse(fields.WrittenMemory))
                {
                    entry = new CommandEntry(seq, time, new EditCommand(cmd, payload.RootElement));
                }

                return true;
        }
    }

    private static bool OnlyFields(JsonElement root, params string[] allowed)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (Array.IndexOf(allowed, property.Name) < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Checks the suffix and checksum, then parses the JSON with the depth and element limits and rejects duplicate keys.</summary>
    private static bool TryOpen(ReadOnlySpan<byte> line, int maxBytes, JournalLimits limits, out JsonDocument document, out string problem)
    {
        document = null!;
        problem = "A record has no valid checksum.";
        if (line.Length > maxBytes)
        {
            problem = "A record is over its size limit.";
            return false;
        }

        if (line.Length < SuffixLength + 2 || line[^2] != (byte)'"' || line[^1] != (byte)'}')
        {
            return false;
        }

        var markerAt = line.Length - SuffixLength;
        if (!line.Slice(markerAt, CrcMarker.Length).SequenceEqual(CrcMarker))
        {
            return false;
        }

        var hex = line.Slice(markerAt + CrcMarker.Length, 8);
        uint stated = 0;
        foreach (var digit in hex)
        {
            int value = digit switch
            {
                >= (byte)'0' and <= (byte)'9' => digit - '0',
                >= (byte)'a' and <= (byte)'f' => digit - 'a' + 10,
                _ => -1,
            };
            if (value < 0)
            {
                return false;
            }

            stated = (stated << 4) | (uint)value;
        }

        var body = line[..markerAt];
        if (Crc32C.Compute(body) != stated)
        {
            return false;
        }

        var json = new byte[body.Length + 1];
        body.CopyTo(json);
        json[^1] = (byte)'}';
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = limits.MaxJsonDepth, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException)
        {
            problem = "A record is not valid JSON.";
            return false;
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            problem = "A record is not a JSON object.";
            return false;
        }

        var budget = limits.MaxJsonElements;
        if (!WalkIsStrict(document.RootElement, ref budget))
        {
            document.Dispose();
            problem = "A record has a duplicate key or too many elements.";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    private static bool WalkIsStrict(JsonElement element, ref int budget)
    {
        if (--budget < 0)
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name) || !WalkIsStrict(property.Value, ref budget))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (!WalkIsStrict(item, ref budget))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return true;
        }
    }

    private static string? Text(JsonElement value, int maxChars)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return text is null || text.Length > maxChars || text.Contains('\0', StringComparison.Ordinal) ? null : text;
    }

    private static string? IsHex(JsonElement value, int length)
    {
        var text = Text(value, length);
        return text is { } t && t.Length == length && t.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')) ? t : null;
    }
}
