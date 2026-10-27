using System.Text;

namespace Quire.PdfWriter.Tests;

public sealed class ObjectParserTests
{
    private static (PdfObject? Obj, PdfError? Error, IReadOnlyList<PdfError> Issues) Run(string text, bool strict = true, ParserLimits? limits = null)
    {
        var ctx = new ParseContext(limits ?? ParserLimits.Default, strict);
        try
        {
            var parser = new ObjectParser(Encoding.Latin1.GetBytes(text), ctx);
            return (parser.ParseObject(), null, ctx.Issues);
        }
        catch (PdfParseException ex)
        {
            return (null, ex.Error, ctx.Issues);
        }
    }

    private static T Ok<T>(string text, bool strict = true)
        where T : PdfObject
    {
        var (obj, error, _) = Run(text, strict);
        Assert.Null(error);
        return Assert.IsType<T>(obj);
    }

    private static PdfErrorCode Fails(string text, bool strict = true, ParserLimits? limits = null)
    {
        var (obj, error, _) = Run(text, strict, limits);
        Assert.NotNull(error);
        Assert.Null(obj);
        return error.Code;
    }

    // ---- scalars -------------------------------------------------------------------------------------------------

    [Fact]
    public void Keywords()
    {
        Assert.True(Ok<PdfBoolean>("true").Value);
        Assert.False(Ok<PdfBoolean>("false").Value);
        Assert.Same(PdfNull.Instance, Ok<PdfNull>("null"));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("17", 17)]
    [InlineData("+17", 17)]
    [InlineData("-98", -98)]
    [InlineData("007", 7)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("-2147483648", int.MinValue)]
    public void Integers(string text, long expected) => Assert.Equal(expected, Ok<PdfInteger>(text).Value);

    [Theory]
    [InlineData("34.5", 34.5)]
    [InlineData("-3.62", -3.62)]
    [InlineData("+123.6", 123.6)]
    [InlineData("4.", 4.0)]
    [InlineData("-.002", -0.002)]
    [InlineData(".5", 0.5)]
    [InlineData("0.0", 0.0)]
    public void Reals(string text, double expected) => Assert.Equal(expected, Ok<PdfReal>(text).Value, 12);

