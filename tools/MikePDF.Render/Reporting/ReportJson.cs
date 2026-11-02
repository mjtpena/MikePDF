using System.Text.Json;
using System.Text.Json.Serialization;

namespace MikePDF.Render.Reporting;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RunReport))]
[JsonSerializable(typeof(SsimBaseline))]
internal sealed partial class ReportJson : JsonSerializerContext
{
    public static string Serialize(RunReport report) => JsonSerializer.Serialize(report, Default.RunReport);
}
