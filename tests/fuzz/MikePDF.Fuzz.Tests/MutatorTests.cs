using MikePDF.Fuzz.Shared;

namespace MikePDF.Fuzz.Tests;

/// <summary>The mutator, dictionary reader, outcome signatures and crash minimiser that the .NET fuzz targets are driven by.</summary>
public class MutatorTests
{
    [Fact]
    public void Mutation_never_exceeds_the_maximum_length_and_is_repeatable_for_a_seed()
    {
        var corpus = new List<byte[]> { new byte[64], new byte[] { 1, 2, 3 }, Enumerable.Range(0, 200).Select(i => (byte)i).ToArray() };
        var dictionary = new List<byte[]> { new byte[100], new byte[] { 0xFF } };
        var first = new Random(7);
        var second = new Random(7);
        for (int i = 0; i < 3000; i++)
        {
            byte[] a = MutationalFuzzer.Mutate(corpus[i % corpus.Count], corpus, dictionary, first, 128);
            byte[] b = MutationalFuzzer.Mutate(corpus[i % corpus.Count], corpus, dictionary, second, 128);
            Assert.True(a.Length <= 128);
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void Mutation_changes_inputs_and_keeps_producing_non_empty_ones_from_a_one_byte_seed()
    {
        var corpus = new List<byte[]> { new byte[] { 0 } };
        var rng = new Random(1);
        var distinct = new HashSet<string>();
        for (int i = 0; i < 500; i++)
        {
            byte[] mutated = MutationalFuzzer.Mutate(corpus[0], corpus, [], rng, 64);
            distinct.Add(Convert.ToHexString(mutated));
        }

        Assert.True(distinct.Count > 100, $"only {distinct.Count} distinct inputs from 500 mutations");
    }

    [Fact]
    public void The_dictionary_reader_understands_names_escapes_and_comments()
    {
        using var scratch = new FuzzAssets.Scratch();
        string path = Path.Combine(scratch.Path, "t.dict");
        File.WriteAllText(path, string.Join('\n',
            "# a comment",
            "",
            "plain=\"abc\"",
            "\"\\x00\\xFF\\x7f\"",
            "quote=\"a\\\"b\"",
            "slash=\"\\\\\"",
            "bad line without quotes"));

        var tokens = MutationalFuzzer.LoadDictionary(path);
        Assert.Equal(4, tokens.Count);
        Assert.Equal("abc"u8.ToArray(), tokens[0]);
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x7F }, tokens[1]);
        Assert.Equal("a\"b"u8.ToArray(), tokens[2]);
        Assert.Equal("\\"u8.ToArray(), tokens[3]);
    }

    [Theory]
    [InlineData("protocol.dict")]
    [InlineData("pdf.dict")]
    public void The_shipped_dictionaries_are_well_formed_for_libFuzzer(string name)
    {
        string path = FuzzAssets.Directory("dictionaries", name);
        var tokens = MutationalFuzzer.LoadDictionary(path);
        Assert.True(tokens.Count >= 50, $"{name} has only {tokens.Count} tokens");
        Assert.All(tokens, t => Assert.InRange(t.Length, 1, 128)); // libFuzzer refuses entries over 128 bytes

        // Every non-comment line must have been read as a token: a typo would otherwise silently shrink the dictionary.
        int lines = File.ReadLines(path).Count(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));
        Assert.Equal(lines, tokens.Count);
    }

    [Fact]
    public void Outcome_signatures_ignore_digits_so_that_offsets_in_messages_do_not_look_new()
    {
        Assert.Equal(MutationalFuzzer.TextSignature("object 12 at offset 345"), MutationalFuzzer.TextSignature("object 9 at offset 7"));
        Assert.NotEqual(MutationalFuzzer.TextSignature("object 12 at offset 345"), MutationalFuzzer.TextSignature("stream 12 at offset 345"));
    }

    [Fact]
    public void The_minimiser_shrinks_a_failing_input_to_the_bytes_that_matter()
    {
        using var scratch = new FuzzAssets.Scratch();
        string path = Path.Combine(scratch.Path, "crash-input");
        byte[] big = new byte[500];
        new Random(3).NextBytes(big);
        for (int i = 0; i < big.Length; i++) if (big[i] == 0x42) big[i] = 0x41;
        big[200] = 0x42;
        big[201] = 0x43;
        File.WriteAllBytes(path, big);

        var target = new MutationalFuzzer.Target(
            "t",
            input =>
            {
                for (int i = 0; i + 1 < input.Length; i++)
                    if (input[i] == 0x42 && input[i + 1] == 0x43) throw new InvalidOperationException("planted");
                return 0;
            },
            SeedExtensions: []);

        int exit = MutationalFuzzer.Minimize(target, new MutationalFuzzer.Options { MinimizeFile = path });

        Assert.Equal(0, exit);
        Assert.Equal(new byte[] { 0x42, 0x43 }, File.ReadAllBytes(path + ".min"));
    }

    [Fact]
    public void The_minimiser_refuses_an_input_that_does_not_fail()
    {
        using var scratch = new FuzzAssets.Scratch();
        string path = Path.Combine(scratch.Path, "fine");
        File.WriteAllBytes(path, [1, 2, 3]);
        var target = new MutationalFuzzer.Target("t", _ => 0, SeedExtensions: []);

        Assert.Equal(2, MutationalFuzzer.Minimize(target, new MutationalFuzzer.Options { MinimizeFile = path }));
        Assert.False(File.Exists(path + ".min"));
    }
}
