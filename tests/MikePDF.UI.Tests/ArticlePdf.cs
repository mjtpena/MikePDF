using System.Globalization;
using System.Text;

namespace MikePDF.UI.Tests;

/// <summary>
/// A multi-page document with real content, for the journeys and for the screenshots: a cover, a contents page whose entries are links, four
/// chapters of prose with headings, a quotation, a table and a bar chart, a nested bookmark tree, and a link to a web address on the last page.
/// Everything the tests expect (page numbers, how often a word occurs) is worked out while the pages are laid out, so the numbers cannot drift
/// away from the text.
/// </summary>
internal sealed class ArticlePdf
{
    public const string Title = "The Reader's Handbook";
    public const string Subtitle = "Notes on paper, type and patience";
    public const string Address = "https://example.org/reading-notes";
    public const string SearchWord = "margin";

    private const double Left = 72;
    private const double Right = 540;
    private const double Top = 720;
    private const double Bottom = 90;

    private static readonly (string Chapter, (string Section, string[] Paragraphs)[] Sections)[] Chapters =
    [
        ("Why Margins Matter",
        [
            ("The Edge of the Page",
            [
                "A page is more than the words printed on it. The empty space around the text, the margin, is part of the design, and a careful designer spends as much effort on it as on the letters. Wide margins rest the eye and give a thumb somewhere to land. Narrow margins pack more onto the sheet, but they make a book feel hurried, as if the printer were short of paper.",
                "In older manuscripts the margin was never really empty. Readers wrote in it, scribes corrected their slips in it, and illuminators let small beasts wander along its edge. A margin was the place where the reader answered the book. Today we keep the habit when we underline a sentence or leave a question mark beside a claim that does not seem quite right.",
            ]),
            ("Room to Think",
            [
                "Room to think is the quiet purpose of white space. When a line of text is neither too long nor too crowded, the mind can follow it without effort and rest for a moment at the end of each paragraph. Typographers talk about measure, the width of a column, and a good measure is about sixty-six characters. Longer lines tire the eye as it hunts for the start of the next one.",
                "A reader who zooms a document to fill the window is changing the measure without meaning to. A line that was comfortable on paper becomes a long road across a wide monitor. That is one reason a good reader lets you fit the page to the window, or the width of the page, and choose for yourself where the edge of the text should fall.",
            ]),
        ]),
        ("Type and Its Habits",
        [
            ("Serifs and Sans",
            [
                "Serif letters carry small feet at the ends of their strokes. Some say the feet guide the eye along the line; others say they are only a habit inherited from stone carving and the chisel. Sans serif letters drop the feet and look plain and open on a screen, where the pixels are coarse and fine detail is lost. Neither style is right for every purpose, and the best choice depends on the size, the medium and the reader.",
                "On paper at ten or eleven points a serif face usually reads comfortably for hours. On a phone, a sans serif face often looks cleaner. A reading program cannot change the typeface of a PDF, because the file carries its own fonts, but it can draw them faithfully and let the reader enlarge them until they are easy to see.",
            ]),
            ("The Rhythm of a Line",
            [
                "Every line of text has a rhythm. Words of different length follow one another, spaces open and close, and tall letters rise like fence posts above the line. When a paragraph is set well the rhythm is steady and invisible. When it is set badly, rivers of white space run down the page and the eye trips over them. Justified text, with both edges straight, is the usual cause.",
            ]),
            ("Numbers in Tables",
            [
                "Numbers deserve their own care. In a table, digits should be the same width so that columns of figures line up, and the decimal points should sit one above another. The table below lists the sample chapters of this handbook in the plain style an accountant would approve.",
            ]),
        ]),
        ("Paper, Ink and Time",
        [
            ("A Short History of the Leaf",
            [
                "The earliest books were scrolls of papyrus, rolled around a stick and read a handspan at a time. The codex, a stack of folded leaves sewn along one edge, replaced the scroll because it was easier to carry, easier to search and cheaper to make. Parchment, the prepared skin of a calf or a sheep, gave way slowly to paper, which arrived from the east and was made from rags pounded to a pulp.",
                "Printing changed the price of a book more than it changed the book. A volume that once took a scribe a year could be produced in a week, and the margin shrank a little as printers learned how much paper cost. Yet the reader's old habits survived: the contents page, the index, the page number and the habit of turning back to check a passage.",
            ]),
            ("Keeping Things Well",
            [
                "Paper keeps well when it is cool, dry and dark. Acid in cheap pulp slowly eats the sheet, and sunlight bleaches the ink. A digital file has its own enemies: formats that fall out of use, drives that fail, and passwords that are forgotten. A careful reader keeps copies, in more than one place, and opens old files now and then to see that they still open.",
            ]),
        ]),
        ("Reading Without Hurry",
        [
            ("Slow Reading",
            [
                "Slow reading is not a matter of moving the eyes slowly. It is the habit of stopping at a sentence that deserves it, of going back, of finding the word again with the search box when the memory fails. A program can help by staying out of the way: no pop-ups, no banners, no pleading for attention while the reader is in the middle of a thought.",
            ]),
            ("Notes Beside the Text",
            [
                "Notes beside the text are the oldest form of conversation with a book. A reader who selects a sentence and copies it into a notebook is doing what readers have always done, only faster. The copy should be exact, with the line breaks sensible and the page number kept, so that the quotation can be found again.",
                "That is the end of the main text. If a link in the contents took you straight to a chapter, or a thumbnail showed you the shape of the whole document at a glance, the tools are doing their job. A short glossary follows.",
            ]),
        ]),
        ("A Small Glossary",
        [
            ("Terms of the Trade",
            [
                "Ascender: the part of a letter such as b, d or h that rises above the height of the lower case x. Descender: the part of a letter such as g, p or y that falls below the line on which the letters sit.",
                "Baseline: the invisible line on which most letters stand. A good reader draws it straight, however far the page is zoomed. Em: a unit of length equal to the size of the type, so that one em in a line of eleven point text is eleven points wide.",
                "Gutter: the space where two facing pages meet, or the gap between two columns of text. Leading: the distance from one baseline to the next, named for the strips of lead a compositor once slipped between lines of metal type.",
                "Kerning: the adjustment of the space between a pair of letters, so that a capital T and a lower case o sit close together without touching. Ligature: two or more letters joined into a single shape, as in the fi of a fine finish.",
            ]),
            ("More Terms",
            [
                "Measure: the width of a column of text, usually counted in characters. Orphan: a single line of a paragraph left at the foot of a page, with the rest of the paragraph on the next. Widow: a last line left alone at the top of a page.",
                "Recto: the right hand page of an open book, usually odd numbered. Verso: the left hand page, usually even numbered. Rag: the uneven edge of text that is not justified; a good rag looks even without being ruled.",
                "Small capitals: capital letters drawn to the height of the lower case, used for abbreviations and the first words of a chapter. Tracking: the adjustment of space across a whole run of letters, rather than a single pair as in kerning.",
            ]),
        ]),
    ];

