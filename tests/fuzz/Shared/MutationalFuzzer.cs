using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;

namespace MikePDF.Fuzz.Shared;

/// <summary>
/// A dependency-free mutational fuzzer for the .NET targets (ADR-0005 rule 7, master prompt 12.6).
///
/// What it is, plainly: a havoc-style byte mutator with a corpus and a watchdog. It has NO code coverage feedback,
/// because instrumenting an assembly needs SharpFuzz.CommandLine and a libFuzzer driver, neither of which has an accepted ADR
/// (see docs/adr/0017-fuzzing-toolchain.md). In its place the target returns an *outcome signature* (which decoder verdict and
/// which typed error an input produced), and an input that yields a signature not seen before joins the corpus. That steers
/// the search towards new rejection and acceptance paths without instrumentation, but it is weaker than coverage guidance:
/// two inputs that take different code paths to the same verdict look the same to it.
///
/// A supervisor process restarts a worker process whenever it dies, so a stack overflow or an out-of-memory crash is recorded
/// as a finding instead of ending the run. The input being executed is mirrored into a memory-mapped file before each run, which
/// the operating system keeps even when the worker is killed.
///
/// Exit codes: 0 no finding, 1 at least one finding, 2 bad usage.
/// </summary>
internal static partial class MutationalFuzzer
{
    private const int WorkerFinding = 98;
    private const int WorkerTimeout = 99;
    private const int MaxFindingsPerRun = 25;

    /// <summary>The target's name, how to run it on bytes, which seed files to take, and any extra seeds derived from a seed.</summary>
    internal sealed record Target(
        string Name,
        Func<byte[], long> Run,
        string[] SeedExtensions,
        Func<byte[], IEnumerable<byte[]>>? DerivedSeeds = null,
        string[]? LeadingArgs = null,
        IReadOnlyList<byte[]>? BuiltInSeeds = null);

    internal sealed class Options
    {
        public int Seconds { get; set; }
        public string? CorpusDir { get; set; }
        public string FindingsDir { get; set; } = "fuzz-findings";
        public List<string> Seeds { get; } = [];
        public string? DictionaryFile { get; set; }
        public int MaxLength { get; set; } = 65_540;
        public int TimeoutMs { get; set; } = 10_000;
        public int Rng { get; set; }
        public int StatsSeconds { get; set; } = 10;
        public bool Worker { get; set; }
        public string? CurrentFile { get; set; }
        public string? MinimizeFile { get; set; }
        public string? SelfTest { get; set; }
        public int MaxFindings { get; set; } = MaxFindingsPerRun;
    }

    /// <summary>True when `args` ask for a mutational run (or one of its helper modes) rather than a replay or SharpFuzz run.</summary>
    public static bool Handles(string[] args) =>
        args.Any(a => a is "--mutate" or "--worker" or "--minimize");

    /// <summary>
    /// Targets that fail on purpose once the mutator has made an input start with "FZZ": <c>--selftest throw|hang|overflow</c> replaces the real
    /// target. They prove that the fuzzer finds an exception, a hang (watchdog) and a stack overflow (supervisor) and keeps a reproducer.
    /// </summary>
    internal static Target SelfTestTarget(string kind) => new(
        "selftest-" + kind,
        input =>
        {
            if (input.Length >= 3 && input[0] == 'F' && input[1] == 'Z' && input[2] == 'Z')
            {
                switch (kind)
                {
                    case "throw": throw new InvalidOperationException("selftest: the planted failure was reached");
                    case "hang": Thread.Sleep(Timeout.Infinite); break;
                    default: Overflow(1); break;
                }
            }

            return input.Length >= 2 && input[0] == 'F' && input[1] == 'Z' ? 1 : 0;
        },
        SeedExtensions: [],
        BuiltInSeeds: [[(byte)'F', (byte)'Z', (byte)'A']],
        LeadingArgs: ["--selftest", kind]);

