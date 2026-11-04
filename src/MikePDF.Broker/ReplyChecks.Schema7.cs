using MikePDF.Protocol;

namespace MikePDF.Broker;

/// <summary>
/// The semantic checks on the schema 7 replies (text quads, the annotation read side, forms, SaveDocument, NormalizeTail). The decoder has enforced every length, count and range;
/// these relate a reply to the request it answers and to the document. Each returns the problem, or null. Pure functions, so tests can feed them anything.
/// </summary>
internal static partial class ReplyChecks
{
    /// <summary>The most characters of a page the engine reports (GetPageTextReply.total).</summary>
    public const int MaxPageCharacters = 200_000;

    private const int QuadsPerReply = 64;
    private const int PasswordFlag = 1 << 13;

    // ---- text quads --------------------------------------------------------------------------

    /// <summary>
    /// Four corners a quad, <c>firstQuad + quads &lt;= totalQuads</c> and a window that is exactly what was asked for (64 quads, or what is left), a truncated range holding
    /// the 4096 the engine stops at.
    /// </summary>
    public static string? CheckTextQuads(GetTextQuads asked, GetTextQuadsReply reply)
    {
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(reply);
        if (reply.Xs.Length % 4 != 0)
        {
            return "the corners of a quad come four at a time";
        }

        var quads = reply.Xs.Length / 4;
        if ((ulong)asked.FirstQuad > reply.TotalQuads)
        {
            return "the engine answered a window that starts past the end of the quads";
        }

        if ((long)asked.FirstQuad + quads > reply.TotalQuads || quads != Math.Min(QuadsPerReply, (int)(reply.TotalQuads - asked.FirstQuad)))
        {
            return "the quad window is not what was asked for";
        }

        return reply.Truncated && reply.TotalQuads != 4096 ? "a truncated range has 4096 quads" : null;
    }

    // ---- annotations -------------------------------------------------------------------------

    /// <summary>
    /// The window is what was asked for; a parent is -1 or another entry of the page (never the entry itself). A reversed rectangle is not a violation here: the consumer marks
    /// such an entry invalid and skips it (annotation-core 11), see <see cref="IsValidRectangle"/>.
    /// </summary>
    public static string? CheckAnnotationList(ListAnnotations asked, ListAnnotationsReply reply)
    {
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(reply);
        if (!WindowFits(asked.First, asked.Count, reply.Total, reply.Subtypes.Length, allowStartPastEnd: true))
        {
            return "the annotation window is not what was asked for";
        }

        for (var i = 0; i < reply.Parents.Length; i++)
        {
            var parent = reply.Parents[i];
            if (parent >= 0 && ((uint)parent >= reply.Total || (uint)parent == asked.First + (uint)i))
            {
                return "an annotation replies to an entry the page does not have or to itself";
            }
        }

        return null;
    }

    /// <summary>A rectangle a consumer may use: ordered edges. Anything else is listed but not drawn or hit.</summary>
    public static bool IsValidRectangle(float left, float bottom, float right, float top) => left <= right && bottom <= top;

    /// <summary>
    /// Parts only a subtype has: quadrilaterals (text markup and links), vertices (polygon, polyline, line), strokes (ink). Numbers come in groups: eight to a quadrilateral, two to a point.
    /// An annotation does not reply to itself.
    /// </summary>
    public static string? CheckAnnotation(GetAnnotation asked, GetAnnotationReply reply)
    {
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(reply);
        const ushort link = 2, line = 4, polygon = 7, polyline = 8, highlight = 9, underline = 10, squiggly = 11, strikeout = 12, ink = 15;
        if (reply.QuadPoints.Length % 8 != 0 || reply.Vertices.Length % 2 != 0)
        {
            return "an annotation's numbers do not come in whole quadrilaterals and points";
        }

        if (reply.QuadPoints.Length > 0 && reply.Subtype is not (link or highlight or underline or squiggly or strikeout))
        {
            return "an annotation without attachment points has quadrilaterals";
        }

        if (reply.Vertices.Length > 0 && reply.Subtype is not (line or polygon or polyline))
        {
            return "an annotation without vertices has vertices";
        }

        if ((reply.StrokeCount > 0 || reply.PointCount > 0) && reply.Subtype != ink)
        {
            return "an annotation that is not ink has strokes";
        }

        if (reply.StrokeCount == 0 && reply.PointCount > 0)
        {
            return "points without a stroke";
        }

        return reply.ParentIndex >= 0 && (uint)reply.ParentIndex == asked.Index ? "an annotation replies to itself" : null;
    }

    /// <summary>The window of points is exactly what was asked for, and a stroke without widths has zeros.</summary>
    public static string? CheckInkStroke(GetInkStroke asked, GetInkStrokeReply reply)
    {
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(reply);
        var expected = asked.First >= reply.PointCount ? 0 : (int)Math.Min(asked.Count, reply.PointCount - asked.First);
        if (reply.Xs.Length != expected)
        {
            return "the ink window is not what was asked for";
        }

        if (!reply.HasWidths && reply.Widths.Any(static w => w != 0f))
        {
            return "widths for a stroke that has none";
        }

        return null;
    }

