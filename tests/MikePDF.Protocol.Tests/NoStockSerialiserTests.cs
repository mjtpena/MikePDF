using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using MikePDF.ProtocolGen;

namespace MikePDF.Protocol.Tests;

/// <summary>
/// ADR-0005 wants one validated path from the wire to the object model. These tests keep MessagePack-CSharp's reflective
/// and source-generated serialisation out of MikePDF.Protocol: only the MessagePackReader and MessagePackWriter primitives
/// may be used.
/// </summary>
public sealed class NoStockSerialiserTests
{
    private static readonly Assembly Protocol = typeof(ProtocolCodec).Assembly;

    // The only MessagePack types the assembly may reference.
    private static readonly string[] AllowedMessagePackTypes =
    [
        "MessagePack.MessagePackReader",
        "MessagePack.MessagePackWriter",
        "MessagePack.MessagePackCode",
        "MessagePack.MessagePackSerializationException",
    ];

    [Fact]
    public void No_type_carries_a_MessagePack_serialisation_attribute()
    {
        foreach (var type in Protocol.GetTypes())
        {
            Assert.DoesNotContain(type.GetCustomAttributesData(), a => a.AttributeType.Namespace == "MessagePack");
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                Assert.DoesNotContain(member.GetCustomAttributesData(), a => a.AttributeType.Namespace == "MessagePack");
        }
    }

    [Fact]
    public void The_assembly_references_no_MessagePack_type_except_the_reader_and_writer_primitives()
    {
        using var stream = File.OpenRead(Protocol.Location);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();

        var referenced = new List<string>();
        foreach (var handle in md.TypeReferences)
        {
            var reference = md.GetTypeReference(handle);
            var ns = md.GetString(reference.Namespace);
            if (ns == "MessagePack" || ns.StartsWith("MessagePack.", StringComparison.Ordinal))
                referenced.Add(ns + "." + md.GetString(reference.Name));
        }

        Assert.NotEmpty(referenced); // the scan is looking at the right assembly
        Assert.DoesNotContain(referenced, r => r.Contains("MessagePackSerializer", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, r => r.Contains("Resolver", StringComparison.Ordinal) || r.Contains("Formatter", StringComparison.Ordinal));
        Assert.Empty(referenced.Except(AllowedMessagePackTypes));

        // And no generated resolver or formatter types exist in the assembly itself.
        Assert.DoesNotContain(Protocol.GetTypes(), t => t.Name.Contains("Resolver", StringComparison.Ordinal) || t.Name.Contains("Formatter", StringComparison.Ordinal));
    }

    [Fact]
    public void Neither_the_source_nor_the_generated_code_names_the_stock_serialiser()
    {
        var root = Generator.FindRoot(AppContext.BaseDirectory);
        var sources = Directory.EnumerateFiles(Path.Combine(root, "src", "MikePDF.Protocol"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .ToList();
        Assert.True(sources.Count >= 7);
        foreach (var file in sources)
        {
            var text = File.ReadAllText(file);
            foreach (var banned in new[] { "MessagePackSerializer", "MessagePackObject", "[Key(", "IMessagePackFormatter", "MessagePack.Resolvers", "GeneratedMessagePackResolver" })
                Assert.DoesNotContain(banned, text, StringComparison.Ordinal);
        }

        foreach (var (path, content) in Generator.Render(Generator.LoadSchema(root)))
        {
            Assert.DoesNotContain("MessagePackSerializer", content, StringComparison.Ordinal);
            Assert.DoesNotContain("MessagePackObject", content, StringComparison.Ordinal);
            Assert.True(path.Length > 0);
        }
    }
}
