using System.Reflection;
using MikePDF.ProtocolGen;

namespace MikePDF.Protocol.Tests;

/// <summary>
/// Reads the schema and, for every field of every message, tries the value at each edge of its limit and one step beyond.
/// A value inside the limit must validate and survive a trip over the wire; a value outside must fail Validate(), be
/// refused by the encoder, and come back from the decoder as InvalidArgument.
/// </summary>
public sealed class LimitTests
{
    private static readonly Schema Schema = Generator.LoadSchema(Generator.FindRoot(AppContext.BaseDirectory));

    private sealed record Case(string Id, string Field, string TypeName, bool IsRequest, Action<object> Mutate, bool Valid, bool OnWire);

    private static readonly Lazy<IReadOnlyList<Case>> LazyCases = new(BuildCases);

    private static IReadOnlyList<Case> AllCases => LazyCases.Value;

    public static TheoryData<string> CaseIds => [.. AllCases.Select(c => c.Id)];

    // ---- building the cases from the schema ----------------------------------------------------

    private static List<Case> BuildCases()
    {
        var cases = new List<Case>();
        foreach (var m in Schema.Messages)
        {
            foreach (var (st, isRequest) in new[] { (m.Request, true), (m.Reply, false) })
            {
                foreach (var f in st.Fields)
                {
                    var prop = Schema.Pascal(f.Name);
                    var where = $"{st.TypeName}.{f.Name}";
                    if (f.Type == FieldType.Array)
                    {
                        AddArrayCases(cases, st, f, prop, where, isRequest);
                    }
                    else
                    {
                        foreach (var (label, value, valid, onWire) in Edges(f.Type, f))
                            cases.Add(new($"{where}: {label}", where, st.TypeName, isRequest, o => Set(o, prop, value), valid, onWire));
                    }
                }
            }
        }

        return cases;
    }

    private static void AddArrayCases(List<Case> cases, StructDef st, FieldDef f, string prop, string where, bool isRequest)
    {
        var element = f.Element!.Value;
        var companions = st.Fields.Where(o => o.Type == FieldType.Array).Select(o => (Prop: Schema.Pascal(o.Name), Field: o)).ToList();

        void Count(string label, int n, bool valid) =>
            cases.Add(new($"{where}: {label}", where, st.TypeName, isRequest, o =>
            {
                // All arrays of the message change length together, so only the count under test can be wrong.
                foreach (var (p, field) in companions) Set(o, p, MakeArray(field.Element!.Value, field, n));
            }, valid, true));

        Count("empty", 0, true);
        Count("max count", f.MaxCount!.Value, true);
        Count("max count + 1", f.MaxCount.Value + 1, false);
        cases.Add(new($"{where}: null", where, st.TypeName, isRequest, o => Set(o, prop, null), false, false));

        foreach (var (label, value, valid, onWire) in Edges(element, f))
            cases.Add(new($"{where}: element {label}", where, st.TypeName, isRequest, o => ((Array)Get(o, prop)!).SetValue(value, 0), valid, onWire));

        if (f.SameLengthAs is not null)
        {
            var other = Schema.Pascal(f.SameLengthAs);
            cases.Add(new($"{where}: one element longer than {f.SameLengthAs}", where, st.TypeName, isRequest,
                o => Set(o, prop, MakeArray(element, f, ((Array)Get(o, other)!).Length + 1)), false, true));
            cases.Add(new($"{where}: one element shorter than {f.SameLengthAs}", where, st.TypeName, isRequest,
                o => Set(o, prop, MakeArray(element, f, ((Array)Get(o, other)!).Length - 1)), false, true));
        }
    }

    private static Array MakeArray(FieldType element, FieldDef f, int n)
    {
        var array = Array.CreateInstance(element switch
        {
            FieldType.U16 => typeof(ushort),
            FieldType.U32 => typeof(uint),
            FieldType.F32 => typeof(float),
            _ => typeof(string),
        }, n);
        for (var i = 0; i < n; i++) array.SetValue(ValidValue(element, f), i);
        return array;
    }

    private static object ValidValue(FieldType t, FieldDef f) => t switch
    {
        FieldType.U16 => (ushort)(f.Min ?? 0),
        FieldType.U32 => (uint)(f.Min ?? 0),
        FieldType.F32 => (float)f.Max!.Value,
        _ => "x",
    };

