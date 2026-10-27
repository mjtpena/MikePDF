using Quire.ProtocolGen;

namespace Quire.Protocol.Tests;

public sealed class GeneratorTests
{
    private static readonly string Root = Generator.FindRoot(AppContext.BaseDirectory);

    [Fact]
    public void Checked_in_generated_code_is_up_to_date()
    {
        var problems = Generator.Check(Root);
        Assert.True(problems.Count == 0, "Run: dotnet run --project tools/Quire.ProtocolGen -- generate" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Generation_is_deterministic()
    {
        var schema = Generator.LoadSchema(Root);
        Assert.Equal(Generator.Render(schema), Generator.Render(schema));
    }

    [Fact]
    public void Generation_writes_and_check_detects_drift_and_orphans()
    {
        // Work on a scratch copy of the schema so the checked-in outputs are never touched.
        var scratch = Path.Combine(Path.GetTempPath(), "quire-gen-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(scratch, "src", "Quire.Protocol", "schema"));
            File.WriteAllText(Path.Combine(scratch, "Quire.slnx"), "<Solution />");
            File.Copy(Path.Combine(Root, Generator.SchemaPath), Path.Combine(scratch, Generator.SchemaPath));

            Assert.NotEmpty(Generator.Check(scratch)); // nothing generated yet: everything is missing
            Generator.Generate(scratch);
            Assert.Empty(Generator.Check(scratch));
            Assert.Equal(Generator.FindRoot(Path.Combine(scratch, "src")), scratch);

            // CRLF checkouts must not count as drift.
            var file = Path.Combine(scratch, "src", "Quire.Protocol", "generated", "Enums.g.cs");
            File.WriteAllText(file, File.ReadAllText(file).Replace("\n", "\r\n", StringComparison.Ordinal));
            Assert.Empty(Generator.Check(scratch));

            File.AppendAllText(file, "// edited by hand\n");
            Assert.Contains(Generator.Check(scratch), p => p.StartsWith("stale:", StringComparison.Ordinal));

            Generator.Generate(scratch);
            File.WriteAllText(Path.Combine(scratch, "src", "Quire.Protocol", "generated", "Old.g.cs"), "// left over\n");
            Assert.Contains(Generator.Check(scratch), p => p.StartsWith("orphan:", StringComparison.Ordinal));
            Generator.Generate(scratch);
            Assert.Empty(Generator.Check(scratch));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static string SchemaText(Func<string, string> edit) => edit(File.ReadAllText(Path.Combine(Root, Generator.SchemaPath)));

    [Fact]
    public void The_checked_in_schema_parses()
    {
        var schema = ProtocolGen.Schema.Parse(SchemaText(s => s));
        Assert.Equal((int)ProtocolInfo.SchemaVersion, schema.SchemaVersion);
        Assert.Equal(ProtocolInfo.MaxFrameBytes, schema.MaxFrameBytes);
        Assert.Equal(Enum.GetValues<MessageType>().Length, schema.Messages.Count);
    }

    [Theory]
    [InlineData("\"maxCount\": 5000, \"exclusiveMin\": 0, \"max\": 14400, \"doc\": \"Points.\"", "\"maxCount\": 10000, \"exclusiveMin\": 0, \"max\": 14400, \"doc\": \"Points.\"", "exceeds maxFrameBytes")]
    [InlineData("\"key\": 1, \"name\": \"processKind\"", "\"key\": 7, \"name\": \"processKind\"", "contiguous")]
    [InlineData("\"name\": \"nonce\", \"type\": \"u64\" } ] },\n      \"reply\"", "\"name\": \"nonce\", \"type\": \"u128\" } ] },\n      \"reply\"", "unknown type")]
    [InlineData("\"id\": 2, \"name\": \"Ping\"", "\"id\": 1, \"name\": \"Ping\"", "duplicate message ids")]
    [InlineData("\"maxBytes\": 64,", "", "needs maxBytes")]
    [InlineData("\"name\": \"scale\", \"type\": \"f32\", \"min\": 0.01, \"max\": 64", "\"name\": \"scale\", \"type\": \"f32\", \"min\": 0.01", "finite range")]
    [InlineData("\"sameLengthAs\": \"widths\", \"doc\": \"Quarter", "\"sameLengthAs\": \"nothing\", \"doc\": \"Quarter", "earlier array field")]
    [InlineData("\"name\": \"processKind\", \"type\": \"enum\", \"enum\": \"ProcessKind\"", "\"name\": \"processKind\", \"type\": \"enum\", \"enum\": \"Nope\"", "unknown enum")]
    [InlineData("\"name\": \"password\", \"type\": \"string\", \"optional\": true", "\"name\": \"password\", \"type\": \"u32\", \"optional\": true", "only strings can be optional")]
    [InlineData("\"max\": 16777216 },\n          { \"key\": 4", "\"max\": 99999999999 },\n          { \"key\": 4", "range exceeds")]
    [InlineData("\"documentId\": \"required\",\n      \"request\": { \"type\": \"CloseDocument\"", "\"documentId\": \"maybe\",\n      \"request\": { \"type\": \"CloseDocument\"", "documentId must be")]
    public void A_bad_schema_is_rejected_with_a_reason(string find, string replace, string expectedError)
    {
        var json = SchemaText(s =>
        {
            var normalised = s.Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Contains(find, normalised, StringComparison.Ordinal); // the test edit must hit something
            return normalised.Replace(find, replace, StringComparison.Ordinal);
        });
        var error = Assert.Throws<SchemaException>(() => ProtocolGen.Schema.Parse(json));
        Assert.Contains(expectedError, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Names_are_converted_the_same_way_in_both_languages()
    {
        Assert.Equal("schema_version", ProtocolGen.Schema.Snake("schemaVersion"));
        Assert.Equal("tile_x", ProtocolGen.Schema.Snake("tileX"));
        Assert.Equal("process_kind", ProtocolGen.Schema.Snake("ProcessKind"));
        Assert.Equal("has_xfa", ProtocolGen.Schema.Snake("hasXfa"));
        Assert.Equal("pdf_version", ProtocolGen.Schema.Snake("pdfVersion"));
        Assert.Equal("PageCount", ProtocolGen.Schema.Pascal("pageCount"));
    }
}
