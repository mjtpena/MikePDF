using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MikePDF.Markup.Editing;

/// <summary>
/// The fields of a command, built without reflection (the command payloads are JSON objects; a trimmed or Native AOT build cannot serialise a type nobody names).
/// A field is a string, a number, a bool, an array of numbers or strings, an array of those, another <see cref="CommandFields"/>, or the JSON a command already holds.
/// </summary>
public sealed class CommandFields : IEnumerable<KeyValuePair<string, JsonNode?>>
{
    private readonly JsonObject _fields = [];

    public object? this[string name]
    {
        set => _fields[name] = ToNode(value);
    }

    public bool Contains(string name) => _fields.ContainsKey(name);

    public bool Remove(string name) => _fields.Remove(name);

    /// <summary>The fields as JSON text.</summary>
    public string ToJson() => _fields.ToJsonString();

    public JsonElement ToElement()
    {
        using var document = JsonDocument.Parse(ToJson());
        return document.RootElement.Clone();
    }

    public IEnumerator<KeyValuePair<string, JsonNode?>> GetEnumerator() => _fields.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Add(string name, object? value) => this[name] = value;

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        uint u => JsonValue.Create(u),
        long l => JsonValue.Create(l),
        float f => JsonValue.Create(f),
        double d => JsonValue.Create(d),
        CommandFields nested => JsonNode.Parse(nested.ToJson()),
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        double[] numbers => Array(numbers.Select(static n => (JsonNode?)JsonValue.Create(n))),
        float[] numbers => Array(numbers.Select(static n => (JsonNode?)JsonValue.Create(n))),
        IEnumerable<string> strings => Array(strings.Select(static s => (JsonNode?)JsonValue.Create(s))),
        IEnumerable<float[]> lists => Array(lists.Select(static l => ToNode(l))),
        IEnumerable<CommandFields> items => Array(items.Select(static c => ToNode(c))),
        _ => throw new ArgumentException("A command field is a string, a number, a bool, an array of those, or nested fields.", nameof(value)),
    };

    private static JsonArray Array(IEnumerable<JsonNode?> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add(item);
        }

        return array;
    }
}
