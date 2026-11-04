namespace MikePDF.Markup;

/// <summary>What can be done to an annotation (annotation-core 3.2).</summary>
public enum AnnotationOperation
{
    Select,
    Delete,
    Reply,
    SetReviewState,
    EditContents,
    Move,
    Resize,
    Restyle,
}

/// <summary>The answer to "may I?": allowed, or not and the resource key of the reason shown next to the disabled command (annotation-core 12).</summary>
public readonly record struct OperationCheck(bool Allowed, string? ReasonKey)
{
    public static OperationCheck Yes { get; } = new(true, null);

    public static OperationCheck No(string reasonKey) => new(false, reasonKey);
}

/// <summary>Classifies annotations and decides which operations each class allows (annotation-core 3.2, AC-02).</summary>
public static class AnnotationRules
{
    public const string ReasonForeign = "Annot_Disabled_Foreign";
    public const string ReasonUnsupported = "Annot_Disabled_Unsupported";
    public const string ReasonOwner = "Annot_Disabled_Owner";
    public const string ReasonTooLarge = "Annot_Disabled_TooLarge";
    public const string ReasonStructure = "Annot_Disabled_Structure";
    public const string ReasonOthersComment = "Note_Cannot_Edit_Others";
    public const string ReasonTextAnchored = "Annot_Disabled_TextAnchored";
    public const string ReasonThreadLimit = "Note_Thread_Limit";
    public const string ReasonNotResizable = "Annot_Disabled_NotResizable";

    private static readonly HashSet<string> SupportedSubtypes = new(StringComparer.Ordinal)
    {
        "Highlight", "Underline", "StrikeOut", "Squiggly", "Text", "FreeText", "Ink", "Square", "Circle", "Line", "Polygon", "PolyLine", "Stamp",
    };

    /// <summary>The class of an annotation read from a file: owned by the <c>/NM</c> prefix, foreign if it is a kind MikePDF draws, else unsupported (links, widgets, popups, file attachments...).</summary>
    public static AnnotationClass Classify(string? subtype, string? nm)
    {
        if (subtype is null || !SupportedSubtypes.Contains(subtype))
        {
            return AnnotationClass.Unsupported;
        }

        return AnnotationIds.IsOwned(nm) ? AnnotationClass.Owned : AnnotationClass.ForeignSupported;
    }

    public static AnnotationClass Classify(Annotation a) => Classify(a.Subtype, a.Id);

    /// <summary>Whether an operation is allowed on an annotation for the person whose author name is <paramref name="currentAuthor"/>.</summary>
    public static OperationCheck Check(Annotation a, AnnotationOperation operation, string currentAuthor)
    {
        ArgumentNullException.ThrowIfNull(a);
        var cls = Classify(a);
        if (cls == AnnotationClass.Owned)
        {
            return operation == AnnotationOperation.Move && a.IsTextAnchored ? OperationCheck.No(ReasonTextAnchored)
                : operation == AnnotationOperation.Resize && !SelectionGeometry.HasBoxHandles(a) && a.Payload is not (LinePayload or PolyPayload) ? OperationCheck.No(ReasonNotResizable)
                : OperationCheck.Yes;
        }

        if (cls == AnnotationClass.Unsupported)
        {
            return operation == AnnotationOperation.Select ? OperationCheck.Yes : OperationCheck.No(ReasonUnsupported);
        }

        return operation switch
        {
            AnnotationOperation.Select or AnnotationOperation.Delete or AnnotationOperation.Reply or AnnotationOperation.SetReviewState => OperationCheck.Yes,
            AnnotationOperation.EditContents => string.Equals(a.Author, currentAuthor, StringComparison.Ordinal) && currentAuthor.Length > 0
                ? OperationCheck.Yes
                : OperationCheck.No(ReasonOthersComment),
            AnnotationOperation.Move => a.IsTextAnchored ? OperationCheck.No(ReasonTextAnchored) : OperationCheck.Yes,
            _ => OperationCheck.No(ReasonForeign),
        };
    }

    /// <summary>The confirmation text key for deleting a comment with replies (sticky-notes 2.3, SN-03): none without replies, one for a single reply, another for more.</summary>
    public static string? DeleteConfirmKey(int replies) => replies switch
    {
        <= 0 => null,
        1 => "Note_Delete_Thread_Confirm_One",
        _ => "Note_Delete_Thread_Confirm_Other",
    };
}
