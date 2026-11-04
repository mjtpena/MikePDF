namespace MikePDF.Broker.Saving;

/// <summary>How serious a save message is: it picks the strip's colour and glyph.</summary>
public enum SaveNoticeSeverity
{
    Informational,
    Warning,
    Error,
}

/// <summary>What the strip offers after a save that did not happen. The shell turns each into a button.</summary>
public enum SaveAction
{
    /// <summary>Save a copy somewhere else (the file picker).</summary>
    SaveAs,

    /// <summary>Run the same save again.</summary>
    TryAgain,

    /// <summary>The path names another file now and the person chose to replace it with their version.</summary>
    Overwrite,

    /// <summary>Show the short reason code (non-sensitive).</summary>
    Details,

    /// <summary>Open the folder of the original file (RestorePending: the original is under another name).</summary>
    ShowOriginal,
}

/// <summary>What fills the <c>{0}</c> of a notice's body.</summary>
public enum SaveNoticeArgument
{
    None,

    /// <summary>The document's file name.</summary>
    DocumentName,

    /// <summary>The path where the user's original file is now (<see cref="SaveResult.OriginalAt"/>).</summary>
    OriginalPath,
}

/// <summary>
/// What a person is told about a save that did not complete (save-pipeline 6, design 14 section 2.8). Resource keys, never text: the words are in the .resw files. The sentence says what happened,
/// that the changes are still there, and what can be done; the code is only in Details.
/// </summary>
public sealed record SaveNotice(SaveNoticeSeverity Severity, string TitleKey, SaveNoticeArgument TitleArgument, string BodyKey, SaveNoticeArgument BodyArgument, IReadOnlyList<SaveAction> Actions, string? DetailCode);

/// <summary>Maps the result of a save to what the shell shows (save-pipeline 6 and 3.8).</summary>
public static class SavePresentation
{
    /// <summary>The resource keys this class can return: a test keeps each of them in the resources.</summary>
    public static IReadOnlyList<string> ResourceKeys { get; } =
    [
        Keys.RefusedTitle, Keys.FailedTitle, Keys.FailedBody, Keys.ReadOnly, Keys.HardLinks, Keys.Efs, Keys.Encrypted, Keys.TooLarge, Keys.NoSpace, Keys.NoFolderAccess,
        Keys.ChangedOnDisk, Keys.TargetGone, Keys.InUse, Keys.Unsupported, Keys.Structure, Keys.Cancelled, Keys.RestorePendingTitle, Keys.RestorePending,
    ];

    /// <summary>The message for a result, or null when there is nothing to say (saved, or nothing to save).</summary>
    public static SaveNotice? Describe(SaveResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Outcome is SaveOutcome.Saved or SaveOutcome.NothingToSave)
        {
            return null;
        }

        var detail = result.Reason + (string.IsNullOrEmpty(result.Detail) ? string.Empty : ": " + result.Detail);
        var name = SaveNoticeArgument.DocumentName;
        const SaveNoticeSeverity Warning = SaveNoticeSeverity.Warning;
        const SaveNoticeSeverity Error = SaveNoticeSeverity.Error;

        SaveNotice Refused(string body, SaveNoticeArgument argument, params SaveAction[] actions) =>
            new(Warning, Keys.RefusedTitle, name, body, argument, [.. actions], detail);

        SaveNotice Failed(string body, SaveNoticeArgument argument, params SaveAction[] actions) =>
            new(Error, Keys.FailedTitle, SaveNoticeArgument.None, body, argument, [.. actions], detail);

        return result.Reason switch
        {
            // The file or the document does not allow an in-place save: say why, and offer a copy.
            SaveReason.ReadOnly => Refused(Keys.ReadOnly, name, SaveAction.SaveAs),
            SaveReason.HardLinks => Refused(Keys.HardLinks, name, SaveAction.SaveAs),
            SaveReason.EncryptedFile => Refused(Keys.Efs, name, SaveAction.SaveAs),
            SaveReason.DirectoryNotWritable => Refused(Keys.NoFolderAccess, SaveNoticeArgument.None, SaveAction.SaveAs),
            SaveReason.TargetGone => Refused(Keys.TargetGone, name, SaveAction.SaveAs),

            // MikePDF cannot write encrypted objects, so a copy would be refused for the same reason: there is no copy to offer (save-pipeline OQ-SP-8, docs/design/decisions.md).
            SaveReason.PasswordProtected => Refused(Keys.Encrypted, SaveNoticeArgument.None),
            SaveReason.TooLarge => Refused(Keys.TooLarge, SaveNoticeArgument.None),
            SaveReason.Structure => Refused(Keys.Structure, SaveNoticeArgument.None),
            SaveReason.UnsupportedEdits => Refused(Keys.Unsupported, SaveNoticeArgument.None, SaveAction.Details),

            // The path names another file now: the shell asks Overwrite, Save a copy, Cancel (a dialog); this is the message behind it.
            SaveReason.ChangedOnDisk => Refused(Keys.ChangedOnDisk, name, SaveAction.Overwrite, SaveAction.SaveAs),

            SaveReason.NoSpace => Failed(Keys.NoSpace, SaveNoticeArgument.None, SaveAction.SaveAs, SaveAction.TryAgain, SaveAction.Details),
            SaveReason.InUse => Failed(Keys.InUse, name, SaveAction.SaveAs, SaveAction.TryAgain, SaveAction.Details),
            SaveReason.Cancelled => new SaveNotice(SaveNoticeSeverity.Informational, Keys.FailedTitle, SaveNoticeArgument.None, Keys.Cancelled, SaveNoticeArgument.None, [], null),

            // The original could not be put back after ReplaceFileW moved it aside: it is under another name, and the next start restores it.
            SaveReason.RestorePending => new SaveNotice(Error, Keys.RestorePendingTitle, SaveNoticeArgument.None, Keys.RestorePending, SaveNoticeArgument.OriginalPath, [SaveAction.ShowOriginal, SaveAction.Details], detail),

            // Stage 5 found the output wrong, the writer host died or timed out, an I/O error: "MikePDF couldn't save safely. Your changes are still here."
            _ => Failed(Keys.FailedBody, SaveNoticeArgument.None, SaveAction.SaveAs, SaveAction.TryAgain, SaveAction.Details),
        };
    }

    private static class Keys
    {
        public const string RefusedTitle = "Save_Refused_Title";
        public const string FailedTitle = "Save_Failed_Title";
        public const string FailedBody = "Save_Failed_Body";
        public const string ReadOnly = "Save_ReadOnly";
        public const string HardLinks = "Save_HardLinks";
        public const string Efs = "Save_Efs";
        public const string Encrypted = "Save_Encrypted";
        public const string TooLarge = "Save_TooLarge";
        public const string NoSpace = "Save_NoSpace";
        public const string NoFolderAccess = "Save_NoFolderAccess";
        public const string ChangedOnDisk = "Save_ChangedOnDisk";
        public const string TargetGone = "Save_TargetGone";
        public const string InUse = "Save_InUse";
        public const string Unsupported = "Save_Unsupported";
        public const string Structure = "Save_Structure";
        public const string Cancelled = "Save_Cancelled";
        public const string RestorePendingTitle = "Save_RestorePending_Title";
        public const string RestorePending = "Save_RestorePending";
    }
}
