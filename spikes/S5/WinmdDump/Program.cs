using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

// S5.1 / S6.4 helper: lists types (and their public members) in one or more .winmd files whose full name matches a regex.
// usage: WinmdDump <typeNameRegex> [--members] <winmd>...
var rx = new Regex(args[0], RegexOptions.IgnoreCase);
bool members = args.Contains("--members");
foreach (var path in args.Skip(1).Where(a => !a.StartsWith("--")))
{
    using var fs = File.OpenRead(path);
    using var pe = new PEReader(fs);
    var md = pe.GetMetadataReader();
    foreach (var h in md.TypeDefinitions)
    {
        var t = md.GetTypeDefinition(h);
        string ns = md.GetString(t.Namespace), name = md.GetString(t.Name);
        string full = ns + "." + name;
        if (!rx.IsMatch(full)) continue;
        if ((t.Attributes & System.Reflection.TypeAttributes.VisibilityMask) != System.Reflection.TypeAttributes.Public) continue;
        string kind = (t.Attributes & System.Reflection.TypeAttributes.Interface) != 0 ? "interface" : "type";
        Console.WriteLine($"{Path.GetFileName(path)}\t{kind}\t{full}");
        if (!members) continue;
        foreach (var mh in t.GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            var mn = md.GetString(m.Name);
            if (mn.StartsWith("get_") || mn.StartsWith("put_") || mn.StartsWith("add_") || mn.StartsWith("remove_") || mn == ".ctor") continue;
            Console.WriteLine($"\t\t{mn}()");
        }
        foreach (var ph in t.GetProperties())
        {
            var p = md.GetPropertyDefinition(ph);
            Console.WriteLine($"\t\tprop {md.GetString(p.Name)}");
        }
        foreach (var eh in t.GetEvents())
        {
            var e = md.GetEventDefinition(eh);
            Console.WriteLine($"\t\tevent {md.GetString(e.Name)}");
        }
    }
}