    private readonly List<string> _objects = [];
    private readonly List<Page> _pages = [];
    private readonly List<Heading> _headings = [];
    private readonly List<string> _allText = [];
    private Page _page = null!;
    private double _y;

    private sealed class Page
    {
        public StringBuilder Content { get; } = new();

        public List<(double X0, double Y0, double X1, double Y1, string Action)> Links { get; } = [];

        public int ObjectNumber { get; set; }
    }

    private sealed record Heading(int Level, string Text, int PageIndex, double Y, int Parent);

    private ArticlePdf()
    {
    }

    /// <summary>The chapter titles in order, each with its page (1-based).</summary>
    public List<(string Title, int Page)> ChapterPages { get; } = [];

    /// <summary>The section titles in order, each with its page (1-based).</summary>
    public List<(string Title, int Page)> SectionPages { get; } = [];

    public int PageCount => _pages.Count;

    /// <summary>How often <see cref="SearchWord"/> occurs in the text of the pages, in any case (what a search that ignores case finds).</summary>
    public int SearchWordCount { get; private set; }

    /// <summary>How often <see cref="SearchWord"/> occurs exactly as written (lower case): what a search that matches case finds.</summary>
    public int SearchWordCountExact { get; private set; }

    /// <summary>The page (1-based) of the contents.</summary>
    public const int ContentsPage = 2;