    [Theory]
    [InlineData("--5")]
    [InlineData("1.2.3")]
    [InlineData("1e5")]
    [InlineData("1-2")]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData(".")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("99999999999999999999")]
    public void MalformedNumbersAreRejectedInStrictMode(string text) => Assert.Equal(PdfErrorCode.InvalidNumber, Fails(text));

    [Fact]
    public void OutOfRangeRealIsRejected() =>
        Assert.Equal(PdfErrorCode.InvalidNumber, Fails("1" + new string('0', 40) + ".0"));

    [Fact]
    public void LenientModeRecoversFromMalformedNumbers()
    {
        var (obj, error, issues) = Run("[1 --5 2 99999999999 3]", strict: false);
        Assert.Null(error);
        var arr = Assert.IsType<PdfArray>(obj);
        Assert.Equal(5, arr.Count);
        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal(PdfErrorCode.InvalidNumber, i.Code));
        Assert.Equal(int.MaxValue, Assert.IsType<PdfInteger>(arr[3]).Value);
    }

    [Fact]
    public void UnknownKeywordIsRejectedStrictAndNullLenient()
    {
        Assert.Equal(PdfErrorCode.InvalidSyntax, Fails("endobj"));
        var (obj, error, issues) = Run("endobj", strict: false);
        Assert.Null(error);
        Assert.IsType<PdfNull>(obj);
        Assert.Single(issues);
    }

    // ---- references ----------------------------------------------------------------------------------------------

    [Fact]
    public void ReferenceIsRecognised()
    {
        var r = Ok<PdfReference>("12 3 R");
        Assert.Equal(12, r.Number);
        Assert.Equal(3, r.Generation);
        Assert.Equal(new PdfReference(12, 3), r);
        Assert.Equal("12 3 R", r.ToString());
    }

    [Fact]
    public void ReferenceInsideAnArrayDoesNotSwallowNeighbours()
    {
        var a = Ok<PdfArray>("[1 2 3 0 R 4 5]");
        Assert.Equal(5, a.Count);
        Assert.IsType<PdfInteger>(a[0]);
        Assert.IsType<PdfInteger>(a[1]);
        Assert.IsType<PdfReference>(a[2]);
        Assert.IsType<PdfInteger>(a[4]);
    }

    [Theory]
    [InlineData("[1 0 Rx]")]
    [InlineData("[1 0 R2]")]
    public void NotAReferenceWhenRIsFollowedByARegularCharacter(string text) => Assert.Equal(PdfErrorCode.InvalidSyntax, Fails(text));

    [Fact]
    public void SignedOrTooLongGenerationIsNotAReference()
    {
        var a = Ok<PdfArray>("[1 -2 3]");
        Assert.Equal(3, a.Count);
        Assert.IsType<PdfInteger>(Ok<PdfArray>("[1 123456 4]")[1]);
    }

    [Fact]
    public void GenerationAbove65535IsAViolation()
    {
        Assert.Equal(PdfErrorCode.InvalidReference, Fails("1 70000 R"));
        var (obj, error, issues) = Run("1 70000 R", strict: false);
        Assert.Null(error);
        Assert.Equal(65535, Assert.IsType<PdfReference>(obj).Generation);
        Assert.Single(issues);
    }

    // ---- names ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/Name1", "Name1")]
    [InlineData("/", "")]
    [InlineData("/A#20B", "A B")]
    [InlineData("/#41#42", "AB")]
    [InlineData("/A;Name_With-Various***Characters?", "A;Name_With-Various***Characters?")]
    [InlineData("/#E9", "é")]
    public void Names(string text, string expected) => Assert.Equal(expected, Ok<PdfName>(text).Value);

    [Theory]
    [InlineData("/A#")]
    [InlineData("/A#4")]
    [InlineData("/A#GG")]
    [InlineData("/A#00B")]
    public void BadNameEscapesAreRejectedStrictAndKeptLenient(string text)
    {
        Assert.Equal(PdfErrorCode.InvalidName, Fails(text));
        var (obj, error, issues) = Run(text, strict: false);
        Assert.Null(error);
        Assert.IsType<PdfName>(obj);
        Assert.NotEmpty(issues);
    }

    [Fact]
    public void NameLengthLimit()
    {
        var limits = new ParserLimits { MaxNameLength = 8 };
        Assert.IsType<PdfName>(Run("/12345678", limits: limits).Obj);
        var (_, error, _) = Run("/123456789", limits: limits);
        Assert.Equal(PdfLimit.NameLength, error?.Limit);

        // Escapes shrink the decoded name but not the scanned span: the raw span is bounded at 3x.
        Assert.Equal(PdfLimit.NameLength, Run("/" + string.Concat(Enumerable.Repeat("#41", 9)), limits: limits).Error?.Limit);
        Assert.IsType<PdfName>(Run("/" + string.Concat(Enumerable.Repeat("#41", 8)), limits: limits).Obj);
        Assert.Equal(PdfLimit.NameLength, Run("/" + new string('a', 40), limits: limits).Error?.Limit);
    }

    [Fact]
    public void LongNamesUseTheHeapPath()
    {
        string name = new('a', 300);
        Assert.Equal(name, Ok<PdfName>("/" + name).Value);
    }

    // ---- strings -------------------------------------------------------------------------------------------------

    private static string Str(PdfString s) => Encoding.Latin1.GetString(s.Value);

    [Theory]
    [InlineData("(abc)", "abc")]
    [InlineData("()", "")]
    [InlineData("(a(b)c)", "a(b)c")]
    [InlineData(@"(a\(b\)c)", "a(b)c")]
    [InlineData(@"(a\\b)", "a\\b")]
    [InlineData(@"(\n\r\t\b\f)", "\n\r\t\b\f")]
    [InlineData(@"(\101\60)", "A0")]
    [InlineData(@"(\0053)", "\u00053")]
    [InlineData(@"(\777)", "ÿ")]
    [InlineData("(a\\\nb)", "ab")]
    [InlineData("(a\\\r\nb)", "ab")]
    [InlineData("(a\\\rb)", "ab")]
    [InlineData("(a\r\nb)", "a\nb")]
    [InlineData("(a\rb)", "a\nb")]
    [InlineData(@"(\q)", "q")]
    public void LiteralStrings(string text, string expected)
    {
        var s = Ok<PdfString>(text);
        Assert.Equal(expected, Str(s));
        Assert.Equal(PdfStringFormat.Literal, s.Format);
    }

    [Theory]
    [InlineData("(abc")]
    [InlineData("(a(b)")]
    [InlineData(@"(abc\")]
    public void UnterminatedLiteralStrings(string text) => Assert.Equal(PdfErrorCode.UnexpectedEndOfData, Fails(text));

    [Theory]
    [InlineData("<48656C6C6F>", "Hello")]
    [InlineData("<48 65 6c 6C 6f>", "Hello")]
    [InlineData("<4>", "@")]
    [InlineData("<>", "")]
    [InlineData("<486>", "H`")]
    public void HexStrings(string text, string expected)
    {
        var s = Ok<PdfString>(text);
        Assert.Equal(expected, Str(s));
        Assert.Equal(PdfStringFormat.Hex, s.Format);
    }

    [Fact]
    public void HexStringErrors()
    {
        Assert.Equal(PdfErrorCode.InvalidString, Fails("<48ZZ>"));
        Assert.Equal(PdfErrorCode.UnexpectedEndOfData, Fails("<4865"));
        var (obj, error, issues) = Run("<48ZZ65>", strict: false);
        Assert.Null(error);
        Assert.Equal("He", Str(Assert.IsType<PdfString>(obj)));
        Assert.Equal(2, issues.Count);
    }

    [Fact]
    public void StringLengthLimit()
    {
        var limits = new ParserLimits { MaxStringLength = 4 };
        Assert.Equal("abcd", Str(Assert.IsType<PdfString>(Run("(abcd)", limits: limits).Obj)));
        Assert.Equal(PdfLimit.StringLength, Run("(abcde)", limits: limits).Error?.Limit);
        Assert.Equal(PdfLimit.StringLength, Run("<6162636465>", limits: limits).Error?.Limit);
        Assert.Equal(PdfLimit.StringLength, Run("(" + new string('x', 100) + ")", limits: new ParserLimits { MaxStringLength = 20 }).Error?.Limit);
    }

    [Fact]
    public void LongStringsGrowTheBuffer()
    {
        string text = new('x', 5000);
        Assert.Equal(text, Str(Ok<PdfString>("(" + text + ")")));
    }

    // ---- arrays, dictionaries, comments --------------------------------------------------------------------------

    [Fact]
    public void ArraysNestAndSkipComments()
    {
        var a = Ok<PdfArray>("[ 1 % comment ]\n [ /A (b) ] <</K 2>> null ]");
        Assert.Equal(4, a.Count);
        Assert.IsType<PdfArray>(a[1]);
        Assert.IsType<PdfDictionary>(a[2]);
    }

    [Fact]
    public void ArrayErrors()
    {
        Assert.Equal(PdfErrorCode.UnexpectedEndOfData, Fails("[1 2"));
        Assert.Equal(PdfErrorCode.InvalidSyntax, Fails("[1 > 2]"));
        Assert.Equal(PdfErrorCode.InvalidSyntax, Fails("[1 ) 2]"));
        Assert.Equal(PdfErrorCode.UnexpectedEndOfData, Fails("   "));
        Assert.Equal(PdfErrorCode.UnexpectedEndOfData, Fails("% only a comment"));
    }

    [Fact]
    public void DictionaryParsesInOrder()
    {
        var d = Ok<PdfDictionary>("<< /Z 1 /A 2 /M [3] /S (x) /R 4 0 R >>");
        Assert.Equal(["Z", "A", "M", "S", "R"], d.Keys.Select(k => k.Value));
        Assert.Equal(2, d.GetInteger("A"));
        Assert.Equal(new PdfReference(4, 0), d.GetReference("R"));
        Assert.Null(d["Nope"]);
        Assert.Null(d.GetName("A"));
    }

    [Fact]
    public void DictionaryErrors()
    {
        Assert.Equal(PdfErrorCode.UnexpectedEndOfData, Fails("<< /A 1"));
        Assert.Equal(PdfErrorCode.UnexpectedEndOfData, Fails("<<"));
        Assert.Equal(PdfErrorCode.InvalidSyntax, Fails("<< 1 2 >>"));
        Assert.Equal(PdfErrorCode.InvalidSyntax, Fails("<< /A 1 > "));
        Assert.Equal(PdfErrorCode.InvalidSyntax, Fails("<< /A >>"));
        var (obj, error, issues) = Run("<< /A >>", strict: false);
        Assert.Null(error);
        Assert.IsType<PdfNull>(Assert.IsType<PdfDictionary>(obj)["A"]);
        Assert.Single(issues);
    }

    [Fact]
    public void DuplicateKeysAreRejectedStrictAndLastWinsLenient()
    {
        Assert.Equal(PdfErrorCode.DuplicateKey, Fails("<< /A 1 /A 2 >>"));
        var (obj, error, issues) = Run("<< /A 1 /B 9 /A 2 >>", strict: false);
        Assert.Null(error);
        var d = Assert.IsType<PdfDictionary>(obj);
        Assert.Equal(2, d.Count);
        Assert.Equal(2, d.GetInteger("A"));
        Assert.Equal(PdfErrorCode.DuplicateKey, Assert.Single(issues).Code);
    }

    [Fact]
    public void DuplicateKeysAreFoundInLargeDictionariesToo()
    {
        string keys = string.Concat(Enumerable.Range(0, 30).Select(i => $"/K{i} {i} "));
        Assert.Equal(PdfErrorCode.DuplicateKey, Fails($"<< {keys}/K17 0 >>"));
        Assert.Equal(PdfErrorCode.DuplicateKey, Fails($"<< {keys}/K29 0 >>"));
        var d = Ok<PdfDictionary>($"<< {keys} >>");
        Assert.Equal(30, d.Count);
        Assert.Equal(25, d.GetInteger("K25"));
        Assert.Null(d["K30"]);
        var (obj, _, _) = Run($"<< {keys}/K20 99 >>", strict: false);
        Assert.Equal(99, Assert.IsType<PdfDictionary>(obj).GetInteger("K20"));
    }

    [Fact]
    public void KeysThatDifferOnlyByEscapingAreDuplicates()
    {
        // /A and /#41 decode to the same name: exactly the kind of duplicate a raw-text comparison would miss.
        Assert.Equal(PdfErrorCode.DuplicateKey, Fails("<< /A 1 /#41 2 >>"));
    }

    [Fact]
    public void NestingDepthLimit()
    {
        var limits = new ParserLimits { MaxNestingDepth = 4 };
        Assert.IsType<PdfArray>(Run("[[[[1]]]]", limits: limits).Obj);
        Assert.Equal(PdfLimit.NestingDepth, Run("[[[[[1]]]]]", limits: limits).Error?.Limit);
        Assert.Equal(PdfLimit.NestingDepth, Run("<< /A << /B << /C << /D << >> >> >> >> >>", limits: limits).Error?.Limit);

        // Even an absurd configured depth is capped, so hostile input cannot exhaust the stack.
        var big = new ParserLimits { MaxNestingDepth = int.MaxValue };
        string deep = new string('[', 100_000) + new string(']', 100_000);
        Assert.Equal(PdfLimit.NestingDepth, Run(deep, limits: big).Error?.Limit);
    }

    [Fact]
    public void ArrayAndDictionaryLengthLimits()
    {
        var limits = new ParserLimits { MaxArrayLength = 3, MaxDictionaryEntries = 2 };
        Assert.Equal(3, Assert.IsType<PdfArray>(Run("[1 2 3]", limits: limits).Obj).Count);
        Assert.Equal(PdfLimit.ArrayLength, Run("[1 2 3 4]", limits: limits).Error?.Limit);
        Assert.IsType<PdfDictionary>(Run("<< /A 1 /B 2 >>", limits: limits).Obj);
        Assert.Equal(PdfLimit.DictionaryEntries, Run("<< /A 1 /B 2 /C 3 >>", limits: limits).Error?.Limit);
    }

    [Fact]
    public void OverlongTokenIsALimitError() =>
        Assert.Equal(PdfLimit.TokenLength, Run(new string('a', 1000)).Error?.Limit);

    // ---- indirect objects ----------------------------------------------------------------------------------------

    private static (ParsedIndirectObject? Obj, PdfError? Error, IReadOnlyList<PdfError> Issues) Indirect(string text, bool strict = true, Func<PdfReference, long?>? resolver = null, int offset = 0)
    {
        var ctx = new ParseContext(ParserLimits.Default, strict);
        try
        {
            var parser = new ObjectParser(Encoding.Latin1.GetBytes(text), ctx);
            return (parser.ParseIndirectObject(offset, resolver), null, ctx.Issues);
        }
        catch (PdfParseException ex)
        {
            return (null, ex.Error, ctx.Issues);
        }
    }

    [Fact]
    public void PlainIndirectObject()
    {
        var (o, error, _) = Indirect("12 0 obj\n<< /A 1 >>\nendobj\n");
        Assert.Null(error);
        Assert.Equal(12, o!.Number);
        Assert.Equal(0, o.Generation);
        Assert.IsType<PdfDictionary>(o.Value);
        Assert.Equal(0, o.Start);
        Assert.Equal("12 0 obj\n<< /A 1 >>\nendobj".Length, o.End);
    }

    [Fact]
    public void ObjectWithoutSpacesBeforeValue()
    {
        var (o, error, _) = Indirect("5 1 obj<</A 1>>endobj");
        Assert.Null(error);
        Assert.Equal(1, o!.Generation);
    }

    [Fact]
    public void LeadingWhitespaceIsTolerated()
    {
        var (o, error, _) = Indirect("  \n3 0 obj 7 endobj", offset: 0);
        Assert.Null(error);
        Assert.Equal(3, o!.Start);
    }

    [Theory]
    [InlineData("x 0 obj null endobj")]
    [InlineData("1 obj null endobj")]
    [InlineData("1 0 foo null endobj")]
    [InlineData("1 0 objnull endobj")]
    [InlineData("12345678901 0 obj null endobj")]
    [InlineData("99999999999 0 obj null endobj")]
    [InlineData("1 123456 obj null endobj")]
    [InlineData("1 70000 obj null endobj")]
    [InlineData("10 0obj null endobj")]
    public void BadObjectHeaders(string text) => Assert.Equal(PdfErrorCode.ObjectHeaderMismatch, Indirect(text).Error?.Code);

    [Fact]
    public void ObjectOffsetOutsideTheBuffer()
    {
        Assert.Equal(PdfErrorCode.XrefOffsetOutOfRange, Indirect("1 0 obj null endobj", offset: 99).Error?.Code);
        Assert.Equal(PdfErrorCode.XrefOffsetOutOfRange, Indirect("1 0 obj null endobj", offset: -1).Error?.Code);
    }

    [Fact]
    public void MissingEndobjIsRejectedStrictAndRecordedLenient()
    {
        Assert.Equal(PdfErrorCode.MissingEndObj, Indirect("1 0 obj 5 ").Error?.Code);
        var (o, error, issues) = Indirect("1 0 obj 5 ", strict: false);
        Assert.Null(error);
        Assert.NotNull(o);
        Assert.Equal(PdfErrorCode.MissingEndObj, Assert.Single(issues).Code);
    }

    [Fact]
    public void EmptyObjectIsRejectedStrict()
    {
        Assert.Equal(PdfErrorCode.InvalidSyntax, Indirect("1 0 obj endobj").Error?.Code);
        var (o, error, _) = Indirect("1 0 obj endobj", strict: false);
        Assert.Null(error);
        Assert.IsType<PdfNull>(o!.Value);
    }

    [Fact]
    public void StreamKeywordAfterNonDictionary()
    {
        Assert.Equal(PdfErrorCode.InvalidStream, Indirect("1 0 obj 5 stream\nabc\nendstream endobj").Error?.Code);
        var (o, _, issues) = Indirect("1 0 obj 5 stream\nabc\nendstream endobj", strict: false);
        Assert.NotNull(o);
        Assert.NotEmpty(issues);
    }

    private static string StreamObj(string dict, string data, string eol = "\n") =>
        $"7 0 obj\n{dict}\nstream{eol}{data}\nendstream\nendobj\n";

    [Fact]
    public void StreamWithDirectLength()
    {
        var (o, error, _) = Indirect(StreamObj("<< /Length 5 >>", "hello"));
        Assert.Null(error);
        var s = Assert.IsType<PdfStream>(o!.Value);
        Assert.Equal("hello", Encoding.Latin1.GetString(s.RawData.Span));
        Assert.Equal(5, s.Dictionary.GetInteger("Length"));
    }

    [Fact]
    public void StreamWithCrLfAfterKeyword()
    {
        var (o, error, _) = Indirect(StreamObj("<< /Length 5 >>", "hello", "\r\n"));
        Assert.Null(error);
        Assert.Equal(5, Assert.IsType<PdfStream>(o!.Value).RawData.Length);
    }

    [Fact]
    public void StreamKeywordFollowedByLoneCrIsRejectedStrict()
    {
        string text = StreamObj("<< /Length 5 >>", "hello", "\r");
        Assert.Equal(PdfErrorCode.InvalidStream, Indirect(text).Error?.Code);
        var (o, error, issues) = Indirect(text, strict: false);
        Assert.Null(error);
        Assert.NotEmpty(issues);
        Assert.Equal(5, Assert.IsType<PdfStream>(o!.Value).RawData.Length);
    }

    [Fact]
    public void StreamKeywordWithoutEndOfLine()
    {
        string text = "7 0 obj\n<< /Length 5 >>\nstream hello\nendstream\nendobj";
        Assert.Equal(PdfErrorCode.InvalidStream, Indirect(text).Error?.Code);
        Assert.NotNull(Indirect(text, strict: false).Obj);
    }

    [Fact]
    public void StreamWithIndirectLengthUsesTheResolver()
    {
        string text = StreamObj("<< /Length 9 0 R >>", "hello");
        var (o, error, _) = Indirect(text, resolver: r => r.Number == 9 ? 5 : null);
        Assert.Null(error);
        Assert.Equal(5, Assert.IsType<PdfStream>(o!.Value).RawData.Length);

        Assert.Equal(PdfErrorCode.StreamLengthMismatch, Indirect(text).Error?.Code);
        Assert.Equal(PdfErrorCode.StreamLengthMismatch, Indirect(text, resolver: _ => null).Error?.Code);
        var (lenient, lenientError, _) = Indirect(text, strict: false, resolver: _ => null);
        Assert.Null(lenientError);
        Assert.Equal(5, Assert.IsType<PdfStream>(lenient!.Value).RawData.Length);
    }

    [Theory]
    [InlineData("<< /Length 3 >>")] // too short
    [InlineData("<< /Length 50 >>")] // too long
    [InlineData("<< /Length -1 >>")]
    [InlineData("<< >>")] // missing
    [InlineData("<< /Length (x) >>")]
    public void WrongLengthIsRejectedStrictAndRecoveredLenient(string dict)
    {
        string text = StreamObj(dict, "hello");
        Assert.Equal(PdfErrorCode.StreamLengthMismatch, Indirect(text).Error?.Code);
        var (o, error, issues) = Indirect(text, strict: false);
        Assert.Null(error);
        Assert.NotEmpty(issues);
        Assert.Equal("hello", Encoding.Latin1.GetString(Assert.IsType<PdfStream>(o!.Value).RawData.Span));
    }

    [Fact]
    public void LenientRecoveryTrimsCrLfBeforeEndstream()
    {
        string text = "7 0 obj\n<< /Length 99 >>\nstream\nhello\r\nendstream\nendobj";
        var (o, _, _) = Indirect(text, strict: false);
        Assert.Equal(5, Assert.IsType<PdfStream>(o!.Value).RawData.Length);
    }

    [Fact]
    public void StreamWithoutEndstreamIsFatal()
    {
        string text = "7 0 obj\n<< /Length 99 >>\nstream\nhello";
        Assert.Equal(PdfErrorCode.StreamLengthMismatch, Indirect(text).Error?.Code);
        Assert.Equal(PdfErrorCode.StreamLengthMismatch, Indirect(text, strict: false).Error?.Code);
    }

    [Fact]
    public void StreamMissingEndobj()
    {
        string text = "7 0 obj\n<< /Length 5 >>\nstream\nhello\nendstream\n";
        Assert.Equal(PdfErrorCode.MissingEndObj, Indirect(text).Error?.Code);
        var (o, error, issues) = Indirect(text, strict: false);
        Assert.Null(error);
        Assert.NotNull(o);
        Assert.NotEmpty(issues);
    }

    [Fact]
    public void ZeroLengthStream()
    {
        var (o, error, _) = Indirect("7 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj");
        Assert.Null(error);
        Assert.Equal(0, Assert.IsType<PdfStream>(o!.Value).RawData.Length);
    }

    [Fact]
    public void EndstreamInsideDataDoesNotConfuseADeclaredLength()
    {
        string payload = "ab endstream cd";
        var (o, error, _) = Indirect(StreamObj($"<< /Length {payload.Length} >>", payload));
        Assert.Null(error);
        Assert.Equal(payload.Length, Assert.IsType<PdfStream>(o!.Value).RawData.Length);
    }

    // ---- model ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ModelValidation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfReal(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfReal(double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfReal(1e300));
        Assert.Throws<ArgumentException>(() => new PdfName("Ā"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfReference(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfReference(1, 65536));
        Assert.Throws<ArgumentException>(() => PdfString.FromAscii("é"));
        Assert.Throws<ArgumentException>(() => new PdfDictionary([new(new PdfName("A"), PdfNull.Instance), new(new PdfName("A"), PdfNull.Instance)]));
        Assert.Equal(PdfBoolean.True, PdfBoolean.From(true));
        Assert.Equal(PdfBoolean.False, PdfBoolean.From(false));
    }

    [Fact]
    public void DictionaryWithAndWithout()
    {
        var d = new PdfDictionary([new(new PdfName("A"), new PdfInteger(1)), new(new PdfName("B"), new PdfInteger(2))]);
        var d2 = d.With("A", new PdfInteger(9)).With("C", new PdfInteger(3));
        Assert.Equal(["A", "B", "C"], d2.Keys.Select(k => k.Value));
        Assert.Equal(9, d2.GetInteger("A"));
        Assert.Equal(1, d.GetInteger("A")); // immutable
        Assert.Same(d, d.Without("Z"));
        Assert.Equal(["B"], d.Without("A").Keys.Select(k => k.Value));
        Assert.False(d.ContainsKey("Q"));
        Assert.True(d.TryGetValue("B", out var b) && b is PdfInteger);
        Assert.False(d.TryGetValue("Q", out _));
        Assert.Empty(PdfDictionary.Empty);
    }

    [Fact]
    public void DeepEqualsAndReferenceEnumeration()
    {
        var a = Ok<PdfDictionary>("<< /K [1 2.5 (s) <41> /N 3 0 R true null << /X 4 1 R >>] >>");
        var b = Ok<PdfDictionary>("<< /K [1 2.5 (s) <41> /N 3 0 R true null << /X 4 1 R >>] >>");
        Assert.True(PdfObjects.DeepEquals(a, b));
        Assert.False(PdfObjects.DeepEquals(a, Ok<PdfDictionary>("<< /K [1] >>")));
        Assert.False(PdfObjects.DeepEquals(a, Ok<PdfDictionary>("<< /J [1 2.5 (s) <41> /N 3 0 R true null << /X 4 1 R >>] >>")));
        Assert.False(PdfObjects.DeepEquals(PdfNull.Instance, PdfBoolean.True));
        Assert.False(PdfObjects.DeepEquals(null, PdfNull.Instance));
        Assert.False(PdfObjects.DeepEquals(Ok<PdfArray>("[[[[1]]]]"), Ok<PdfArray>("[[[[1]]]]"), maxDepth: 2));
        Assert.True(PdfObjects.DeepEquals(new PdfStream(a, new byte[] { 1 }), new PdfStream(b, new byte[] { 1 })));
        Assert.False(PdfObjects.DeepEquals(new PdfStream(a, new byte[] { 1 }), new PdfStream(b, new byte[] { 2 })));
        Assert.False(PdfObjects.DeepEquals(new PdfInteger(1), new PdfReal(1)));

        var refs = PdfObjects.EnumerateReferences(new PdfStream(a, ReadOnlyMemory<byte>.Empty)).ToList();
        Assert.Equal([new PdfReference(3, 0), new PdfReference(4, 1)], refs);
    }

    [Fact]
    public void ResultAccessors()
    {
        var ok = PdfResult.Ok(5);
        Assert.True(ok.IsSuccess);
        Assert.Equal(5, ok.Value);
        Assert.True(ok.TryGetValue(out int v) && v == 5);
        Assert.False(ok.TryGetError(out _));
        Assert.Throws<InvalidOperationException>(() => ok.Error);

        var fail = PdfResult.Fail<int>(new PdfError(PdfErrorCode.InvalidSyntax, "x", 12));
        Assert.False(fail.IsSuccess);
        Assert.Throws<InvalidOperationException>(() => fail.Value);
        Assert.False(fail.TryGetValue(out _));
        Assert.True(fail.TryGetError(out var e));
        Assert.Contains("InvalidSyntax at 12", e.ToString(), StringComparison.Ordinal);
        Assert.Contains("InvalidSyntax: x", new PdfError(PdfErrorCode.InvalidSyntax, "x").ToString(), StringComparison.Ordinal);
        Assert.True(ok == PdfResult.Ok(5));
        Assert.True(ok != fail);
        Assert.False(ok.Equals((object)"x"));
        Assert.Equal(ok.GetHashCode(), PdfResult.Ok(5).GetHashCode());
    }
}
