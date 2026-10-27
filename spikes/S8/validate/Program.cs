// S8.2 CLI: S8.Validate <pdf|dir>... [--trust root.cer]... [--system-trust] [--revocation NoCheck|Offline|Online] [--json out.json] [-v]
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using S8.Validate;

var opts = new ValidatorOptions();
var inputs = new List<string>();
string? jsonOut = null;
bool verbose = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--trust": opts.TrustAnchors.Add(X509CertificateLoader.LoadCertificateFromFile(args[++i])); break;
        case "--system-trust": opts.UseSystemTrust = true; break;
        case "--revocation": opts.RevocationMode = Enum.Parse<X509RevocationMode>(args[++i], true); break;
        case "--json": jsonOut = args[++i]; break;
        case "-v": verbose = true; break;
        default: inputs.Add(args[i]); break;
    }
}
var files = inputs.SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p, "*.pdf", SearchOption.AllDirectories) : new[] { p }).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
var v = new Validator(opts);
var reports = new List<FileReport>();
foreach (var f in files)
{
    var r = v.ValidateFile(File.ReadAllBytes(f), Path.GetFileName(f));
    reports.Add(r);
    Console.WriteLine($"{r.File}: {(r.Signatures.Count == 0 ? "no signatures" : "")}{(r.StructureError != null ? $"  [structure: {r.StructureError}]" : "")}");
    foreach (var s in r.Signatures)
    {
        Console.WriteLine($"  sig#{s.Index} '{s.FieldName}' {s.SubFilter}: {s.Status}  (integrity={s.Integrity}, identity={s.Identity}, mods={s.Modifications})");
        Console.WriteLine($"      {s.StatusReason}");
        if (verbose)
        {
            foreach (var c in s.Checks) Console.WriteLine($"      [{c.Outcome}] {c.Id}: {c.Detail}");
            foreach (var c in s.Changes) Console.WriteLine($"      <{c.Class}> {c.Description}");
        }
    }
}
if (jsonOut != null)
    File.WriteAllText(jsonOut, JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never }));
