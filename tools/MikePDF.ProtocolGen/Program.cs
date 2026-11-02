// MikePDF.ProtocolGen: generates (or verifies) the C# and C++ protocol code from the schema (ADR-0005).
//   dotnet run --project tools/MikePDF.ProtocolGen -- generate [--root <repo>]
//   dotnet run --project tools/MikePDF.ProtocolGen -- check    [--root <repo>]
namespace MikePDF.ProtocolGen;

internal static class Program
{
    private static int Main(string[] args)
    {
        var command = args.Length > 0 ? args[0] : "";
        var rootIndex = Array.IndexOf(args, "--root");
        try
        {
            var root = rootIndex >= 0 && rootIndex + 1 < args.Length
                ? Path.GetFullPath(args[rootIndex + 1])
                : Generator.FindRoot(Directory.GetCurrentDirectory());
            switch (command)
            {
                case "generate":
                    Generator.Generate(root);
                    Console.WriteLine("Protocol code generated.");
                    return 0;
                case "check":
                    var problems = Generator.Check(root);
                    foreach (var p in problems) Console.Error.WriteLine(p);
                    if (problems.Count == 0)
                    {
                        Console.WriteLine("Protocol code is up to date.");
                        return 0;
                    }

                    Console.Error.WriteLine("Generated protocol code is out of date. Run: dotnet run --project tools/MikePDF.ProtocolGen -- generate");
                    return 1;
                default:
                    Console.Error.WriteLine("usage: MikePDF.ProtocolGen (generate|check) [--root <repo>]");
                    return 2;
            }
        }
        catch (SchemaException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }
}
