using System.Diagnostics;
using System.Globalization;
using MikePDF.Broker;
using MikePDF.Core.Editing;
using MikePDF.Core.Persistence;
using MikePDF.Core.Settings;
using MikePDF.Core.Windowing;
using MikePDF.Protocol;

namespace MikePDF.TrimProbe;

/// <summary>
/// Runs the parts of MikePDF.App that do not need a window, in the form that ships (trimmed, ReadyToRun, self-contained). Each check prints one line; the exit code is the
/// number of checks that failed (0 = all passed), 64 when the arguments are wrong. Arguments: --engine-dir &lt;folder with MikePDF.Engine.exe and pdfium.dll&gt; --pdf &lt;file&gt;.
/// Without --engine-dir the engine check is skipped (and says so).
/// </summary>
internal static class Program
{
    private static int _failed;

    private static async Task<int> Main(string[] args)
    {
        string? engineDirectory = null;
        string? pdf = null;
        var expectTrimmed = false;
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--engine-dir":
                    engineDirectory = args[i + 1];
                    break;
                case "--expect-trimmed":
                    expectTrimmed = args[i + 1] == "1";
                    break;
                case "--pdf":
                    pdf = args[i + 1];
                    break;
                default:
                    Console.Error.WriteLine("usage: mikepdf-trimprobe [--expect-trimmed 1] [--engine-dir <dir> --pdf <file>]");
                    return 64;
            }
        }

        var scratch = Path.Combine(Path.GetTempPath(), "mikepdf-trimprobe-" + Environment.ProcessId);
        Directory.CreateDirectory(scratch);
        var total = Stopwatch.StartNew();
        try
        {
            if (expectTrimmed)
            {
                // PublishTrimmed turns reflection-based JSON off. If it is on, this probe is not running the trimmed form and proves nothing.
                Check("this is a trimmed build (reflection JSON is off)", () => Require(!System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault, "reflection JSON is still enabled: the probe was not trimmed"));
            }

            Check("settings store: set, flush, reload", () => SettingsRoundTrip(scratch));
            Check("document store: recent files and positions, reload", () => DocumentStoreRoundTrip(scratch));
            Check("session file: write, read", SessionRoundTrip);
            Check("edit commands: FromJson and the reserved-field rule", EditCommands);
            if (pdf is not null)
            {
                await CheckAsync("WinRT through C#/WinRT: IAsyncOperation<StorageFile>, IReadOnlyList<IStorageItem>, DataPackage, UISettings", () => WinRtCalls(pdf)).ConfigureAwait(false);
            }

            if (engineDirectory is not null && pdf is not null)
            {
                await CheckAsync("sandboxed engine: open a document and render a tile (Broker, CsWin32, MessagePack protocol)", () => EngineSession(engineDirectory, pdf, scratch)).ConfigureAwait(false);
            }
            else
            {
                Console.WriteLine("SKIP sandboxed engine (no --engine-dir and --pdf)");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (IOException)
            {
                // Temp files; the next run uses its own folder.
            }
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{(_failed == 0 ? "PASS" : "FAIL")}: {_failed} failed, {total.ElapsedMilliseconds} ms"));
        return _failed;
    }

    private static void Check(string name, Action check)
    {
        try
        {
            check();
            Console.WriteLine("ok   " + name);
        }
        catch (Exception e)
        {
            _failed++;
            Console.WriteLine("FAIL " + name + ": " + e.GetType().Name + ": " + e.Message);
        }
    }

    private static async Task CheckAsync(string name, Func<Task> check)
    {
        try
        {
            await check().ConfigureAwait(false);
            Console.WriteLine("ok   " + name);
        }
        catch (Exception e)
        {
            _failed++;
            Console.WriteLine("FAIL " + name + ": " + e.GetType().Name + ": " + e.Message);
        }
    }

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException(what);
        }
    }

    private static void SettingsRoundTrip(string scratch)
    {
        var path = Path.Combine(scratch, "settings.json");
        using (var store = new SettingsStore(path))
        {
            Require(store.Set("general.theme", "Dark") == SetResult.Applied, "setting the theme did not report a change");
            Require(store.Set("recent.maxItems", 7) == SetResult.Applied, "setting the recent-file limit did not report a change");
            Require(store.WriteNow(), "the settings file was not written");
        }

        using var reloaded = new SettingsStore(path);
        Require(reloaded.FileExisted, "the settings file was not found again");
        Require(reloaded.Value<string>("general.theme") == "Dark", "the theme did not survive a reload");
        Require(reloaded.Value<int>("recent.maxItems") == 7, "the recent-file limit did not survive a reload");
    }

    private static void DocumentStoreRoundTrip(string scratch)
    {
        var path = Path.Combine(scratch, "state.json");
        var first = new DocumentStore(path);
        first.AddRecent(@"C:\probe\a.pdf");
        first.AddRecent(@"C:\probe\b.pdf");
        first.SavePosition(@"C:\probe\a.pdf", page: 3, pageFraction: 0.25, zoom: 1.5, MikePDF.Core.Layout.FitMode.Width, viewTurns: 1);
        first.SetPageColour(MikePDF.Core.Colour.PageColourMode.Normal);

        var second = new DocumentStore(path);
        Require(second.Recent.Count == 2, $"expected 2 recent files after a reload, found {second.Recent.Count}");
        var position = second.GetPosition(@"C:\probe\a.pdf");
        Require(position is { Page: 3 } && Math.Abs(position.Zoom - 1.5) < 1e-9, "the saved position did not survive a reload");
    }

    private static void SessionRoundTrip()
    {
        var view = new TabViewState(2, 0.5, 1.25, MikePDF.Core.Layout.FitMode.Page, 0);
        var session = new SessionFile(
            CleanExit: true,
            Saved: new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero),
            ActiveWindow: 0,
            Windows:
            [
                new SessionWindow(
                    new StoredBounds(10, 20, 1200, 800, 96, "primary"),
                    Maximized: false,
                    ActiveTab: 0,
                    SidebarOpen: true,
                    SidebarPane: "thumbnails",
                    Tabs: [new SessionTab(TabKind.Document, @"C:\probe\a.pdf", null, "a.pdf", view, Suspended: false)]),
            ]);
        var bytes = SessionCodec.Write(session);
        var read = SessionCodec.Read(bytes);
        Require(read.IsOk && read.Session is { DocumentCount: 1 }, "the session file did not read back (" + read.Problem + ")");
        Require(read.Session!.Windows[0].Tabs[0].View.Page == 2, "the tab's page did not survive");
    }

    private static void EditCommands()
    {
        var command = EditCommand.FromJson("AddAnnotation", """{"page":0,"kind":"Highlight","name":"h1","contents":"caf\u00e9"}""");
        Require(command.Kind == EditKinds.Annotations, "AddAnnotation is not an annotation edit");
        Require(command.Payload.GetProperty("name").GetString() == "h1", "the payload lost a field");
        var rejected = false;
        try
        {
            _ = EditCommand.FromJson("AddAnnotation", """{"seq":1}""");
        }
        catch (ArgumentException)
        {
            rejected = true;
        }

        Require(rejected, "a field that shadows the envelope was accepted");
    }

    /// <summary>
    /// The WinRT calls MikePDF.App makes that need no window and no package identity, with the same shapes: awaiting an <c>IAsyncOperation&lt;T&gt;</c> of a runtime class (file
    /// pickers, the Jump List, drag and drop), a collection of interface objects whose runtime class the projection must find by name (<c>GetStorageItemsAsync</c>), a value struct
    /// (<c>Windows.UI.Color</c>), a string result and a WinRT event-free object created by activation (<c>UISettings</c>). C#/WinRT looks runtime classes up by name and the trimmer
    /// cannot see that: a type it removed would come back as a plain object here.
    /// </summary>
    private static async Task WinRtCalls(string pdf)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(pdf);
        Require(string.Equals(file.Name, Path.GetFileName(pdf), StringComparison.OrdinalIgnoreCase), "StorageFile.Name is wrong");
        var properties = await file.GetBasicPropertiesAsync();
        Require(properties.Size == (ulong)new FileInfo(pdf).Length, "BasicProperties.Size is wrong");

        var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(pdf)!);
        var items = await folder.GetItemsAsync();
        Require(items.Count > 0, "the fixture folder has no items");
        var found = items.OfType<Windows.Storage.StorageFile>().Any(i => string.Equals(i.Name, file.Name, StringComparison.OrdinalIgnoreCase));
        Require(found, "the folder's IStorageItem list did not hand back the file as a StorageFile (a trimmed-away runtime class?)");

        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText("trim probe");
        var text = await package.GetView().GetTextAsync();
        Require(text == "trim probe", "DataPackage text did not round-trip");

        var colour = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
        Require(colour.A == 255, "UISettings returned a colour that is not opaque");
    }

    private static async Task EngineSession(string engineDirectory, string pdf, string scratch)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var profile = AppContainerProfile.CreateOrOpen("MikePDF.TrimProbe", "MikePDF trim probe", "Zero-capability AppContainer for the trimmed-build probe");
        var deploy = Path.Combine(scratch, "engine");
        Directory.CreateDirectory(deploy);
        EngineDeployment.EnsureAppContainerAccess(deploy, profile); // before the copy, so the files inherit it
        foreach (var file in new[] { "MikePDF.Engine.exe", "pdfium.dll" })
        {
            File.Copy(Path.Combine(engineDirectory, file), Path.Combine(deploy, file));
        }

        try
        {
            await using var supervisor = await EngineSupervisor.StartAsync(
                new EngineSupervisorOptions
                {
                    EnginePath = Path.Combine(deploy, "MikePDF.Engine.exe"),
                    Profile = profile,
                    SlotCount = 2,
                    SlotSize = 4 << 20,
                    DefaultRequestDeadline = TimeSpan.FromSeconds(30),
                },
                cancel.Token).ConfigureAwait(false);
            var opened = await supervisor.OpenDocumentAsync(pdf, password: null, cancel.Token).ConfigureAwait(false);
            Require(opened.Status == Status.Ok && opened.Value is not null, "open: " + opened.Status + " " + opened.Detail);
            await using var document = opened.Value!;
            Require(document.PageCount >= 1, "the document reports no pages");
            var tile = await document.RenderTileAsync(new RenderTileArgs(0, 1.0f, 0, 0, 0, 256, 256), generation: 1, TimeSpan.FromSeconds(30), cancel.Token).ConfigureAwait(false);
            Require(tile.Status is Status.Ok && tile.Value is not null, "render: " + tile.Status + " " + tile.Detail);
            using var lease = tile.Value!;
            Require(lease.Width == 256 && lease.Height == 256, "the tile has the wrong size");
            var pixels = lease.Pixels;
            var coloured = false;
            for (var i = 0; i + 3 < pixels.Length && !coloured; i += 4)
            {
                coloured = pixels[i] != 255 || pixels[i + 1] != 255 || pixels[i + 2] != 255;
            }

            Require(coloured, "the tile is blank: the fixture has content in this corner");
        }
        finally
        {
            try
            {
                EngineDeployment.RevokeAppContainerAccess(deploy, profile.Sid);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                // Best effort, as in the render tool.
            }
        }
    }
}
