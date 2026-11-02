using System.Text.Json;

namespace MikePDF.Core.Editing;

/// <summary>What kind of change a command makes. The save planner picks the stages from the union (save-pipeline 3.1).</summary>
[Flags]
public enum EditKinds
{
    None = 0,
    Annotations = 1,
    Forms = 2,
    Pages = 4,
}

/// <summary>
/// The commands the journal knows (save-pipeline 3.4: "Command records are defined by each feature spec") and the kind of each. A command name
/// outside this list is "unknown": the journal reader skips it and counts it, and the model refuses to apply it. A feature work package adds its
/// commands here, in one place, so the planner and the reader agree.
/// </summary>
public static class EditCommandCatalog
{
    public const string Undo = "Undo";
    public const string Redo = "Redo";
    public const string SaveIntent = "SaveIntent";
    public const string SaveBegin = "SaveBegin";
    public const string SaveCommitted = "SaveCommitted";
    public const string Discard = "Discard";

    private static readonly Dictionary<string, EditKinds> Commands = new(StringComparer.Ordinal)
    {
        ["AddAnnotation"] = EditKinds.Annotations,
        ["UpdateAnnotation"] = EditKinds.Annotations,
        ["MoveAnnotation"] = EditKinds.Annotations,
        ["ResizeAnnotation"] = EditKinds.Annotations,
        ["DeleteAnnotation"] = EditKinds.Annotations,
        ["AddReply"] = EditKinds.Annotations,
        ["SetReviewState"] = EditKinds.Annotations,
        ["AddInkStroke"] = EditKinds.Annotations,
        ["EraseInk"] = EditKinds.Annotations,
        ["SetFieldValue"] = EditKinds.Forms,
        ["ResetField"] = EditKinds.Forms,
        ["ClearForm"] = EditKinds.Forms,
        ["InsertPages"] = EditKinds.Pages,
        ["InsertBlankPage"] = EditKinds.Pages,
        ["DeletePages"] = EditKinds.Pages,
        ["MovePages"] = EditKinds.Pages,
        ["RotatePages"] = EditKinds.Pages,
    };

    private static readonly HashSet<string> Controls = new(StringComparer.Ordinal) { Undo, Redo, SaveIntent, SaveBegin, SaveCommitted, Discard };

    /// <summary>Envelope names a command's own fields may not use: they would collide with the record around them.</summary>
    internal static readonly HashSet<string> ReservedFields = new(StringComparer.Ordinal) { "v", "seq", "t", "cmd", "crc", "type", "target" };

    public static bool IsEditCommand(string cmd) => Commands.ContainsKey(cmd);

    public static bool IsControl(string cmd) => Controls.Contains(cmd);

    public static EditKinds KindOf(string cmd) => Commands.TryGetValue(cmd, out var kind) ? kind : EditKinds.None;

    public static IReadOnlyCollection<string> EditCommands => Commands.Keys;
}

/// <summary>
/// One user edit, as the journal stores it: a name from <see cref="EditCommandCatalog"/> and a flat JSON object of its fields. The feature
/// specs own the fields; this layer owns only the envelope, the size limits and the rule that a field may not shadow the envelope.
/// </summary>
public sealed class EditCommand
{
    public const int MaxCommandNameLength = 64;

    public EditCommand(string cmd, JsonElement payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(cmd);
        if (cmd.Length > MaxCommandNameLength || !cmd.All(static c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')))
        {
            throw new ArgumentException("A command name is 1 to 64 ASCII letters and digits.", nameof(cmd));
        }

        if (!EditCommandCatalog.IsEditCommand(cmd))
        {
            throw new ArgumentException($"'{cmd}' is not a command of the edit catalog.", nameof(cmd));
        }

        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("A command's fields are a JSON object.", nameof(payload));
        }

        foreach (var property in payload.EnumerateObject())
        {
            if (EditCommandCatalog.ReservedFields.Contains(property.Name))
            {
                throw new ArgumentException($"A command may not have a field called '{property.Name}'.", nameof(payload));
            }
        }

        Cmd = cmd;
        Payload = payload.Clone();
    }

    public string Cmd { get; }

    public JsonElement Payload { get; }

    public EditKinds Kind => EditCommandCatalog.KindOf(Cmd);

    /// <summary>Builds a command from any serialisable object of named fields (an anonymous object is the usual caller).</summary>
    public static EditCommand Create(string cmd, object fields) => new(cmd, JsonSerializer.SerializeToElement(fields));

    public override string ToString() => Cmd + " " + Payload.GetRawText();
}
