namespace MikePDF.Core.Editing;

/// <summary>The names the save dialogs suggest (docs/features/save-pipeline.md 3.2: <c>{name} (edited).pdf</c> for the policy SaveMode=AlwaysSaveAsCopy).</summary>
public static class SaveNames
{
    /// <summary>
    /// <paramref name="name"/> with the suffix of <paramref name="format"/> (a resource such as <c>"{0} (edited)"</c>), unless it already ends with that suffix: saving a copy of a copy suggests
    /// "report (edited)", not "report (edited) (edited)". The comparison ignores case, as file names do. A format whose placeholder is not at the start has no suffix to look for and is applied as it is.
    /// </summary>
    public static string WithSuffix(string name, string format)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentException.ThrowIfNullOrEmpty(format);
        if (format.StartsWith("{0}", StringComparison.Ordinal) && format.IndexOf("{0}", 3, StringComparison.Ordinal) < 0)
        {
            var suffix = format[3..];
            if (suffix.Length > 0 && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        return string.Format(System.Globalization.CultureInfo.CurrentCulture, format, name);
    }
}