    // Values at and beyond each edge of the limits of one scalar: (label, value, valid, representable on the wire).
    private static IEnumerable<(string Label, object? Value, bool Valid, bool OnWire)> Edges(FieldType t, FieldDef f)
    {
        switch (t)
        {
            case FieldType.U16 or FieldType.U32 or FieldType.U64 or FieldType.I32:
                var (typeMin, typeMax) = Schema.Range(t);
                var lo = f.Min ?? typeMin;
                var hi = f.Max ?? typeMax;
                yield return ("min", Convert.ChangeType(lo, Clr(t), null), true, true);
                yield return ("max", Convert.ChangeType(hi, Clr(t), null), true, true);
                if (lo > typeMin) yield return ("min - 1", Convert.ChangeType(lo - 1, Clr(t), null), false, true);
                if (hi < typeMax) yield return ("max + 1", Convert.ChangeType(hi + 1, Clr(t), null), false, true);
                break;

            case FieldType.F32:
                var fhi = (float)f.Max!.Value;
                var flo = f.ExclusiveMin is { } ex ? MathF.BitIncrement((float)ex) : (float)f.Min!.Value;
                yield return ("lowest legal", flo, true, true);
                yield return ("max", fhi, true, true);
                yield return (f.ExclusiveMin is not null ? "exclusive minimum itself" : "just below min", f.ExclusiveMin is { } e2 ? (float)e2 : MathF.BitDecrement(flo), false, true);
                yield return ("just above max", MathF.BitIncrement(fhi), false, true);
                yield return ("NaN", float.NaN, false, true);
                yield return ("+Inf", float.PositiveInfinity, false, true);
                yield return ("-Inf", float.NegativeInfinity, false, true);
                break;

            case FieldType.Enum:
                var enumType = typeof(Status).Assembly.GetType("MikePDF.Protocol." + f.EnumName)!;
                var count = Schema.Enums.Single(e => e.Name == f.EnumName).Values.Count;
                yield return ("last defined", Enum.ToObject(enumType, count - 1), true, true);
                yield return ("first undefined", Enum.ToObject(enumType, (ushort)count), false, true);
                yield return ("65535", Enum.ToObject(enumType, ushort.MaxValue), false, true);
                break;

            case FieldType.Text:
                var max = f.MaxBytes!.Value;
                yield return ("empty", "", true, true);
                yield return ("max bytes of ASCII", new string('a', max), true, true);
                yield return ("max bytes of 2-byte characters", new string('é', max / 2), true, true);
                yield return ("max bytes + 1", new string('a', max + 1), false, true);
                yield return ("max bytes + 2 of 2-byte characters", new string('é', max / 2 + 1), false, true);
                yield return ("embedded NUL", "a\0b", false, true);
                yield return ("lone surrogate", "ab\uD800", false, false);
                if (f.Optional) yield return ("null", null, true, true);
                else if (f.Type == FieldType.Text) yield return ("null", null, false, false);
                break;
        }
    }

    private static Type Clr(FieldType t) => t switch
    {
        FieldType.U16 => typeof(ushort),
        FieldType.U32 => typeof(uint),
        FieldType.U64 => typeof(ulong),
        _ => typeof(int),
    };

    private static void Set(object o, string prop, object? value) => o.GetType().GetProperty(prop)!.SetValue(o, value);

    private static object? Get(object o, string prop) => o.GetType().GetProperty(prop)!.GetValue(o);

    private static object Fresh(string typeName)
    {
        // A private copy of the sample, made by sending it through the codec.
        var request = Samples.Requests.FirstOrDefault(r => r.Payload.GetType().Name == typeName);
        if (request is not null)
        {
            var frame = ProtocolCodec.EncodeRequestFrame(request.RequestId, request.DocumentId, request.DeadlineMs, request.Payload).Value;
            return ProtocolCodec.DecodeRequestFrame(frame).Value.Payload;
        }

        var response = Samples.Responses.First(r => r.Payload?.GetType().Name == typeName);
        var reply = ProtocolCodec.EncodeResponseFrame(response.RequestId, response.Status, response.Type, response.Payload).Value;
        return ProtocolCodec.DecodeResponseFrame(reply).Value.Payload!;
    }

