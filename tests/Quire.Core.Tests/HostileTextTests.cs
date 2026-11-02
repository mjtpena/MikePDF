using Quire.Core.Geometry;
using Quire.Core.Reading;
using Quire.Core.Text;
using Quire.Protocol;
using static Quire.Core.Tests.ScriptedEngine;

namespace Quire.Core.Tests;

/// <summary>
/// What the reader does with characters and replies that no honest engine would send: random boxes, random units, random flags, lists that
/// do not add up. A fixed seed, so a failure can be reproduced. The model and the reader must neither throw nor loop, and what they build must
/// keep its invariants.
/// </summary>
public sealed class HostileTextTests
{
    private static PageCharacters RandomPage(Random random, int count)
    {
        var units = new ushort[count];
        var lefts = new float[count];
        var bottoms = new float[count];
        var rights = new float[count];
        var tops = new float[count];
        var flags = new byte[count];
        float Coordinate() => random.Next(8) switch
        {
            0 => (float)((random.NextDouble() * 2e6) - 1e6),
            1 => 0f,
            _ => (float)(random.NextDouble() * 700),
        };

        for (var i = 0; i < count; i++)
        {
            units[i] = random.Next(10) switch
            {
                0 => (ushort)random.Next(0xD800, 0xE000),
                1 => (ushort)random.Next(0, 32),
                2 => (ushort)0xFB00,
                3 => (ushort)0x200B,
                _ => (ushort)random.Next(32, 0x2FFF),
            };
            var (a, b, c, d) = (Coordinate(), Coordinate(), Coordinate(), Coordinate());
            lefts[i] = a;
            rights[i] = c;
            bottoms[i] = b;
            tops[i] = d;
            flags[i] = (byte)random.Next(4);
        }

        return new PageCharacters(units, lefts, bottoms, rights, tops, flags, false);
    }

    [Fact]
    public void RandomPagesBuildModelsThatKeepTheirInvariants()
    {
        var random = new Random(0x51E5);
        for (var round = 0; round < 300; round++)
        {
            var count = random.Next(0, round < 250 ? 300 : 3000);
            var geometry = new PageGeometry(new PdfRect(0, 0, 612, 792), random.Next(3) == 0 ? new PdfRect(50, 60, 500, 700) : null, 90 * random.Next(4));
            var model = TextModelBuilder.Build(RandomPage(random, count), geometry, round);

            Assert.True(model.Length <= count * 2);
            foreach (var unit in Enum.GetValues<TextUnitKind>())
            {
                var bounds = model.Boundaries(unit);
                Assert.Equal(0, bounds[0]);
                Assert.Equal(model.Length, bounds[^1]);
                for (var i = 1; i < bounds.Length; i++)
                {
                    Assert.True(bounds[i] >= bounds[i - 1]);
                }
            }

            Assert.Equal(model.Text, model.GetText(0, model.Length));
            foreach (var atom in model.Atoms)
            {
                if (atom.Rect is { } rect)
                {
                    Assert.True(double.IsFinite(rect.X) && double.IsFinite(rect.Y) && double.IsFinite(rect.Width) && double.IsFinite(rect.Height));
                }

                Assert.DoesNotContain(atom.Text, t => char.IsSurrogate(t) && atom.Text.Length == 1);
            }

            var hit = model.HitTest(round, new PointD(random.NextDouble() * 700, random.NextDouble() * 900));
            Assert.True(model.Length == 0 ? hit == -1 : hit >= -1 && hit < model.Length);
            _ = model.LineRects(0, model.Length).ToList();
            var selection = new SelectionController(new LoadedPages(model));
            selection.SelectAll();
            Assert.NotNull(SelectionText.RectsOnPage(selection.Selection, 0, model));
            _ = TextModel.Combine([model, model]);
            var range = new TextRange(model, 0, model.Length);
            range.ExpandToEnclosingUnit(TextUnitKind.Word);
            range.Move(TextUnitKind.Line, 3);
            range.MoveEndpointByUnit(RangeEndpoint.End, TextUnitKind.Paragraph, -2);
        }
    }

