using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace MikePDF.App.Services;

/// <summary>
/// UI text from Strings/en-US/Resources.resw (master prompt 10.4: no hard-coded UI text, no concatenated sentences). A missing key returns the
/// key itself, which is visible in the UI and caught by the UI tests.
/// </summary>
internal static class Strings
{
    private static readonly Lazy<ResourceLoader> Loader = new(() => new ResourceLoader());

    public static string Get(string key)
    {
        try
        {
            var value = Loader.Value.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return key;
        }
    }

    /// <summary>A string with <c>{0}</c>, <c>{1}</c> placeholders; the whole sentence is one resource, so a translation can reorder it.</summary>
    public static string Format(string key, params object[] arguments) => string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
}