    /// <summary>The page (1-based) with the web address.</summary>
    public int AddressPage => _pages.Count;

    public static ArticlePdf Create(string directory, string name, out string path)
    {
        Directory.CreateDirectory(directory);
        var article = new ArticlePdf();
        article.Layout();
        path = Path.Combine(directory, name);
        File.WriteAllBytes(path, article.Build());
        return article;
    }

    // ---- layout -------------------------------------------------------------------------------------------------------------------

    private void Layout()
    {
        NewPage();
        Cover();
        NewPage(); // the contents, filled in once the pages of the chapters are known
        var contentsPage = _page;
        var chapters = new List<(string Title, int PageIndex, double Y, List<(string Title, int PageIndex, double Y)> Sections)>();
        foreach (var (chapter, sections) in Chapters)
        {
            NewPage();
            _y = Top;
            var chapterEntry = (Title: chapter, PageIndex: _pages.Count - 1, Y: _y, Sections: new List<(string, int, double)>());
            ChapterHeading(chapter);
            _headings.Add(new Heading(0, chapter, _pages.Count - 1, chapterEntry.Y, -1));
            ChapterPages.Add((chapter, _pages.Count));
            var parent = _headings.Count - 1;
            foreach (var (section, paragraphs) in sections)
            {
                Ensure(90);
                var sectionEntry = (Title: section, PageIndex: _pages.Count - 1, Y: _y);
                SectionHeading(section);
                chapterEntry.Sections.Add(sectionEntry);
                _headings.Add(new Heading(1, section, sectionEntry.PageIndex, sectionEntry.Y, parent));
                SectionPages.Add((section, sectionEntry.PageIndex + 1));
                foreach (var paragraph in paragraphs)
                {
                    Paragraph(paragraph);
                }

                if (section == "Numbers in Tables")
                {
                    Table();
                }

                if (section == "The Edge of the Page")
                {
                    Quotation("A margin is the place where the reader answers the book.");
                }

                if (section == "Keeping Things Well")
                {
                    BarChart();
                }
            }

            chapters.Add(chapterEntry);
        }

        Ensure(60);
        AddressLine();
        FillContents(contentsPage, chapters);
        for (var i = 0; i < _pages.Count; i++)
        {
            Footer(_pages[i], i + 1);
        }

        SearchWordCount = _allText.Sum(t => CountOf(t, SearchWord, StringComparison.OrdinalIgnoreCase));
        SearchWordCountExact = _allText.Sum(t => CountOf(t, SearchWord, StringComparison.Ordinal));
    }

    private void NewPage()
    {
        _page = new Page();
        _pages.Add(_page);
        _y = Top;
    }

    private void Ensure(double needed)
    {
        if (_y - needed < Bottom)
        {
            NewPage();
        }
    }

    private void Text(Page page, string font, double size, double x, double y, string text, string colour = "0.12 0.12 0.14 rg")
    {
        _allText.Add(text);
        page.Content.Append(colour).Append(" BT /").Append(font).Append(' ').Append(Num(size)).Append(" Tf ").Append(Num(x)).Append(' ').Append(Num(y)).Append(" Td (").Append(Escape(text)).Append(") Tj ET\n");
    }

    private void Cover()
    {
        _page.Content.Append("0.10 0.35 0.42 rg 0 430 612 362 re f\n");
        _page.Content.Append("0.93 0.72 0.20 rg 72 410 120 6 re f\n");
        Text(_page, "F2", 40, 72, 640, Title, "1 1 1 rg");
        Text(_page, "F3", 18, 72, 590, Subtitle, "0.92 0.96 0.97 rg");
        Text(_page, "F1", 12, 72, 360, "A short guide for people who read on screens", "0.25 0.25 0.28 rg");
        Text(_page, "F1", 12, 72, 340, "and would like the screen to behave more like a book.", "0.25 0.25 0.28 rg");
        Text(_page, "F1", 10, 72, 120, "Sample document for MikePDF. Written for testing; the opinions are modest.", "0.45 0.45 0.48 rg");
    }

