using MikePDF.Core.Settings;

namespace MikePDF.Core.Printing;

/// <summary>The keys of the settings the print sheet keeps (docs/features/print.md 3.2). All are last-used values, set by the sheet when a job starts.</summary>
public static class PrintSettingKeys
{
    public const string Printer = "print.printer";
    public const string Paper = "print.paper";
    public const string Scaling = "print.scaling";
    public const string CustomPercent = "print.customPercent";
    public const string AutoRotate = "print.autoRotate";
    public const string Orientation = "print.orientation";
    public const string PagesPerSheet = "print.pagesPerSheet";
    public const string SheetOrder = "print.sheetOrder";
    public const string Annotations = "print.annotations";
    public const string FormFields = "print.formFields";
    public const string AsImage = "print.asImage";
    public const string Grayscale = "print.grayscale";
    public const string Collate = "print.collate";
    public const string Duplex = "print.duplex";

    public static IReadOnlyList<string> All { get; } =
        [Printer, Paper, Scaling, CustomPercent, AutoRotate, Orientation, PagesPerSheet, SheetOrder, Annotations, FormFields, AsImage, Grayscale, Collate, Duplex];
}

/// <summary>
/// What the person last printed with: the printer and the sheet's options, as the settings file holds them. Copies are never remembered (a document's own wish or 1), and neither is the
/// choice of pages (they belong to a document). Every value is validated by the registry on the way in and clamped by <see cref="PrintOptions.Normalize"/> on the way out, so a settings
/// file written by hand or by another version never gives the sheet a value it cannot show.
/// </summary>
public sealed record PrintPreferences(string Printer, PrintOptions Options)
{
    public static PrintPreferences Default { get; } = new(string.Empty, new PrintOptions());

    /// <summary>The last-used values (or the defaults for what was never set).</summary>
    public static PrintPreferences Load(SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var options = new PrintOptions
        {
            PaperSizeId = settings.Value<int>(PrintSettingKeys.Paper),
            Scaling = ParseEnum(settings.Value<string>(PrintSettingKeys.Scaling), PrintScaling.Shrink),
            CustomPercent = settings.Value<int>(PrintSettingKeys.CustomPercent),
            AutoRotate = settings.Value<bool>(PrintSettingKeys.AutoRotate),
            Orientation = ParseEnum(settings.Value<string>(PrintSettingKeys.Orientation), PaperOrientation.Auto),
            PagesPerSheet = settings.Value<int>(PrintSettingKeys.PagesPerSheet),
            SheetOrder = ParseEnum(settings.Value<string>(PrintSettingKeys.SheetOrder), SheetOrder.Horizontal),
            Annotations = settings.Value<bool>(PrintSettingKeys.Annotations),
            FormFields = settings.Value<bool>(PrintSettingKeys.FormFields),
            AsImage = settings.Value<bool>(PrintSettingKeys.AsImage),
            Grayscale = settings.Value<bool>(PrintSettingKeys.Grayscale),
            Collate = settings.Value<bool>(PrintSettingKeys.Collate),
            Duplex = ParseEnum(settings.Value<string>(PrintSettingKeys.Duplex), PrintDuplex.None),
        };
        return new PrintPreferences(settings.Value<string>(PrintSettingKeys.Printer), options.Normalize());
    }

    /// <summary>Writes the values that differ from what is stored. Called when a job starts.</summary>
    public static void Save(SettingsStore settings, string printer, PrintOptions options)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        var o = options.Normalize();
        settings.Set(PrintSettingKeys.Printer, printer.Length <= 260 ? printer : string.Empty);
        settings.Set(PrintSettingKeys.Paper, o.PaperSizeId);
        settings.Set(PrintSettingKeys.Scaling, o.Scaling.ToString());
        settings.Set(PrintSettingKeys.CustomPercent, (int)Math.Round(o.CustomPercent));
        settings.Set(PrintSettingKeys.AutoRotate, o.AutoRotate);
        settings.Set(PrintSettingKeys.Orientation, o.Orientation.ToString());
        settings.Set(PrintSettingKeys.PagesPerSheet, o.PagesPerSheet);
        settings.Set(PrintSettingKeys.SheetOrder, o.SheetOrder.ToString());
        settings.Set(PrintSettingKeys.Annotations, o.Annotations);
        settings.Set(PrintSettingKeys.FormFields, o.FormFields);
        settings.Set(PrintSettingKeys.AsImage, o.AsImage);
        settings.Set(PrintSettingKeys.Grayscale, o.Grayscale);
        settings.Set(PrintSettingKeys.Collate, o.Collate);
        settings.Set(PrintSettingKeys.Duplex, o.Duplex.ToString());
    }

    private static T ParseEnum<T>(string text, T fallback)
        where T : struct, Enum => Enum.TryParse<T>(text, out var value) && Enum.IsDefined(value) ? value : fallback;
}
