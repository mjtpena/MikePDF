// S10 probe. Usage:
//   S10.Probe matrix  <rootDir> <label> [out.jsonl]    holder x mechanism replace matrix
//   S10.Probe attack  <rootDir> [out.jsonl]            what another process can do under each sharing mode
//   S10.Probe meta    <rootDir> <label> [out.jsonl]    ACL / ADS / attributes / times / file id / links after a replace
//   S10.Probe crash   <rootDir> [out.jsonl]            kill a simulated save at each step, then recover
//   S10.Probe stress  <rootDir> <rounds> [out.jsonl]   kill a replacing loop at random moments
// Internal: hold, attacker, savesim, stress-child (spawned by the commands above).
using S10;

if (args.Length == 0) { Console.WriteLine("see header of Program.cs"); return 1; }
switch (args[0])
{
    case "matrix": Matrix.Run(Dir(args[1]), args[2], args.ElementAtOrDefault(3)); return 0;
    case "attack": Attack.Run(Dir(args[1]), args.ElementAtOrDefault(2)); return 0;
    case "meta": Meta.Run(Dir(args[1]), args[2], args.ElementAtOrDefault(3)); return 0;
    case "crash": Crash.RunAll(Dir(args[1]), args.ElementAtOrDefault(2)); return 0;
    case "stress": Crash.Stress(Dir(args[1]), int.Parse(args[2]), args.ElementAtOrDefault(3)); return 0;
    case "repeat": Matrix.Repeat(Dir(args[1]), int.Parse(args[2]), args.ElementAtOrDefault(3)); return 0;
    case "netshare": NetShare.Run(Dir(args[1])); return 0;
    case "attrs": Console.WriteLine("0x" + N.GetFileAttributesW(args[1]).ToString("X")); return 0;
    case "cloud": Cloud.Run(args[1], args[2]); return 0;
    case "cloud-focused": Cloud.RunFocused(args[1], args[2], int.Parse(args[3])); return 0;
    case "hold": Matrix.HoldMain(); return 0;
    case "attacker": Attack.AttackerMain(args[1], args[2], args[3]); return 0;
    case "savesim": return Crash.SaveSimMain(args[1], args[2], int.Parse(args[3]), args[4]);
    case "stress-child": Crash.StressChild(args[1], args[2]); return 0;
    default: Console.Error.WriteLine("unknown command " + args[0]); return 1;
}

static string Dir(string p)
{
    Directory.CreateDirectory(p);
    return p;
}