    private void ChapterHeading(string title)
    {
        Text(_page, "F2", 26, Left, _y - 26, title, "0.10 0.35 0.42 rg");
        _page.Content.Append("0.93 0.72 0.20 rg ").Append(Num(Left)).Append(' ').Append(Num(_y - 38)).Append(" 90 3 re f\n");
        _y -= 66;
    }

    private void SectionHeading(string title)
    {
        _y -= 6;
        Text(_page, "F2", 14, Left, _y - 12, title);
        _y -= 28;
    }

    private void Paragraph(string text)
    {
        const double size = 11;
        const double leading = 15.5;
        var lines = Wrap(text, size, Right - Left);
        Ensure(Math.Min(lines.Count, 3) * leading + 6);
        foreach (var line in lines)
        {
            if (_y - leading < Bottom)
            {
                NewPage();
            }

            Text(_page, "F4", size, Left, _y - size, line);
            _y -= leading;
        }

        _y -= 8;
    }

    private void Quotation(string text)
    {
        Ensure(70);
        _page.Content.Append("0.94 0.95 0.96 rg ").Append(Num(Left)).Append(' ').Append(Num(_y - 50)).Append(' ').Append(Num(Right - Left)).Append(" 50 re f\n");
        _page.Content.Append("0.10 0.35 0.42 rg ").Append(Num(Left)).Append(' ').Append(Num(_y - 50)).Append(" 4 50 re f\n");
        Text(_page, "F5", 15, Left + 18, _y - 30, text, "0.10 0.35 0.42 rg");
        _y -= 70;
    }

    private void Table()
    {
        Ensure(120);
        var rows = new (string, string, string)[]
        {
            ("Chapter", "Pages", "Words"),
            ("Why Margins Matter", "2", "410"),
            ("Type and Its Habits", "2", "395"),
            ("Paper, Ink and Time", "2", "360"),
            ("Reading Without Hurry", "1", "260"),
        };
        for (var i = 0; i < rows.Length; i++)
        {
            var y = _y - 16 - (i * 18);
            var font = i == 0 ? "F2" : "F1";
            if (i == 0)
            {
                _page.Content.Append("0.88 0.92 0.93 rg ").Append(Num(Left)).Append(' ').Append(Num(y - 5)).Append(' ').Append(Num(Right - Left)).Append(" 18 re f\n");
            }

            Text(_page, font, 10.5, Left + 8, y, rows[i].Item1);
            Text(_page, font, 10.5, 380, y, rows[i].Item2);
            Text(_page, font, 10.5, 460, y, rows[i].Item3);
        }

        _y -= 16 + (rows.Length * 18) + 10;
    }

    private void BarChart()
    {
        Ensure(150);
        Text(_page, "F2", 11, Left, _y - 12, "Hours a sheet survives, by storage");
        var bars = new (string Label, double Value, string Colour)[]
        {
            ("Cool, dry, dark", 160, "0.10 0.35 0.42 rg"),
            ("A sunny shelf", 55, "0.93 0.72 0.20 rg"),
            ("A damp cellar", 30, "0.75 0.30 0.25 rg"),
        };
        for (var i = 0; i < bars.Length; i++)
        {
            var y = _y - 38 - (i * 26);
            Text(_page, "F1", 10, Left, y, bars[i].Label);
            _page.Content.Append(bars[i].Colour).Append(' ').Append(Num(Left + 110)).Append(' ').Append(Num(y - 3)).Append(' ').Append(Num(bars[i].Value * 2)).Append(" 14 re f\n");
        }

        _y -= 38 + (bars.Length * 26) + 6;
    }

    private void AddressLine()
    {
        var y = _y - 14;
        Text(_page, "F1", 11, Left, y, "Reading notes online:");
        const double x = 190;
        Text(_page, "F1", 11, x, y, Address, "0.05 0.30 0.70 rg");
        var width = Address.Length * 11 * 0.5;
        _page.Content.Append("0.05 0.30 0.70 RG 0.6 w ").Append(Num(x)).Append(' ').Append(Num(y - 2)).Append(" m ").Append(Num(x + width)).Append(' ').Append(Num(y - 2)).Append(" l S\n");
        _page.Links.Add((x, y - 3, x + width, y + 11, "<< /S /URI /URI (" + Escape(Address) + ") >>"));
        _y -= 30;
    }