    [Fact]
    public async Task TheReaderNeverThrowsForWhateverAnEngineSends()
    {
        var random = new Random(0xBAD);
        for (var round = 0; round < 400; round++)
        {
            ushort[] Kinds(int n) => [.. Enumerable.Range(0, n).Select(_ => (ushort)random.Next(0, 6))];
            uint[] Pages(int n) => [.. Enumerable.Range(0, n).Select(_ => (uint)random.Next(0, 40))];
            float[] Floats(int n) => [.. Enumerable.Range(0, n).Select(_ => (float)((random.NextDouble() * 2000) - 1000))];
            var engine = new ScriptedEngine
            {
                Outline = (_, _, _) => random.Next(5) == 0
                    ? ReadReply.Fail<GetOutlineReply>((Status)random.Next(0, 13))
                    : Ok(new GetOutlineReply
                    {
                        Total = (uint)random.Next(0, 300),
                        Titles = [.. Enumerable.Range(0, random.Next(0, 5)).Select(_ => new string((char)random.Next(0, 0xFFFF), random.Next(0, 5)).Replace("\0", string.Empty, StringComparison.Ordinal))],
                        Kinds = Kinds(random.Next(0, 5)),
                        Pages = Pages(random.Next(0, 5)),
                        Xs = Floats(random.Next(0, 5)),
                        Ys = Floats(random.Next(0, 5)),
                        HasChildren = Kinds(random.Next(0, 5)),
                    }),
                Links = (_, _, _) => Ok(new GetPageLinksReply
                {
                    Total = (uint)random.Next(0, 3000),
                    Lefts = Floats(random.Next(0, 4)),
                    Bottoms = Floats(random.Next(0, 4)),
                    Rights = Floats(random.Next(0, 4)),
                    Tops = Floats(random.Next(0, 4)),
                    Kinds = Kinds(random.Next(0, 4)),
                    Pages = Pages(random.Next(0, 4)),
                    Xs = Floats(random.Next(0, 4)),
                    Ys = Floats(random.Next(0, 4)),
                }),
                Text = (_, _, _) => Ok(new GetPageTextReply
                {
                    Total = (uint)random.Next(0, 5000),
                    Truncated = random.Next(2) == 0,
                    Units = [.. Enumerable.Range(0, random.Next(0, 6)).Select(_ => (ushort)random.Next(0, 65536))],
                    Lefts = Floats(random.Next(0, 6)),
                    Bottoms = Floats(random.Next(0, 6)),
                    Rights = Floats(random.Next(0, 6)),
                    Tops = Floats(random.Next(0, 6)),
                    Flags = Kinds(random.Next(0, 6)),
                }),
                Boxes = _ => Ok(new GetPageBoxesReply
                {
                    MediaLeft = Floats(1)[0], MediaBottom = Floats(1)[0], MediaRight = Floats(1)[0], MediaTop = Floats(1)[0],
                    HasCropBox = random.Next(2) == 0, CropLeft = Floats(1)[0], CropBottom = Floats(1)[0], CropRight = Floats(1)[0], CropTop = Floats(1)[0],
                    Rotation = (ushort)random.Next(0, 4),
                }),
                Search = (_, _, _, _) => Task.FromResult(Ok(new SearchPageReply
                {
                    Truncated = random.Next(2) == 0,
                    Starts = [.. Enumerable.Range(0, random.Next(0, 4)).Select(_ => (uint)random.Next(0, 200_000))],
                    Lengths = [.. Enumerable.Range(0, random.Next(0, 4)).Select(_ => (uint)random.Next(1, 200_000))],
                })),
            };
            var reader = new DocumentReader(engine, 40);
            var token = CancellationToken.None;
            _ = await reader.GetOutlineLevelAsync([], token);
            _ = await reader.GetOutlineWindowAsync([1, 2], random.Next(0, 90), 64, token);
            _ = await reader.GetLinksAsync(random.Next(0, 40), token);
            _ = await reader.GetCharactersAsync(random.Next(0, 40), token);
            _ = await reader.GetGeometryAsync(random.Next(0, 40), token);
            _ = await reader.SearchAsync(random.Next(0, 40), new SearchOptions("x"), token);
        }
    }
}
