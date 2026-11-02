using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MikePDF.ProtocolGen;

public enum FieldType { U16, U32, U64, I32, F32, Bool, Text, Enum, Array }

public enum DocumentIdRule { None, Required }

public sealed record EnumValue(string Name, int Value, bool CarriesPayload);

public sealed record EnumDef(string Name, IReadOnlyList<EnumValue> Values);

/// <summary>One payload field. For arrays, Min/Max/ExclusiveMin/MaxBytes describe the elements.</summary>
public sealed record FieldDef(
    int Key,
    string Name,
    FieldType Type,
    FieldType? Element,
    string? EnumName,
    decimal? Min,
    decimal? Max,
    decimal? ExclusiveMin,
    int? MaxBytes,
    int? MaxCount,
    bool Optional,
    string? SameLengthAs,
    string? Doc)
{
    /// <summary>The scalar type that is actually checked and written: the element type for arrays.</summary>
    public FieldType Scalar => Type == FieldType.Array ? Element!.Value : Type;
}

public sealed record StructDef(string TypeName, IReadOnlyList<FieldDef> Fields);

public sealed record MessageDef(int Id, string Name, DocumentIdRule DocumentId, StructDef Request, StructDef Reply);

public sealed class SchemaException(IReadOnlyList<string> errors)
    : Exception("Invalid protocol schema:" + Environment.NewLine + string.Join(Environment.NewLine, errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>The parsed and validated protocol schema.</summary>
public sealed class Schema
{
    private static readonly Regex CamelName = new("^[a-z][A-Za-z0-9]*$", RegexOptions.CultureInvariant);
    private static readonly Regex PascalName = new("^[A-Z][A-Za-z0-9]*$", RegexOptions.CultureInvariant);

    public int SchemaVersion { get; private init; }

    public int MaxFrameBytes { get; private init; }

    public long MaxDeadlineMs { get; private init; }

    public IReadOnlyList<EnumDef> Enums { get; private init; } = [];

    public IReadOnlyList<MessageDef> Messages { get; private init; } = [];

    /// <summary>Largest envelope (header fields plus array headers) that can wrap a payload.</summary>
    public const int MaxEnvelopeBytes = 32;

    public static (long Min, decimal Max) Range(FieldType t) => t switch
    {
        FieldType.U16 => (0, ushort.MaxValue),
        FieldType.U32 => (0, uint.MaxValue),
        FieldType.U64 => (0, ulong.MaxValue),
        FieldType.I32 => (int.MinValue, int.MaxValue),
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    public static Schema Parse(string json)
    {
        var errors = new List<string>();
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow });
        var root = doc.RootElement;
        var enums = new List<EnumDef>();
        var messages = new List<MessageDef>();

        foreach (var e in root.GetProperty("enums").EnumerateArray())
        {
            var name = e.GetProperty("name").GetString()!;
            var values = e.GetProperty("values").EnumerateArray()
                .Select(v => new EnumValue(v.GetProperty("name").GetString()!, v.GetProperty("value").GetInt32(),
                    v.TryGetProperty("payload", out var p) && p.GetBoolean()))
                .ToList();
            enums.Add(new EnumDef(name, values));
        }

        foreach (var m in root.GetProperty("messages").EnumerateArray())
        {
            var name = m.GetProperty("name").GetString()!;
            var rule = m.GetProperty("documentId").GetString() switch
            {
                "none" => DocumentIdRule.None,
                "required" => DocumentIdRule.Required,
                var other => Fail(errors, $"{name}: documentId must be 'none' or 'required', not '{other}'", DocumentIdRule.None),
            };
            messages.Add(new MessageDef(m.GetProperty("id").GetInt32(), name, rule,
                ParseStruct(m.GetProperty("request"), errors), ParseStruct(m.GetProperty("reply"), errors)));
        }

        var env = root.GetProperty("envelope");
        var schema = new Schema
        {
            SchemaVersion = root.GetProperty("schemaVersion").GetInt32(),
            MaxFrameBytes = env.GetProperty("maxFrameBytes").GetInt32(),
            MaxDeadlineMs = env.GetProperty("maxDeadlineMs").GetInt64(),
            Enums = enums,
            Messages = messages,
        };
        schema.Check(errors);
        if (errors.Count > 0) throw new SchemaException(errors);
        return schema;
    }

    private static T Fail<T>(List<string> errors, string message, T fallback)
    {
        errors.Add(message);
        return fallback;
    }

    private static StructDef ParseStruct(JsonElement e, List<string> errors)
    {
        var typeName = e.GetProperty("type").GetString()!;
        var fields = new List<FieldDef>();
        foreach (var f in e.GetProperty("fields").EnumerateArray())
        {
            var name = f.GetProperty("name").GetString()!;
            var type = ParseType(f.GetProperty("type").GetString()!, errors, $"{typeName}.{name}");
            FieldType? element = f.TryGetProperty("of", out var of) ? ParseType(of.GetString()!, errors, $"{typeName}.{name}") : null;
            fields.Add(new FieldDef(
                f.GetProperty("key").GetInt32(), name, type, element,
                f.TryGetProperty("enum", out var en) ? en.GetString() : null,
                Num(f, "min"), Num(f, "max"), Num(f, "exclusiveMin"),
                f.TryGetProperty("maxBytes", out var mb) ? mb.GetInt32() : null,
                f.TryGetProperty("maxCount", out var mc) ? mc.GetInt32() : null,
                f.TryGetProperty("optional", out var opt) && opt.GetBoolean(),
                f.TryGetProperty("sameLengthAs", out var sl) ? sl.GetString() : null,
                f.TryGetProperty("doc", out var doc) ? doc.GetString() : null));
        }

        return new StructDef(typeName, fields);
    }

    private static decimal? Num(JsonElement f, string name) =>
        f.TryGetProperty(name, out var v) ? v.GetDecimal() : null;

    private static FieldType ParseType(string s, List<string> errors, string where) => s switch
    {
        "u16" => FieldType.U16,
        "u32" => FieldType.U32,
        "u64" => FieldType.U64,
        "i32" => FieldType.I32,
        "f32" => FieldType.F32,
        "bool" => FieldType.Bool,
        "string" => FieldType.Text,
        "enum" => FieldType.Enum,
        "array" => FieldType.Array,
        _ => Fail(errors, $"{where}: unknown type '{s}'", FieldType.Bool),
    };

    private void Check(List<string> errors)
    {
        if (SchemaVersion is < 1 or > 65535) errors.Add("schemaVersion must be 1..65535");
        if (MaxFrameBytes is < 64 or > 1 << 20) errors.Add("maxFrameBytes must be 64..1048576");

        var status = Enums.FirstOrDefault(e => e.Name == "Status");
        if (status is null || !status.Values.Any(v => v is { Name: "Ok", Value: 0 }))
            errors.Add("enum Status with Ok = 0 is required");
        foreach (var required in new[] { "InvalidArgument", "ProtocolError" })
            if (status is not null && !status.Values.Any(v => v.Name == required)) errors.Add($"Status.{required} is required");

        foreach (var e in Enums)
        {
            if (!PascalName.IsMatch(e.Name)) errors.Add($"enum {e.Name}: name must be PascalCase");
            if (e.Values.Count == 0) errors.Add($"enum {e.Name}: no values");
            foreach (var v in e.Values)
            {
                if (!PascalName.IsMatch(v.Name)) errors.Add($"enum {e.Name}.{v.Name}: name must be PascalCase");
                if (v.Value is < 0 or > ushort.MaxValue) errors.Add($"enum {e.Name}.{v.Name}: value must fit u16");
            }

            Unique(errors, $"enum {e.Name} names", e.Values.Select(v => v.Name));
            Unique(errors, $"enum {e.Name} values", e.Values.Select(v => v.Value));
        }

        Unique(errors, "message ids", Messages.Select(m => m.Id));
        Unique(errors, "message names", Messages.Select(m => m.Name));
        Unique(errors, "payload type names", Messages.SelectMany(m => new[] { m.Request.TypeName, m.Reply.TypeName }));

        foreach (var m in Messages)
        {
            if (m.Id is < 1 or > ushort.MaxValue) errors.Add($"{m.Name}: id must be 1..65535");
            if (!PascalName.IsMatch(m.Name)) errors.Add($"{m.Name}: name must be PascalCase");
            foreach (var s in new[] { m.Request, m.Reply })
            {
                CheckStruct(s, errors);
                var size = WorstCasePayloadBytes(s);
                if (size + MaxEnvelopeBytes > MaxFrameBytes)
                    errors.Add($"{s.TypeName}: worst-case encoding is {size} bytes plus the envelope, which exceeds maxFrameBytes {MaxFrameBytes}");
            }
        }
    }

    private static void Unique<T>(List<string> errors, string what, IEnumerable<T> items)
    {
        foreach (var dup in items.GroupBy(i => i).Where(g => g.Count() > 1))
            errors.Add($"duplicate {what}: {dup.Key}");
    }

    private void CheckStruct(StructDef s, List<string> errors)
    {
        if (!PascalName.IsMatch(s.TypeName)) errors.Add($"{s.TypeName}: type name must be PascalCase");
        for (var i = 0; i < s.Fields.Count; i++)
        {
            var f = s.Fields[i];
            var w = $"{s.TypeName}.{f.Name}";
            if (f.Key != i) errors.Add($"{w}: key {f.Key} must equal its position {i} (keys are contiguous array indices)");
            if (!CamelName.IsMatch(f.Name)) errors.Add($"{w}: name must be camelCase");
            if (s.Fields.Count(o => o.Name == f.Name) > 1) errors.Add($"{w}: duplicate field name");
            if (f.Optional && f.Type != FieldType.Text) errors.Add($"{w}: only strings can be optional");
            if (f.Type == FieldType.Array)
            {
                if (f.Element is null or FieldType.Array or FieldType.Bool or FieldType.Enum or FieldType.U64 or FieldType.I32)
                    errors.Add($"{w}: array element must be u16, u32, f32 or string");
                if (f.MaxCount is null or < 0) errors.Add($"{w}: array needs maxCount");
                if (f.Optional) errors.Add($"{w}: arrays cannot be optional");
            }
            else
            {
                if (f.Element is not null) errors.Add($"{w}: 'of' is only for arrays");
                if (f.MaxCount is not null) errors.Add($"{w}: maxCount is only for arrays");
                if (f.SameLengthAs is not null) errors.Add($"{w}: sameLengthAs is only for arrays");
            }

            if (f.Type == FieldType.Enum)
            {
                if (f.EnumName is null || Enums.All(e => e.Name != f.EnumName)) errors.Add($"{w}: unknown enum '{f.EnumName}'");
            }
            else if (f.EnumName is not null)
            {
                errors.Add($"{w}: 'enum' is only for enum fields");
            }

            var scalar = f.Type == FieldType.Array ? f.Element ?? FieldType.Bool : f.Type;
            if (scalar == FieldType.Text)
            {
                if (f.MaxBytes is null or < 0) errors.Add($"{w}: string needs maxBytes");
            }
            else if (f.MaxBytes is not null)
            {
                errors.Add($"{w}: maxBytes is only for strings");
            }

            var numeric = scalar is FieldType.U16 or FieldType.U32 or FieldType.U64 or FieldType.I32 or FieldType.F32;
            if (!numeric && (f.Min is not null || f.Max is not null || f.ExclusiveMin is not null))
                errors.Add($"{w}: min/max only apply to numbers");
            if (f.ExclusiveMin is not null && scalar != FieldType.F32) errors.Add($"{w}: exclusiveMin is only for f32");
            if (f.ExclusiveMin is not null && f.Min is not null) errors.Add($"{w}: min and exclusiveMin are exclusive");
            if (f.Min is not null && f.Max is not null && f.Min > f.Max) errors.Add($"{w}: min > max");
            if (scalar is FieldType.U16 or FieldType.U32 or FieldType.U64 or FieldType.I32)
            {
                var (lo, hi) = Range(scalar);
                if (f.Min < lo || f.Max > hi || (f.Min ?? 0) % 1 != 0 || (f.Max ?? 0) % 1 != 0)
                    errors.Add($"{w}: range exceeds {scalar} or is not integral");
            }

            if (scalar == FieldType.F32 && (f.Min is null && f.ExclusiveMin is null || f.Max is null))
                errors.Add($"{w}: f32 needs a finite range (min or exclusiveMin, and max)");

            if (f.SameLengthAs is not null)
            {
                var other = s.Fields.FirstOrDefault(o => o.Name == f.SameLengthAs);
                if (other is null || other.Type != FieldType.Array || other.Key >= f.Key)
                    errors.Add($"{w}: sameLengthAs must name an earlier array field");
            }
        }
    }

    // Worst-case encoded size of a payload, using the widest legal value of every field.
    internal static int WorstCasePayloadBytes(StructDef s) => ArrayHeader(s.Fields.Count) + s.Fields.Sum(WorstCaseFieldBytes);

    private static int ArrayHeader(long n) => n <= 15 ? 1 : n <= ushort.MaxValue ? 3 : 5;

    private static int WorstCaseFieldBytes(FieldDef f) => f.Type switch
    {
        FieldType.Array => ArrayHeader(f.MaxCount ?? 0) + (f.MaxCount ?? 0) * WorstCaseScalarBytes(f.Element!.Value, f),
        _ => WorstCaseScalarBytes(f.Type, f),
    };

    private static int WorstCaseScalarBytes(FieldType t, FieldDef f)
    {
        decimal max = f.Max ?? (t == FieldType.I32 ? int.MaxValue : t == FieldType.U16 ? ushort.MaxValue : t == FieldType.U32 ? uint.MaxValue : ulong.MaxValue);
        decimal min = f.Min ?? (t == FieldType.I32 ? int.MinValue : 0);
        return t switch
        {
            FieldType.Bool => 1,
            FieldType.F32 => 5,
            FieldType.Enum => 3,
            FieldType.U16 or FieldType.U32 or FieldType.U64 => UnsignedBytes(max),
            FieldType.I32 => Math.Max(UnsignedBytes(Math.Max(max, 0)), min >= -32 ? 1 : min >= -128 ? 2 : min >= -32768 ? 3 : 5),
            FieldType.Text => Math.Max(1, StringHeader(f.MaxBytes ?? 0) + (f.MaxBytes ?? 0)),
            _ => throw new InvalidOperationException(t.ToString()),
        };
    }

    private static int UnsignedBytes(decimal max) => max <= 127 ? 1 : max <= byte.MaxValue ? 2 : max <= ushort.MaxValue ? 3 : max <= uint.MaxValue ? 5 : 9;

    private static int StringHeader(long n) => n <= 31 ? 1 : n <= byte.MaxValue ? 2 : n <= ushort.MaxValue ? 3 : 5;

    // ---- naming helpers shared by the emitters -------------------------------------------------

    public static string Pascal(string camel) => char.ToUpperInvariant(camel[0]) + camel[1..];

    public static string Snake(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var prevLower = i > 0 && !char.IsUpper(name[i - 1]);
                var nextLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (i > 0 && (prevLower || (nextLower && char.IsUpper(name[i - 1])))) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    public static string Inv(decimal d) => d.ToString(CultureInfo.InvariantCulture);
}