    private void FillContents(Page page, List<(string Title, int PageIndex, double Y, List<(string Title, int PageIndex, double Y)> Sections)> chapters)
    {
        var y = Top;
        Text(page, "F2", 26, Left, y - 26, "Contents", "0.10 0.35 0.42 rg");
        page.Content.Append("0.93 0.72 0.20 rg ").Append(Num(Left)).Append(' ').Append(Num(y - 38)).Append(" 90 3 re f\n");
        y -= 80;
        foreach (var chapter in chapters)
        {
            ContentsLine(page, y, "F2", 13, 0, chapter.Title, chapter.PageIndex, chapter.Y);
            y -= 24;
            foreach (var section in chapter.Sections)
            {
                ContentsLine(page, y, "F4", 11.5, 22, section.Title, section.PageIndex, section.Y);
                y -= 19;
            }

            y -= 10;
        }
    }

    private void ContentsLine(Page page, double y, string font, double size, double indent, string title, int pageIndex, double top)
    {
        Text(page, font, size, Left + indent, y, title);
        Text(page, "F4", size, 510, y, (pageIndex + 1).ToString(CultureInfo.InvariantCulture));
        page.Links.Add((Left, y - 4, Right, y + size, "DEST " + pageIndex.ToString(CultureInfo.InvariantCulture) + " " + Num(top)));
    }

    private void Footer(Page page, int number)
    {
        if (number == 1)
        {
            return;
        }

        Text(page, "F1", 9, 296, 50, number.ToString(CultureInfo.InvariantCulture), "0.45 0.45 0.48 rg");
    }

    // ---- text helpers -----------------------------------------------------------------------------------------------------------------

    // Times-Roman advance widths (thousandths of an em) for the printable ASCII characters 32 to 126, from the Adobe core font metrics.
    private static readonly int[] TimesWidths =
    [
        250, 333, 408, 500, 500, 833, 778, 180, 333, 333, 500, 564, 250, 333, 250, 278,
        500, 500, 500, 500, 500, 500, 500, 500, 500, 500, 278, 278, 564, 564, 564, 444,
        921, 722, 667, 667, 722, 611, 556, 722, 722, 333, 389, 722, 611, 889, 722, 722,
        556, 722, 667, 556, 611, 722, 722, 944, 722, 722, 611, 333, 278, 333, 469, 500,
        333, 444, 500, 444, 500, 444, 333, 500, 500, 278, 278, 500, 278, 778, 500, 500,
        500, 500, 333, 389, 278, 500, 500, 722, 500, 500, 444, 480, 200, 480, 541,
    ];

    private static double WidthOf(string text, double size) => text.Sum(c => c is >= ' ' and <= '~' ? TimesWidths[c - ' '] : 500) * size / 1000.0;

