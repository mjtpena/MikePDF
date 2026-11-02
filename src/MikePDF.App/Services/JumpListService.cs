using System.Runtime.InteropServices;
using Windows.UI.StartScreen;

namespace MikePDF.App.Services;

/// <summary>
/// The Jump List (docs/features/file-association-jumplist.md 3.4): two tasks, "Open a PDF..." and "New window", and Windows' own Recent group for this app. Built after the first window is
/// up, so it never counts against start-up. A failure is ignored: the Jump List is a convenience. Opens are registered with the shell's recent documents when the setting allows it.
/// </summary>
internal static partial class JumpListService
{
    private const uint ShardPathW = 3;

    public static async Task BuildAsync()
    {
        try
        {
            if (!AppPaths.IsPackaged)
            {
                return;
            }

            var list = await JumpList.LoadCurrentAsync();
            list.SystemGroupKind = JumpListSystemGroupKind.Recent;
            list.Items.Clear();
            list.Items.Add(JumpListItem.CreateWithArguments("--open-dialog", Strings.Get("JumpList_OpenFile")));
            list.Items.Add(JumpListItem.CreateWithArguments("--new-window", Strings.Get("JumpList_NewWindow")));
            await list.SaveAsync();

            // A journey asks for what was saved (the Jump List itself is Windows' to show): "MIKEPDF_TEST_JUMPLIST_DUMP" names a file in the private folder.
            if (TestSeams.Get(MikePDF.Core.TestSeamGate.JumpListDump) is { Length: > 0 } name && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
            {
                var saved = await JumpList.LoadCurrentAsync();
                await File.WriteAllLinesAsync(Path.Combine(AppPaths.PrivateDirectory, name), saved.Items.Select(i => i.Arguments + "|" + i.DisplayName + "|" + i.GroupName));
            }
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            System.Diagnostics.Debug.WriteLine("Jump List failed: " + e.GetType().Name);
        }
    }

    /// <summary>Tells the shell this app opened the file, so it can show in the Jump List's Recent group and File Explorer's recent files.</summary>
    public static void AddToRecent(string path) => SHAddToRecentDocs(ShardPathW, path);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void SHAddToRecentDocs(uint flags, string path);
}
