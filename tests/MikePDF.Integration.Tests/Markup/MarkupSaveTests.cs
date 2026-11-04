#pragma warning disable CA1861 // test data: arrays written inline are the point of these cases

using MikePDF.Broker;
using MikePDF.Broker.Saving;
using MikePDF.Core.Editing.Journal;
using MikePDF.Core.Geometry;
using MikePDF.Ink;
using MikePDF.Integration.Tests.Saving;
using MikePDF.Integration.Tests.Support;
using MikePDF.Markup;
using MikePDF.Markup.Tests.Support;
using MikePDF.Protocol;
using MikePDF.Render.Imaging;

namespace MikePDF.Integration.Tests.Markup;

/// <summary>
/// W3-markup-core, the end to end proof: for every annotation kind, the model creates it, the real save pipeline writes it through the sandboxed
/// writer host, and a fresh sandboxed PDFium engine reopens the file and renders it, while a fresh in-process PDFium reports its subtype and
/// rectangle (annotation-core AC-12 and AC-21 as far as PDFium goes; Acrobat, Edge and pdf.js are the manual pass, R-15).
/// </summary>
public sealed class MarkupSaveTests(SandboxFixture sandbox)
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(90);

    private const double Scale = 2;

    private static readonly double[] Media = [0, 0, 612, 792];
    private static readonly double[] Crop = [50, 30, 562, 762];

    private static SaveCoordinator Coordinator(WriterHostClient host, SandboxFixture sandbox) =>
        new(new WriterHostSaveHost(host), new EngineSaveCheck(() => sandbox.Options()), new FileReplacer(5, TimeSpan.FromMilliseconds(50)));

    private static string Name(MarkupKind kind, bool typewriter = false) => typewriter ? "Typewriter" : kind.ToString();

    /// <summary>One annotation of each kind in its own cell of the displayed page, made the way the tools make them: from displayed points, through the page's geometry.</summary>
    private static List<(string Label, Annotation Annotation, int ExpectedSubtype)> MakeAll(Samples samples, PageFrame frame, int page)
    {
        var size = frame.DisplaySize;
        var cellWidth = size.Width / 3;
        var cellHeight = size.Height / 5;
        RectD Cell(int index) => new((index % 3 * cellWidth) + 12, (index / 3 * cellHeight) + 12, cellWidth - 24, cellHeight - 24);
        PointD User(RectD cell, double fx, double fy) => frame.ToUser(new PointD(cell.X + (cell.Width * fx), cell.Y + (cell.Height * fy)));
        Quad QuadOf(RectD cell, double top, double bottom) => new(User(cell, 0.05, top), User(cell, 0.95, top), User(cell, 0.05, bottom), User(cell, 0.95, bottom));
        var f = samples.Factory;
        var list = new List<(string, Annotation, int)>
        {
            ("Highlight", f.TextMarkup(MarkupKind.Highlight, page, [QuadOf(Cell(0), 0.2, 0.5), QuadOf(Cell(0), 0.5, 0.8)], opacity: 0.9), PdfiumProbe.Highlight),
            ("Underline", f.TextMarkup(MarkupKind.Underline, page, [QuadOf(Cell(1), 0.2, 0.5)], Rgb.Red, thickness: 2), PdfiumProbe.Underline),
            ("StrikeOut", f.TextMarkup(MarkupKind.StrikeOut, page, [QuadOf(Cell(2), 0.2, 0.5)], new Rgb(0, 0, 1), thickness: 2), PdfiumProbe.StrikeOut),
            ("Squiggly", f.TextMarkup(MarkupKind.Squiggly, page, [QuadOf(Cell(3), 0.2, 0.6)], Rgb.Red, thickness: 1.5), PdfiumProbe.Squiggly),
            ("Note", f.Note(page, frame, new PointD(Cell(4).X + 10, Cell(4).Y + 10), "Check this clause.", NoteIcon.Comment), PdfiumProbe.Text),
            ("FreeText", f.FreeTextOnPage(page, frame, Cell(5), "Text box\nwith two lines", FontFamily.Serif, size: 14, borderWidth: 1.5, borderColor: Rgb.Red, fill: new Rgb(1, 1, 0.8)), PdfiumProbe.FreeText),
            ("Typewriter", f.FreeTextOnPage(page, frame, new RectD(Cell(6).X, Cell(6).Y, 120, 20), "Typed", FontFamily.Mono, size: 12, typewriter: true), PdfiumProbe.FreeText),
            ("Rectangle", f.Box(MarkupKind.Rectangle, page, frame.ToUser(Cell(7)), Rgb.Red, 3, new Rgb(0.8, 0.9, 1), DashStyle.Dashed), PdfiumProbe.Square),
            ("Ellipse", f.Box(MarkupKind.Ellipse, page, frame.ToUser(Cell(8)), new Rgb(0, 0.5, 0), 2), PdfiumProbe.Circle),
            ("Line", f.Line(page, User(Cell(9), 0.1, 0.8), User(Cell(9), 0.9, 0.2), startEnding: LineEnding.Circle, endEnding: LineEnding.ClosedArrow, color: new Rgb(0, 0, 1), width: 2, interior: Rgb.Yellow), PdfiumProbe.Line),
            ("Arrow", f.Line(page, User(Cell(10), 0.1, 0.5), User(Cell(10), 0.9, 0.5), arrow: true, width: 3), PdfiumProbe.Line),
            ("Polygon", f.Polygon(page, [User(Cell(11), 0.1, 0.8), User(Cell(11), 0.5, 0.1), User(Cell(11), 0.9, 0.8)], Rgb.Red, 2, new Rgb(1, 0.8, 0.8), DashStyle.Dotted), PdfiumProbe.Polygon),
            ("PolyLine", f.PolyLine(page, [User(Cell(12), 0.1, 0.8), User(Cell(12), 0.5, 0.2), User(Cell(12), 0.9, 0.8)], LineEnding.OpenArrow, LineEnding.Diamond, Rgb.Black, 2), PdfiumProbe.PolyLine),
            ("Stamp", f.StampOnPage(page, frame, "Approved", new PointD(Cell(13).X, Cell(13).Y + 10)), PdfiumProbe.Stamp),
        };
        var strokeA = Enumerable.Range(0, 30).Select(i => new InkSample((float)User(Cell(14), 0.05 + (0.9 * i / 29), 0.5 + (0.3 * Math.Sin(i / 4.0))).X, (float)User(Cell(14), 0.05 + (0.9 * i / 29), 0.5 + (0.3 * Math.Sin(i / 4.0))).Y, 0.3f + (0.7f * i / 29))).ToArray();
        list.Add(("Ink", f.Ink(page, [new InkStroke("a", strokeA)], InkStyle.DefaultPen, new Rgb(0.1, 0.1, 0.6)), PdfiumProbe.Ink));
        return list;
    }

    private static (int Width, int Height) TileSize(PageFrame frame) => ((int)Math.Round(frame.DisplaySize.Width * Scale), (int)Math.Round(frame.DisplaySize.Height * Scale));

    /// <summary>The whole page as pixels, in tiles (the engine's tile limit is 1024 in each direction).</summary>
    private static async Task<byte[]> RenderAsync(SupervisedDocument document, int width, int height, uint page = 0)
    {
        const int Tile = 512;
        var bytes = new byte[width * height * 4];
        for (var y = 0; y < height; y += Tile)
        {
            for (var x = 0; x < width; x += Tile)
            {
                var w = Math.Min(Tile, width - x);
                var h = Math.Min(Tile, height - y);
                var result = await document.RenderTileAsync(new RenderTileArgs(page, (float)Scale, 0, x, y, (uint)w, (uint)h), 1);
                Assert.Equal(Status.Ok, result.Status);
                using var lease = result.Value!;
                for (var row = 0; row < h; row++)
                {
                    lease.Pixels.Slice(row * lease.Stride, w * 4).CopyTo(bytes.AsSpan((((y + row) * width) + x) * 4, w * 4));
                }
            }
        }

        return bytes;
    }

    private static async Task<byte[]> RenderFileAsync(EngineSupervisor engine, string path, int width, int height, uint page = 0)
    {
        var opened = await engine.OpenDocumentAsync(path);
        Assert.Equal(Status.Ok, opened.Status);
        await using var document = opened.Value!;
        return await RenderAsync(document, width, height, page);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(90, false)]
    [InlineData(180, false)]
    [InlineData(270, false)]
    [InlineData(0, true)]
    [InlineData(90, true)]
    [InlineData(180, true)]
    [InlineData(270, true)]
    public async Task EveryKindSavedThroughTheRealHostReopensInAFreshEngineWithItsSubtypeAndRectAndRenders(int rotate, bool cropBox)
    {
        using var workspace = new SaveWorkspace();
        var spec = new PageSpec(Media, cropBox ? Crop : null, rotate);
        var original = TestDocuments.Build(spec, PageSpec.Letter);
        var doc = workspace.Write("doc.pdf", original);
        var frame = Samples.Frame(rotate, cropBox ? Crop : null);
        var samples = new Samples();
        var editSession = new EditSession(new JournalStore(workspace.Journals), doc, "1.0.0.0");
        var markup = new MarkupSession(editSession.Model, samples.Factory, samples.Time);
        var made = MakeAll(samples, frame, 0);
        foreach (var (_, annotation, _) in made)
        {
            markup.Add(annotation);
        }

        await using var host = await sandbox.StartWriterHostAsync();
        using var held = File.OpenHandle(doc, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare);
        var result = await Coordinator(host, sandbox).SaveSessionAsync(editSession, doc, doc, SaveMode.Save, 2, held, cancellationToken: new CancellationTokenSource(Wait).Token);

        Assert.True(result.IsSaved, result.Reason + " " + result.Detail);
        var saved = File.ReadAllBytes(doc);
        Assert.True(Soundness.NoNewErrors(saved, original));
        Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original));
        Assert.Equal(0, host.RestartCount);

        // A fresh PDFium: the subtype and the rectangle of every annotation.
        Assert.Null(PdfiumProbe.UnavailableReason);
        var read = PdfiumProbe.Annotations(saved, 0)!;
        foreach (var (label, annotation, subtype) in made)
        {
            var file = annotation.FileRect; // what the file holds: the displayed rectangle, except for an icon on a turned page
            var found = read.Where(a => a.Subtype == subtype && Math.Abs(a.Left - file.Left) < 0.05 && Math.Abs(a.Bottom - file.Bottom) < 0.05
                && Math.Abs(a.Right - file.Right) < 0.05 && Math.Abs(a.Top - file.Top) < 0.05).ToList();
            Assert.True(found.Count == 1, $"{label} (rotate {rotate}, crop {cropBox}): expected one PDFium annotation of subtype {subtype} at {file}, found {found.Count} among {string.Join("; ", read.Select(r => $"{r.Subtype}@{r.Left:0.##},{r.Bottom:0.##},{r.Right:0.##},{r.Top:0.##}"))}");
            Assert.True(found[0].HasAppearance, $"{label}: no /AP in PDFium's view");
        }

        Assert.Equal(1, read.Count(static a => a.Subtype == PdfiumProbe.Popup)); // only the note has a popup
        Assert.Empty(PdfiumProbe.Annotations(saved, 1)!);

        // A fresh sandboxed engine: it opens the result and every annotation draws, inside its rectangle and nowhere else.
        var (width, height) = TileSize(frame);
        var baseFile = workspace.Write("base.pdf", original);
        await using var engine = await sandbox.StartAsync();
        var before = await RenderFileAsync(engine, baseFile, width, height);
        var after = await RenderFileAsync(engine, doc, width, height);
        PngDump.Write($"every-kind-rot{rotate}-{(cropBox ? "crop" : "media")}", after, width, height);
        var allowed = new bool[width * height];
        foreach (var (label, annotation, _) in made)
        {
            var shown = frame.ToDisplay(annotation.Rect);
            var (x0, y0, x1, y1) = (Math.Max(0, (int)Math.Floor((shown.X - 2) * Scale)), Math.Max(0, (int)Math.Floor((shown.Y - 2) * Scale)), Math.Min(width, (int)Math.Ceiling((shown.Right + 2) * Scale)), Math.Min(height, (int)Math.Ceiling((shown.Bottom + 2) * Scale)));
            var changed = 0;
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    allowed[(y * width) + x] = true;
                    changed += Differs(before, after, (y * width) + x) ? 1 : 0;
                }
            }

            Assert.True(changed >= 6, $"{label} (rotate {rotate}, crop {cropBox}) drew {changed} pixels in its rectangle");
        }

        var stray = Enumerable.Range(0, width * height).Count(i => !allowed[i] && Differs(before, after, i));
        Assert.True(stray == 0, $"{stray} pixels changed outside every annotation's rectangle");
    }

    private static bool Differs(byte[] a, byte[] b, int pixel)
    {
        var i = pixel * 4;
        return Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 12;
    }

    [Fact]
    public async Task AnnotationsOnTheSecondPageAreSavedOnTheSecondPage()
    {
        using var workspace = new SaveWorkspace();
        var original = TestDocuments.Build(PageSpec.Letter, new PageSpec(Media, null, 90));
        var doc = workspace.Write("doc.pdf", original);
        var samples = new Samples();
        var editSession = new EditSession(new JournalStore(workspace.Journals), doc, "1.0.0.0");
        var markup = new MarkupSession(editSession.Model, samples.Factory, samples.Time);
        markup.Add(samples.Highlight(0));
        markup.Add(samples.Stamp(1, "Draft"));
        await using var host = await sandbox.StartWriterHostAsync();
        using var held = File.OpenHandle(doc, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare);

        var result = await Coordinator(host, sandbox).SaveSessionAsync(editSession, doc, doc, SaveMode.Save, 2, held, cancellationToken: new CancellationTokenSource(Wait).Token);

        Assert.True(result.IsSaved, result.Reason + " " + result.Detail);
        var saved = File.ReadAllBytes(doc);
        Assert.Equal([PdfiumProbe.Highlight], PdfiumProbe.Annotations(saved, 0)!.Select(static a => a.Subtype));
        Assert.Equal([PdfiumProbe.Stamp], PdfiumProbe.Annotations(saved, 1)!.Select(static a => a.Subtype));
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task TextBoxesAndStampsAreUprightOnARotatedPage(int rotate)
    {
        // The same displayed layout on an unrotated page and on a page turned by /Rotate: the two renders show the same picture (free-text 3.6, stamps 3.5; FT-05, ST-04).
        double[] square = [0, 0, 420, 420];
        using var workspace = new SaveWorkspace();
        var reference = await RenderWithAnnotationsAsync(workspace, "ref", 0, square);
        var turned = await RenderWithAnnotationsAsync(workspace, "turned", rotate, square);

        var similarity = Ssim.MeanSsim(new BgraImage(reference.Width, reference.Height, reference.Pixels).Luma(), new BgraImage(turned.Width, turned.Height, turned.Pixels).Luma(), reference.Width, reference.Height);

        PngDump.Write($"upright-ref", reference.Pixels, reference.Width, reference.Height);
        PngDump.Write($"upright-rot{rotate}", turned.Pixels, turned.Width, turned.Height);
        Assert.True(similarity >= 0.98, $"rotate {rotate}: SSIM {similarity:0.0000} against the unrotated page");
    }

    private async Task<(byte[] Pixels, int Width, int Height)> RenderWithAnnotationsAsync(SaveWorkspace workspace, string name, int rotate, double[] box)
    {
        var original = TestDocuments.Build(new PageSpec(box, null, rotate));
        var doc = workspace.Write(name + ".pdf", original);
        var frame = Samples.Frame(rotate, null, box);
        var samples = new Samples();
        var editSession = new EditSession(new JournalStore(Path.Combine(workspace.Journals, name)), doc, "1.0.0.0");
        var markup = new MarkupSession(editSession.Model, samples.Factory, samples.Time);
        markup.Add(samples.Factory.FreeTextOnPage(0, frame, new RectD(30, 30, 200, 40), "Upright text, even on a turned page", FontFamily.Sans, size: 14, borderWidth: 1, borderColor: Rgb.Red, fill: new Rgb(1, 1, 0.85)));
        markup.Add(samples.Factory.StampOnPage(0, frame, "Confidential", new PointD(30, 120)));
        markup.Add(samples.Factory.StampOnPage(0, frame, "Draft", new PointD(30, 200), userTurns: 1));
        markup.Add(samples.Factory.StampOnPage(0, frame, "Final", new PointD(200, 250), userTurns: 2));
        await using var host = await sandbox.StartWriterHostAsync();
        using var held = File.OpenHandle(doc, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare);
        var result = await Coordinator(host, sandbox).SaveSessionAsync(editSession, doc, doc, SaveMode.Save, 1, held, cancellationToken: new CancellationTokenSource(Wait).Token);
        Assert.True(result.IsSaved, result.Reason + " " + result.Detail);
        var (width, height) = TileSize(frame);
        await using var engine = await sandbox.StartAsync();
        return (await RenderFileAsync(engine, doc, width, height), width, height);
    }

    [Fact]
    public async Task ARepliesAndAStateDrawNothingOfTheirOwn()
    {
        using var workspace = new SaveWorkspace();
        var withoutReplies = await RenderNoteAsync(workspace, "a", replies: false);
        var withReplies = await RenderNoteAsync(workspace, "b", replies: true);

        Assert.Equal(withoutReplies.Pixels, withReplies.Pixels); // SN-11: no stray icons
    }

    private async Task<(byte[] Pixels, int Width, int Height)> RenderNoteAsync(SaveWorkspace workspace, string name, bool replies)
    {
        var original = TestDocuments.Build(PageSpec.Letter);
        var doc = workspace.Write(name + ".pdf", original);
        var frame = Samples.Frame();
        var samples = new Samples();
        var editSession = new EditSession(new JournalStore(Path.Combine(workspace.Journals, name)), doc, "1.0.0.0");
        var markup = new MarkupSession(editSession.Model, samples.Factory, samples.Time);
        var note = markup.Add(samples.Note());
        if (replies)
        {
            markup.Reply(note.Id, "I agree");
            markup.SetReviewState(note.Id, ReviewState.Accepted);
        }

        await using var host = await sandbox.StartWriterHostAsync();
        using var held = File.OpenHandle(doc, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare);
        var result = await Coordinator(host, sandbox).SaveSessionAsync(editSession, doc, doc, SaveMode.Save, 1, held, cancellationToken: new CancellationTokenSource(Wait).Token);
        Assert.True(result.IsSaved, result.Reason + " " + result.Detail);
        var (width, height) = TileSize(frame);
        await using var engine = await sandbox.StartAsync();
        return (await RenderFileAsync(engine, doc, width, height), width, height);
    }

    [Fact]
    public async Task ASecondSaveThatRestylesMovesAndDeletesWhatTheFirstWroteWorksAndUndoAfterSaveIsAChangeAgainstTheSavedFile()
    {
        using var workspace = new SaveWorkspace();
        var original = TestDocuments.Build(PageSpec.Letter);
        var doc = workspace.Write("doc.pdf", original);
        var samples = new Samples();
        var editSession = new EditSession(new JournalStore(workspace.Journals), doc, "1.0.0.0");
        var markup = new MarkupSession(editSession.Model, samples.Factory, samples.Time);
        var rectangle = markup.Add(samples.Rectangle());
        var line = markup.Add(samples.Line());
        var note = markup.Add(samples.Note());
        await using var host = await sandbox.StartWriterHostAsync();
        var coordinator = Coordinator(host, sandbox);
        using (var held = File.OpenHandle(doc, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare))
        {
            Assert.True((await coordinator.SaveSessionAsync(editSession, doc, doc, SaveMode.Save, 1, held)).IsSaved);
        }

        var firstSave = File.ReadAllBytes(doc);
        Assert.Equal(4, PdfiumProbe.Annotations(firstSave, 0)!.Count); // three annotations and the note's popup

        Assert.True(markup.Update(rectangle.Id, a => a with { Color = new Rgb(0, 1, 0), Width = 6 }));
        Assert.True(markup.Move(line.Id, 30, -20));
        Assert.True(markup.Delete(note.Id));
        using (var held = File.OpenHandle(doc, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare))
        {
            var second = await coordinator.SaveSessionAsync(editSession, doc, doc, SaveMode.Save, 1, held);
            Assert.True(second.IsSaved, second.Reason + " " + second.Detail);
        }

        var secondSave = File.ReadAllBytes(doc);
        Assert.True(Soundness.NoNewErrors(secondSave, firstSave));
        var after = PdfiumProbe.Annotations(secondSave, 0)!;
        Assert.Equal([PdfiumProbe.Line, PdfiumProbe.Square], after.Select(static a => a.Subtype).Order()); // the note and its popup are gone
        var movedLine = after.Single(static a => a.Subtype == PdfiumProbe.Line);
        Assert.Equal(markup.Store.Get(line.Id)!.Rect.Left, movedLine.Left, 0.05);

        // Undo after the save: the delete is undone by adding the note again, relative to the saved file.
        Assert.Equal(MikePDF.Core.Editing.UndoOutcome.Done, markup.Undo());
        Assert.True(markup.Store.Contains(note.Id));
        using (var held = File.OpenHandle(doc, FileMode.Open, FileAccess.Read, EngineSupervisor.DocumentShare))
        {
            var third = await coordinator.SaveSessionAsync(editSession, doc, doc, SaveMode.Save, 1, held);
            Assert.True(third.IsSaved, third.Reason + " " + third.Detail);
        }

        Assert.Equal(4, PdfiumProbe.Annotations(File.ReadAllBytes(doc), 0)!.Count);
    }
}