    private static List<string> Wrap(string text, double size, double width)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var word in text.Split(' '))
        {
            if (line.Length > 0 && WidthOf(line + " " + word, size) > width)
            {
                lines.Add(line.ToString());
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            lines.Add(line.ToString());
        }

        return lines;
    }

    private static int CountOf(string text, string word, StringComparison comparison)
    {
        var count = 0;
        for (var at = text.IndexOf(word, comparison); at >= 0; at = text.IndexOf(word, at + word.Length, comparison))
        {
            count++;
        }

        return count;
    }

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Escape(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);

    // ---- the file ---------------------------------------------------------------------------------------------------------------------

    private int Add(string body)
    {
        _objects.Add(body);
        return _objects.Count;
    }

    private int Reserve() => Add(string.Empty);

    private byte[] Build()
    {
        var catalog = Reserve();
        var pages = Reserve();
        var fonts = new[]
        {
            Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
            Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"),
            Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Oblique /Encoding /WinAnsiEncoding >>"),
            Add("<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>"),
            Add("<< /Type /Font /Subtype /Type1 /BaseFont /Times-Italic /Encoding /WinAnsiEncoding >>"),
        };
        var resources = $"/Resources << /Font << /F1 {fonts[0]} 0 R /F2 {fonts[1]} 0 R /F3 {fonts[2]} 0 R /F4 {fonts[3]} 0 R /F5 {fonts[4]} 0 R >> >>";
        foreach (var page in _pages)
        {
            page.ObjectNumber = Reserve();
        }

        foreach (var page in _pages)
        {
            var content = page.Content.ToString();
            var contentObject = Add($"<< /Length {content.Length} >>\nstream\n{content}endstream");
            var annots = new List<int>();
            foreach (var (x0, y0, x1, y1, action) in page.Links)
            {
                var target = action.StartsWith("DEST ", StringComparison.Ordinal)
                    ? DestinationOf(action)
                    : "/A " + action;
                annots.Add(Add($"<< /Type /Annot /Subtype /Link /Rect [{Num(x0)} {Num(y0)} {Num(x1)} {Num(y1)}] /Border [0 0 0] {target} >>"));
            }

            var annotArray = annots.Count == 0 ? string.Empty : "/Annots [" + string.Join(' ', annots.Select(a => a + " 0 R")) + "]";
            _objects[page.ObjectNumber - 1] = $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 612 792] {resources} /Contents {contentObject} 0 R {annotArray} >>";
        }

        var outlines = BuildOutlines();
        _objects[catalog - 1] = $"<< /Type /Catalog /Pages {pages} 0 R /Outlines {outlines} 0 R /PageMode /UseOutlines >>";
        _objects[pages - 1] = $"<< /Type /Pages /Kids [{string.Join(' ', _pages.Select(p => p.ObjectNumber + " 0 R"))}] /Count {_pages.Count} >>";
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < _objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(_objects[i]).Append("\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(_objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        pdf.Append("trailer\n<< /Size ").Append(_objects.Count + 1).Append(" /Root ").Append(catalog).Append(" 0 R /Info << /Title (").Append(Escape(Title)).Append(") /Author (MikePDF test documents) >> >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    private string DestinationOf(string action)
    {
        var parts = action.Split(' ');
        var page = int.Parse(parts[1], CultureInfo.InvariantCulture);
        return $"/Dest [{_pages[page].ObjectNumber} 0 R /XYZ {Left} {parts[2]} 0]";
    }

    private int BuildOutlines()
    {
        var root = Reserve();
        var items = new int[_headings.Count];
        for (var i = 0; i < _headings.Count; i++)
        {
            items[i] = Reserve();
        }

        var chapterItems = _headings.Select((h, i) => (h, i)).Where(p => p.h.Level == 0).Select(p => p.i).ToList();
        for (var i = 0; i < _headings.Count; i++)
        {
            var heading = _headings[i];
            var siblings = heading.Level == 0 ? chapterItems : _headings.Select((h, k) => (h, k)).Where(p => p.h.Parent == heading.Parent && p.h.Level == 1).Select(p => p.k).ToList();
            var at = siblings.IndexOf(i);
            var children = _headings.Select((h, k) => (h, k)).Where(p => p.h.Parent == i).Select(p => p.k).ToList();
            var body = new StringBuilder($"<< /Title ({Escape(heading.Text)}) /Parent {(heading.Level == 0 ? root : items[heading.Parent])} 0 R");
            if (at > 0)
            {
                body.Append(" /Prev ").Append(items[siblings[at - 1]]).Append(" 0 R");
            }

            if (at < siblings.Count - 1)
            {
                body.Append(" /Next ").Append(items[siblings[at + 1]]).Append(" 0 R");
            }

            if (children.Count > 0)
            {
                body.Append(" /First ").Append(items[children[0]]).Append(" 0 R /Last ").Append(items[children[^1]]).Append(" 0 R /Count ").Append(children.Count);
            }

            body.Append(" /Dest [").Append(_pages[heading.PageIndex].ObjectNumber).Append(" 0 R /XYZ ").Append(Left).Append(' ').Append(Num(Math.Min(heading.Y, 792))).Append(" 0] >>");
            _objects[items[i] - 1] = body.ToString();
        }

        _objects[root - 1] = $"<< /Type /Outlines /First {items[chapterItems[0]]} 0 R /Last {items[chapterItems[^1]]} 0 R /Count {chapterItems.Count} >>";
        return root;
    }
}