    // ---- the tests -----------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void Limit_is_enforced(string id)
    {
        var c = AllCases.Single(x => x.Id == id);
        var payload = Fresh(c.TypeName);
        c.Mutate(payload);

        // 1. Validate() on the object.
        var failure = ((IProtocolMessage)payload).Validate();
        if (c.Valid) Assert.Null(failure);
        else Assert.Equal(Status.InvalidArgument, failure!.Value.Status);

        // 2. The encoder refuses what is invalid and accepts what is valid.
        var type = ((IProtocolMessage)payload).MessageType;
        var document = RawFrame.DocumentFor(type);
        var encoded = c.IsRequest
            ? ProtocolCodec.EncodeRequestFrame(1, document, 0, (IRequestMessage)payload)
            : ProtocolCodec.EncodeResponseFrame(1, Status.Ok, type, (IReplyMessage)payload);
        Assert.Equal(c.Valid, encoded.IsOk);
        if (!c.Valid) Assert.Equal(Status.InvalidArgument, encoded.Status);

        // 3. The decoder, fed the bytes a hostile peer could send, reaches the same verdict.
        if (!c.OnWire) return;
        var frame = c.IsRequest
            ? RawFrame.RequestOf((IRequestMessage)payload, 1, document)
            : RawFrame.ResponseOf((IReplyMessage)payload);
        var status = c.IsRequest ? ProtocolCodec.DecodeRequestFrame(frame).Status : ProtocolCodec.DecodeResponseFrame(frame).Status;
        Assert.Equal(c.Valid ? Status.Ok : Status.InvalidArgument, status);
        if (c.Valid)
        {
            object decoded = c.IsRequest ? ProtocolCodec.DecodeRequestFrame(frame).Value.Payload : ProtocolCodec.DecodeResponseFrame(frame).Value.Payload!;
            Assert.Equivalent(payload, decoded, strict: true);
        }
    }

    [Fact]
    public void Every_field_that_has_a_limit_has_a_failing_case()
    {
        Assert.True(AllCases.Count > 200, "expected a couple of hundred schema-driven cases, got " + AllCases.Count);
        foreach (var m in Schema.Messages)
        {
            foreach (var st in new[] { m.Request, m.Reply })
            {
                foreach (var f in st.Fields)
                {
                    var scalar = f.Scalar;
                    var (tmin, tmax) = scalar is FieldType.U16 or FieldType.U32 or FieldType.U64 or FieldType.I32 ? Schema.Range(scalar) : (0, 0m);
                    var limited = f.Type == FieldType.Array || scalar is FieldType.Text or FieldType.Enum or FieldType.F32
                        || (scalar is FieldType.U16 or FieldType.U32 or FieldType.U64 or FieldType.I32 && (f.Min > tmin || f.Max < tmax));
                    var where = $"{st.TypeName}.{f.Name}";
                    var hasInvalid = AllCases.Any(c => c.Field == where && !c.Valid);
                    var hasValid = AllCases.Any(c => c.Field == where && c.Valid);
                    Assert.True(!limited || hasInvalid, where + " has a limit but no failing case");
                    Assert.True(!limited || hasValid, where + " has a limit but no passing edge case");
                }
            }
        }
    }

    [Fact]
    public void The_samples_cover_every_payload_type_in_the_schema()
    {
        foreach (var m in Schema.Messages)
        {
            Assert.True(Samples.ByTypeName.ContainsKey(m.Request.TypeName), m.Request.TypeName);
            Assert.True(Samples.ByTypeName.ContainsKey(m.Reply.TypeName), m.Reply.TypeName);
        }

        // And the generated classes match the schema one for one.
        var generated = typeof(Hello).Assembly.GetTypes().Where(t => typeof(IProtocolMessage).IsAssignableFrom(t) && !t.IsInterface).Select(t => t.Name);
        Assert.Equal(
            Schema.Messages.SelectMany(m => new[] { m.Request.TypeName, m.Reply.TypeName }).Order(),
            generated.Order());
    }

    [Fact]
    public void Generated_properties_follow_the_schema_key_order()
    {
        // The key is the array index on the wire, and Write/Read emit fields in declaration order, so the property order is the key order.
        foreach (var m in Schema.Messages)
        {
            foreach (var st in new[] { m.Request, m.Reply })
            {
                var type = typeof(Hello).Assembly.GetType("MikePDF.Protocol." + st.TypeName)!;
                var names = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Select(p => p.Name).Where(n => n != "MessageType").ToList();
                Assert.Equal(st.Fields.OrderBy(f => f.Key).Select(f => Schema.Pascal(f.Name)), names);
            }
        }
    }
}