    private static int Overflow(int depth) => depth == 0 ? 0 : Overflow(depth + 1) + 1;

    public static int Run(Target target, string[] args)
    {
        Options options;
        try
        {
            options = Parse(args);
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine("usage: --mutate <seconds> [--corpus dir] [--seeds dir|file]... [--findings dir] [--dict file] [--max-len n] [--timeout-ms n] [--rng n]");
            Console.Error.WriteLine("       --minimize <file>   shrink a crashing input (writes <file>.min)");
            return 2;
        }

        if (options.SelfTest is not null) target = SelfTestTarget(options.SelfTest);
        if (options.MinimizeFile is not null) return Minimize(target, options);
        return options.Worker ? RunWorker(target, options) : RunSupervisor(target, options);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Arguments

    private static Options Parse(string[] args)
    {
        var o = new Options { Rng = Environment.TickCount };
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("missing value after " + args[i]);
            switch (args[i])
            {
                case "--mutate": o.Seconds = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--corpus": o.CorpusDir = Next(); break;
                case "--seeds": o.Seeds.Add(Next()); break;
                case "--findings": o.FindingsDir = Next(); break;
                case "--dict": o.DictionaryFile = Next(); break;
                case "--max-len": o.MaxLength = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--timeout-ms": o.TimeoutMs = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--rng": o.Rng = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--stats-seconds": o.StatsSeconds = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--worker": o.Worker = true; break;
                case "--current": o.CurrentFile = Next(); break;
                case "--minimize": o.MinimizeFile = Next(); break;
                case "--selftest": o.SelfTest = Next(); break;
                case "--max-findings": o.MaxFindings = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException("unknown argument " + args[i]);
            }
        }

        if (o.MinimizeFile is null && o.Seconds <= 0) throw new ArgumentException("--mutate <seconds> is required");
        return o;
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Supervisor

    private static int RunSupervisor(Target target, Options o)
    {
        Directory.CreateDirectory(o.FindingsDir);
        string currentFile = Path.Combine(o.FindingsDir, $".current-{Environment.ProcessId}");
        var deadline = DateTime.UtcNow.AddSeconds(o.Seconds);
        var stackSignatures = new HashSet<string>(StringComparer.Ordinal);
        int findings = 0, workers = 0;
        Console.WriteLine($"mutational fuzzer (NO coverage feedback; novelty = new outcome signature), {o.Seconds}s, rng {o.Rng}");

        try
        {
            while (DateTime.UtcNow < deadline && findings < o.MaxFindings)
            {
                int remaining = Math.Max(1, (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalSeconds));
                var (exit, stderr) = RunWorkerProcess(target, o, remaining, currentFile, o.Rng + workers++);
                if (exit == 0) break;
                if (exit == 2)
                {
                    // The worker's own usage error (a bad option, a missing seed folder), not something an input did.
                    Console.Error.WriteLine("the worker could not start:" + Environment.NewLine + stderr);
                    return 2;
                }

                byte[] input = ReadCurrent(currentFile);
                string kind = exit == WorkerTimeout ? "timeout" : "crash";
                string text = stderr.Length > 8000 ? stderr[..8000] : stderr;
                string stack = StackSignature(stderr);
                string hash = Convert.ToHexString(SHA256.HashData(input))[..16].ToLowerInvariant();
                bool duplicate = !stackSignatures.Add(kind + stack);
                Console.WriteLine($"FINDING {kind} (exit {exit:X}), {input.Length} bytes, {(duplicate ? "same stack as an earlier finding" : "new stack")}: {hash}");
                if (!duplicate)
                {
                    File.WriteAllBytes(Path.Combine(o.FindingsDir, $"{kind}-{hash}"), input);
                    File.WriteAllText(Path.Combine(o.FindingsDir, $"{kind}-{hash}.txt"), text);
                    Console.WriteLine(text);
                }

                findings++;
            }
        }
        finally
        {
            try { File.Delete(currentFile); } catch (IOException) { /* best effort */ }
        }

        Console.WriteLine(findings == 0 ? "done: no findings" : $"done: {findings} finding(s) in {o.FindingsDir}");
        return findings == 0 ? 0 : 1;
    }

    private static (int Exit, string Stderr) RunWorkerProcess(Target target, Options o, int seconds, string currentFile, int rng)
    {
        string host = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var psi = new ProcessStartInfo(host) { RedirectStandardError = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
        void Add(params string[] a) { foreach (var x in a) psi.ArgumentList.Add(x); }
        Add(target.LeadingArgs ?? []);
        Add("--worker", "--mutate", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "--current", currentFile,
            "--rng", rng.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--max-len", o.MaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--timeout-ms", o.TimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--stats-seconds", o.StatsSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--findings", o.FindingsDir);
        if (o.CorpusDir is not null) Add("--corpus", o.CorpusDir);
        if (o.DictionaryFile is not null) Add("--dict", o.DictionaryFile);
        foreach (var s in o.Seeds) Add("--seeds", s);

        using var process = Process.Start(psi)!;
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (errors) errors.AppendLine(e.Data); };
        process.BeginErrorReadLine();
        process.WaitForExit();
        process.WaitForExit(); // flushes the asynchronous stderr reader
        lock (errors) return (process.ExitCode, errors.ToString());
    }

    private static byte[] ReadCurrent(string path)
    {
        try
        {
            byte[] raw = File.ReadAllBytes(path);
            if (raw.Length < 4) return [];
            int length = BitConverter.ToInt32(raw, 0);
            return length < 0 || length > raw.Length - 4 ? [] : raw.AsSpan(4, length).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>Exception type plus the first stack frames, with line numbers removed: identifies "the same bug".</summary>
    private static string StackSignature(string stderr)
    {
        var lines = stderr.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var frames = lines.Where(l => l.StartsWith("at ", StringComparison.Ordinal)).Take(3).Select(l => { int p = l.IndexOf(" in ", StringComparison.Ordinal); return p > 0 ? l[..p] : l; });
        string head = lines.FirstOrDefault(l => l.StartsWith("FINDING", StringComparison.Ordinal) || l.Contains("Exception", StringComparison.Ordinal) || l.Contains("TIMEOUT", StringComparison.Ordinal)) ?? "";
        return string.Concat(head.Where(c => !char.IsDigit(c))) + "|" + string.Join('|', frames);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Worker

    private const uint ErrorModeNoDialogs = 0x0001 | 0x0002 | 0x8000; // SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    private static partial uint SetErrorMode(uint mode);

    private static int RunWorker(Target target, Options o)
    {
        // A worker that crashes on purpose (a stack overflow is a finding) must not raise a Windows error dialog and wait for a click.
        _ = SetErrorMode(ErrorModeNoDialogs);
        var rng = new Random(o.Rng);
        var dictionary = o.DictionaryFile is null ? [] : LoadDictionary(o.DictionaryFile);
        var corpus = new List<byte[]>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        var signatures = new HashSet<long>();
        if (o.CorpusDir is not null) Directory.CreateDirectory(o.CorpusDir);

        using var current = o.CurrentFile is null ? null : MemoryMappedFile.CreateFromFile(o.CurrentFile, FileMode.OpenOrCreate, null, o.MaxLength + 8);
        using var view = current?.CreateViewAccessor();
        long startTicks = 0;
        long executions = 0;

        var watchdog = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(200);
                long started = Interlocked.Read(ref startTicks);
                if (started != 0 && (Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency > o.TimeoutMs)
                {
                    Console.Error.WriteLine($"TIMEOUT: an input ran longer than {o.TimeoutMs} ms");
                    Environment.Exit(WorkerTimeout);
                }
            }
        })
        { IsBackground = true, Name = "fuzz-watchdog" };
        watchdog.Start();

        long Execute(byte[] input)
        {
            if (view is not null)
            {
                view.Write(0, input.Length);
                view.WriteArray(4, input, 0, input.Length);
            }

            Interlocked.Exchange(ref startTicks, Stopwatch.GetTimestamp());
            try
            {
                return target.Run(input);
            }
            finally
            {
                Interlocked.Exchange(ref startTicks, 0);
                executions++;
            }
        }

        try
        {
            void AddSeed(byte[] bytes)
            {
                if (bytes.Length == 0 || bytes.Length > o.MaxLength || !known.Add(Convert.ToHexString(SHA256.HashData(bytes)))) return;
                signatures.Add(Execute(bytes));
                corpus.Add(bytes);
            }

            foreach (var path in o.Seeds.SelectMany(s => EnumerateSeeds(s, target.SeedExtensions)).Concat(o.CorpusDir is null ? [] : EnumerateSeeds(o.CorpusDir, [])))
            {
                byte[] bytes = File.ReadAllBytes(path);
                AddSeed(bytes);
                if (target.DerivedSeeds is not null)
                    foreach (var derived in target.DerivedSeeds(bytes)) AddSeed(derived);
            }

            foreach (var builtIn in target.BuiltInSeeds ?? []) AddSeed(builtIn);
            if (corpus.Count == 0) AddSeed([0]);
            Console.WriteLine($"worker: {corpus.Count} seeds, {signatures.Count} distinct outcomes");

            var clock = Stopwatch.StartNew();
            var lastStats = TimeSpan.Zero;
            var end = TimeSpan.FromSeconds(o.Seconds);
            while (clock.Elapsed < end)
            {
                for (int batch = 0; batch < 64; batch++)
                {
                    byte[] baseInput = rng.Next(10) < 3 && corpus.Count > 16 ? corpus[corpus.Count - 1 - rng.Next(16)] : corpus[rng.Next(corpus.Count)];
                    byte[] mutated = Mutate(baseInput, corpus, dictionary, rng, o.MaxLength);
                    long signature = Execute(mutated);
                    if (signatures.Add(signature) && corpus.Count < 50_000)
                    {
                        corpus.Add(mutated);
                        if (o.CorpusDir is not null)
                            File.WriteAllBytes(Path.Combine(o.CorpusDir, Convert.ToHexString(SHA256.HashData(mutated))[..20].ToLowerInvariant()), mutated);
                    }
                }

                if (clock.Elapsed - lastStats >= TimeSpan.FromSeconds(o.StatsSeconds))
                {
                    lastStats = clock.Elapsed;
                    Console.WriteLine($"#{executions} execs, {executions / Math.Max(1.0, clock.Elapsed.TotalSeconds):F0}/s, corpus {corpus.Count}, outcomes {signatures.Count}, {clock.Elapsed.TotalSeconds:F0}s");
                }
            }

            Console.WriteLine($"worker done: {executions} execs, corpus {corpus.Count}, outcomes {signatures.Count}");
            return 0;
        }
#pragma warning disable CA1031 // an escaped exception from the target IS the finding
        catch (Exception e)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine("FINDING: " + e);
            return WorkerFinding;
        }
    }

    private static IEnumerable<string> EnumerateSeeds(string path, string[] extensions)
    {
        if (File.Exists(path)) return [path];
        if (!Directory.Exists(path)) return [];
        return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Where(f => extensions.Length == 0 || extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Minimiser

    internal static int Minimize(Target target, Options o)
    {
        byte[] best = File.ReadAllBytes(o.MinimizeFile!);
        string? wanted = Describe(target, best);
        if (wanted is null)
        {
            Console.Error.WriteLine("the input does not throw; nothing to minimise");
            return 2;
        }

        Console.WriteLine($"minimising {best.Length} bytes that throw: {wanted}");
        for (int chunk = Math.Max(1, best.Length / 2); chunk >= 1;)
        {
            bool shrunk = false;
            for (int at = 0; at + chunk <= best.Length;)
            {
                byte[] trial = [.. best.AsSpan(0, at), .. best.AsSpan(at + chunk)];
                if (trial.Length > 0 && Describe(target, trial) == wanted)
                {
                    best = trial;
                    shrunk = true;
                }
                else
                {
                    at += chunk;
                }
            }

            if (!shrunk) chunk /= 2;
        }

        // Then try to simplify the bytes that are left.
        for (int i = 0; i < best.Length; i++)
        {
            foreach (byte simpler in new byte[] { 0, (byte)'0', (byte)' ' })
            {
                if (best[i] == simpler) break;
                byte[] trial = (byte[])best.Clone();
                trial[i] = simpler;
                if (Describe(target, trial) != wanted) continue;
                best = trial;
                break;
            }
        }

        string output = o.MinimizeFile + ".min";
        File.WriteAllBytes(output, best);
        Console.WriteLine($"wrote {output}: {best.Length} bytes");
        return 0;
    }

    /// <summary>The kind of exception the input throws (type and first stack frame, digits removed), or null when it does not throw.</summary>
    private static string? Describe(Target target, byte[] input)
    {
        try
        {
            target.Run(input);
            return null;
        }
#pragma warning disable CA1031 // the exception is the point
        catch (Exception e)
#pragma warning restore CA1031
        {
            string frame = e.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "";
            int p = frame.IndexOf(" in ", StringComparison.Ordinal);
            return e.GetType().FullName + "|" + (p > 0 ? frame[..p] : frame);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Mutation

    private static readonly byte[] InterestingBytes = [0, 1, 2, 3, 4, 7, 8, 15, 16, 31, 32, 63, 64, 100, 127, 128, 129, 191, 192, 254, 255];
    private static readonly int[] InterestingInts = [0, 1, 2, 3, 255, 256, 257, 511, 512, 1000, 1023, 1024, 1025, 4095, 4096, 32767, 32768, 65535, 65536, 65537, 100_000, 1_000_000, int.MaxValue, int.MinValue, -1, -2];

    internal static byte[] Mutate(byte[] source, List<byte[]> corpus, List<byte[]> dictionary, Random rng, int maxLength)
    {
        var data = new List<byte>(source);
        int rounds = 1 << rng.Next(0, 5);
        for (int r = 0; r < rounds; r++)
        {
            if (data.Count == 0) data.Add((byte)rng.Next(256));
            switch (rng.Next(16))
            {
                case 0: data[rng.Next(data.Count)] ^= (byte)(1 << rng.Next(8)); break;
                case 1: data[rng.Next(data.Count)] = InterestingBytes[rng.Next(InterestingBytes.Length)]; break;
                case 2: data[rng.Next(data.Count)] = (byte)rng.Next(256); break;
                case 3: data[rng.Next(data.Count)] += (byte)rng.Next(1, 36); break;
                case 4: data[rng.Next(data.Count)] -= (byte)rng.Next(1, 36); break;
                case 5: OverwriteInt(data, rng, 2); break;
                case 6: OverwriteInt(data, rng, 4); break;
                case 7: OverwriteInt(data, rng, 8); break;
                case 8: DeleteBlock(data, rng); break;
                case 9: CloneBlock(data, rng); break;
                case 10: InsertRandom(data, rng); break;
                case 11: OverwriteWithCopy(data, rng); break;
                case 12: Splice(data, corpus, rng); break;
                case 13 or 14: UseDictionary(data, dictionary, rng); break;
                default: data.Add(InterestingBytes[rng.Next(InterestingBytes.Length)]); break;
            }

            if (data.Count > maxLength) data.RemoveRange(maxLength, data.Count - maxLength);
        }

        return [.. data];
    }

    private static void OverwriteInt(List<byte> data, Random rng, int width)
    {
        if (data.Count < width) return;
        int at = rng.Next(data.Count - width + 1);
        long value = rng.Next(3) == 0 ? rng.NextInt64() : InterestingInts[rng.Next(InterestingInts.Length)];
        bool big = rng.Next(2) == 0;
        for (int i = 0; i < width; i++)
            data[at + i] = (byte)(value >> (8 * (big ? width - 1 - i : i)));
    }

    private static void DeleteBlock(List<byte> data, Random rng)
    {
        if (data.Count < 2) return;
        int length = rng.Next(1, Math.Min(data.Count, 1 + data.Count / 4 + 8));
        data.RemoveRange(rng.Next(data.Count - length + 1), length);
    }

    private static void CloneBlock(List<byte> data, Random rng)
    {
        int length = rng.Next(1, Math.Min(data.Count, 64) + 1);
        int from = rng.Next(data.Count - length + 1);
        data.InsertRange(rng.Next(data.Count + 1), data.GetRange(from, length));
    }

    private static void InsertRandom(List<byte> data, Random rng)
    {
        int length = rng.Next(1, 33);
        byte[] block = new byte[length];
        if (rng.Next(2) == 0) rng.NextBytes(block); else Array.Fill(block, InterestingBytes[rng.Next(InterestingBytes.Length)]);
        data.InsertRange(rng.Next(data.Count + 1), block);
    }

    private static void OverwriteWithCopy(List<byte> data, Random rng)
    {
        if (data.Count < 2) return;
        int length = rng.Next(1, Math.Min(data.Count, 32) + 1);
        var block = data.GetRange(rng.Next(data.Count - length + 1), length);
        int to = rng.Next(data.Count - length + 1);
        for (int i = 0; i < length; i++) data[to + i] = block[i];
    }

    private static void Splice(List<byte> data, List<byte[]> corpus, Random rng)
    {
        byte[] other = corpus[rng.Next(corpus.Count)];
        if (other.Length == 0) return;
        int keep = rng.Next(data.Count + 1);
        int from = rng.Next(other.Length);
        data.RemoveRange(keep, data.Count - keep);
        data.AddRange(other.AsSpan(from).ToArray());
    }

    private static void UseDictionary(List<byte> data, List<byte[]> dictionary, Random rng)
    {
        if (dictionary.Count == 0) return;
        byte[] token = dictionary[rng.Next(dictionary.Count)];
        if (rng.Next(2) == 0 || data.Count < token.Length)
        {
            data.InsertRange(rng.Next(data.Count + 1), token);
        }
        else
        {
            int at = rng.Next(data.Count - token.Length + 1);
            for (int i = 0; i < token.Length; i++) data[at + i] = token[i];
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Dictionary (the AFL and libFuzzer format: `name="value"` or `"value"` per line, \\ \" \xNN escapes, # comments)

    internal static List<byte[]> LoadDictionary(string path)
    {
        var tokens = new List<byte[]>();
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            int open = line.IndexOf('"', StringComparison.Ordinal);
            int close = line.LastIndexOf('"');
            if (open < 0 || close <= open) continue;
            var bytes = new List<byte>();
            for (int i = open + 1; i < close; i++)
            {
                char c = line[i];
                if (c != '\\' || i + 1 >= close) { bytes.Add((byte)c); continue; }
                char n = line[++i];
                if (n == 'x' && i + 2 < close && byte.TryParse(line.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
                {
                    bytes.Add(b);
                    i += 2;
                }
                else
                {
                    bytes.Add((byte)n);
                }
            }

            if (bytes.Count > 0) tokens.Add([.. bytes]);
        }

        return tokens;
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Helpers for the targets

    /// <summary>Mixes a value into a running signature (FNV-1a style).</summary>
    internal static long Mix(long signature, long value) => unchecked((signature ^ value) * 1_099_511_628_211L);

    /// <summary>A text with its digits removed, hashed: error messages that embed offsets or counts must not make every input look new.</summary>
    internal static long TextSignature(string? text)
    {
        long h = unchecked((long)14_695_981_039_346_656_037UL);
        if (text is null) return h;
        foreach (char c in text)
            if (!char.IsDigit(c)) h = Mix(h, c);
        return h;
    }
}
