using MikePDF.Core;

namespace MikePDF.App.Services;

/// <summary>The app's view of <see cref="TestSeamGate"/>: the real environment, and whether this is a Store-signed package (then no seam is read).</summary>
internal static class TestSeams
{
    private static readonly Lazy<bool> StoreSigned = new(DetectStoreSigned);

    /// <summary>The value of a test seam, or null (not set, or ignored because the package is Store-signed).</summary>
    public static string? Get(string name) => TestSeamGate.Read(name, Environment.GetEnvironmentVariable, StoreSigned.Value);

    private static bool DetectStoreSigned()
    {
        try
        {
            return AppPaths.IsPackaged && Windows.ApplicationModel.Package.Current.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.Store;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return true; // cannot tell: treat it as shipped, and ignore the seams
        }
    }
}
