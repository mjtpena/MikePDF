using System.Runtime.InteropServices;
using System.Text;

namespace Quire.App.Services;

/// <summary>Where the app keeps things, and whether it runs with package identity.</summary>
internal static partial class AppPaths
{
    private static readonly Lazy<bool> Packaged = new(DetectPackaged);

    public static bool IsPackaged => Packaged.Value;

    /// <summary>
    /// The app's private data folder. Packaged: LocalState, which is removed when the app is uninstalled. Unpackaged (development): %LOCALAPPDATA%\Quire.
    /// Nothing here is shared or uploaded.
    /// </summary>
    public static string DataDirectory => IsPackaged
        ? Windows.Storage.ApplicationData.Current.LocalFolder.Path
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quire");

    /// <summary>
    /// What only the broker may read: the recent-files list and last positions. The engine's token carries the package's own capability, which
    /// the rest of the data folder grants full control, so this directory has a protected DACL naming the user and SYSTEM alone (PrivateDirectory).
    /// </summary>
    public static string PrivateDirectory => Path.Combine(DataDirectory, "private");

    public static string DocumentStorePath
    {
        get
        {
            Quire.Broker.PrivateDirectory.Ensure(PrivateDirectory);
            return Path.Combine(PrivateDirectory, "documents.json");
        }
    }

    /// <summary>The engine, beside the executable. It runs from here: in a package this is under WindowsApps, which Windows protects and the package signature covers.</summary>
    public static string EngineSource => Path.Combine(AppContext.BaseDirectory, "Engine");

    /// <summary>The AppContainer profile. A packaged app's profile is scoped under its package by Windows, so the plain name cannot clash with a development build's.</summary>
    public static string ProfileName => "Quire.Engine";

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentPackageFullName")]
    private static unsafe partial int GetCurrentPackageFullName(uint* length, char* buffer);

    private static unsafe bool DetectPackaged()
    {
        uint length = 0;
        // ERROR_INSUFFICIENT_BUFFER (122) means "there is a package"; APPMODEL_ERROR_NO_PACKAGE (15700) means there is not.
        return GetCurrentPackageFullName(&length, null) == 122;
    }

    /// <summary>The package version as "1.0.0.0", or the assembly version when unpackaged.</summary>
    public static string Version
    {
        get
        {
            if (IsPackaged)
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                return new StringBuilder().Append(v.Major).Append('.').Append(v.Minor).Append('.').Append(v.Build).Append('.').Append(v.Revision).ToString();
            }

            return typeof(AppPaths).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        }
    }
}