    /// <summary>One count for each page asked about.</summary>
    public static string? CheckAnnotationCounts(CountAnnotations asked, CountAnnotationsReply reply)
    {
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(reply);
        return reply.Counts.Length == asked.Count ? null : "the engine counted a different number of pages";
    }

    // ---- forms -------------------------------------------------------------------------------

    /// <summary>
    /// The window is what was asked for; ids are unique; pages exist and come in ascending order (tab order runs through the document); a rectangle has ordered edges; and a password
    /// field never shows its value.
    /// </summary>
    public static string? CheckFormFields(GetFormFields asked, GetFormFieldsReply reply, uint pageCount)
    {
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(reply);
        if (!WindowFits(asked.First, asked.Count, reply.Total, reply.FieldIds.Length, allowStartPastEnd: true))
        {
            return "the form field window is not what was asked for";
        }

        var seen = new HashSet<uint>();
        uint previousPage = 0;
        for (var i = 0; i < reply.FieldIds.Length; i++)
        {
            if (!seen.Add(reply.FieldIds[i]))
            {
                return "a form field id appears twice in a window";
            }

            if (reply.Pages[i] >= pageCount || reply.Pages[i] < previousPage)
            {
                return "a form field is on a page the document does not have, or out of page order";
            }

            previousPage = reply.Pages[i];
            if (!IsValidRectangle(reply.Lefts[i], reply.Bottoms[i], reply.Rights[i], reply.Tops[i]))
            {
                return "a form field rectangle is reversed";
            }

            if (reply.Types[i] == 6 && (reply.Flags[i] & PasswordFlag) != 0 && reply.Values[i].Length != 0)
            {
                return "a password field's value was sent";
            }
        }

        return null;
    }

    /// <summary>
    /// Options belong to choice fields (4 and 5) and a check state to check boxes and radio buttons (2 and 3). The option window is what was asked for and the selected options are in
    /// ascending order inside the list.
    /// </summary>
    public static string? CheckFormFieldDetail(GetFormFieldDetail asked, GetFormFieldDetailReply reply)
    {
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(reply);
        var choice = reply.Type is 4 or 5;
        var button = reply.Type is 2 or 3;
        if (!choice && (reply.OptionsTotal != 0 || reply.OptionLabels.Length != 0 || reply.SelectedIndices.Length != 0))
        {
            return "options for a field that is not a choice field";
        }

        if (!button && (reply.Checked || reply.ExportValue.Length != 0 || reply.WidgetCount != 1))
        {
            return "a check state for a field that is not a check box or a radio button";
        }

        var expected = asked.FirstOption >= reply.OptionsTotal ? 0 : (int)Math.Min(64u, reply.OptionsTotal - asked.FirstOption);
        if (reply.OptionLabels.Length != expected)
        {
            return "the option window is not what was asked for";
        }

        for (var i = 0; i < reply.SelectedIndices.Length; i++)
        {
            if (reply.SelectedIndices[i] >= reply.OptionsTotal || (i > 0 && reply.SelectedIndices[i] <= reply.SelectedIndices[i - 1]))
            {
                return "the selected options are not ascending positions inside the list";
            }
        }

        return null;
    }

    /// <summary>The pages to redraw exist, appear once and, after the field's own page, ascend; a truncated list is full.</summary>
    public static string? CheckAffectedPages(uint[] pages, bool truncated, uint pageCount)
    {
        ArgumentNullException.ThrowIfNull(pages);
        var seen = new HashSet<uint>();
        for (var i = 0; i < pages.Length; i++)
        {
            if (pages[i] >= pageCount || !seen.Add(pages[i]))
            {
                return "a page to redraw does not exist or is listed twice";
            }

            if (i > 1 && pages[i] <= pages[i - 1])
            {
                return "the pages to redraw are not in ascending order";
            }
        }

        return truncated && pages.Length != 256 ? "a truncated list of pages is full" : null;
    }

    public static string? CheckSetFormFieldValue(SetFormFieldValueReply reply, uint pageCount)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return CheckAffectedPages(reply.AffectedPages, reply.Truncated, pageCount);
    }

    /// <summary>A reset that happened changed something somewhere; one that did not, nothing. Only a button press can be refused, and a press never reports a reset it did not do.</summary>
    public static string? CheckFormAction(FormAction asked, FormActionReply reply, uint pageCount)
    {
        ArgumentNullException.ThrowIfNull(asked);
        ArgumentNullException.ThrowIfNull(reply);
        if (asked.Action != 2 && reply.Performed > 1)
        {
            return "a refusal for something that is not a button press";
        }

        if (reply.Performed != 1 && (reply.AffectedPages.Length != 0 || reply.Truncated))
        {
            return "pages to redraw for an action that changed nothing";
        }

        if (reply.Performed == 1 && reply.AffectedPages.Length == 0)
        {
            return "a reset that changed no page";
        }

        return CheckAffectedPages(reply.AffectedPages, reply.Truncated, pageCount);
    }

    // ---- saving ------------------------------------------------------------------------------

    /// <summary>The document still has the pages it was opened with, and a save wrote something.</summary>
    public static string? CheckSaveDocument(SaveDocumentReply reply, uint pageCount)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (reply.PageCount != pageCount)
        {
            return "the saved document has another page count";
        }

        return reply.BytesWritten == 0 ? "a save that wrote nothing" : null;
    }
}
