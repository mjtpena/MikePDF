using System.Text;
using MikePDF.Core.Layout;
using MikePDF.Core.Windowing;

namespace MikePDF.Core.Tests;

/// <summary>The session file, the window placement and the activation rules (docs/features/tabs-windows-session.md 3.6 and 3.7, docs/features/file-association-jumplist.md 3.3).</summary>
public sealed class SessionAndActivationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mikepdf-session-" + Guid.NewGuid().ToString("N")[..8]);

    public SessionAndActivationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that stays is no harm.
        }
    }

    private static SessionFile Sample(bool clean = true, int tabs = 3) => new(
        clean,
        new DateTimeOffset(2026, 11, 3, 10, 30, 0, TimeSpan.Zero),
        1,
        [
            new SessionWindow(new StoredBounds(100, 50, 1100, 820, 96, @"\\.\DISPLAY1"), false, 0, true, "thumbnails", [.. Enumerable.Range(0, tabs).Select(i => new SessionTab(TabKind.Document, $@"C:\Docs\file {i}.pdf", "1:abc", i == 0 ? "A \"quoted\" title" : null, new TabViewState(i, 0.25 * i, 1.25, FitMode.Page, 1), i > 0))]),
            new SessionWindow(new StoredBounds(-1800, 40, 1000, 700, 144, @"\\.\DISPLAY2"), true, 1, false, "outline", [new SessionTab(TabKind.Settings, null, null, null, TabViewState.Start, false), new SessionTab(TabKind.Empty, null, null, null, TabViewState.Start, false)]),
        ]);

    // ---- TAB-14 -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TAB14_TheSessionRoundTripsAndCarriesAVersion()
    {
        var bytes = SessionCodec.Write(Sample());
        Assert.Contains("\"version\":1", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        var read = SessionCodec.Read(bytes);
        Assert.True(read.IsOk, read.Problem.ToString());
        Assert.Equal(bytes, SessionCodec.Write(read.Session!)); // identical model: writing what was read gives the same bytes
        var session = read.Session!;
        Assert.Equal(2, session.Windows.Count);
        Assert.Equal(1, session.ActiveWindow);
        Assert.Equal("A \"quoted\" title", session.Windows[0].Tabs[0].Title);
        Assert.Equal(new TabViewState(2, 0.5, 1.25, FitMode.Page, 1), session.Windows[0].Tabs[2].View);
        Assert.Equal(TabKind.Settings, session.Windows[1].Tabs[0].Kind);
        Assert.True(session.Windows[1].Maximized);
    }

    [Fact]
    public void TAB14_AWriteThatDiesBeforeTheMoveLeavesTheOldFileWholeAndANewWriteStillWorks()
    {
        var store = new SessionStore(Path.Combine(_root, "private", "session.json"));
        Assert.True(store.Save(Sample(tabs: 2)));
        var before = File.ReadAllBytes(store.Path);
        store.BeforeReplace = () => throw new IOException("the writer was killed here");
        Assert.False(store.Save(Sample(tabs: 5))); // a write failure is reported, never thrown
        Assert.Equal(before, File.ReadAllBytes(store.Path));
        var loaded = store.Load();
        Assert.True(loaded.IsOk);
        Assert.Equal(2, loaded.Session!.Windows[0].Tabs.Count);
        store.BeforeReplace = null;
        Assert.True(store.Save(Sample(tabs: 5)));
        Assert.Equal(5, store.Load().Session!.Windows[0].Tabs.Count);
    }

    // ---- TAB-18: nothing secret is in the file ----------------------------------------------------------------------------------

    [Fact]
    public void TAB18_TheSessionFileHasNoFieldThatCouldHoldAPassword()
    {
        var text = Encoding.UTF8.GetString(SessionCodec.Write(Sample()));
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        var tab = typeof(SessionTab).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain(tab, name => name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    // ---- TAB-19 -------------------------------------------------------------------------------------------------------------

    public static TheoryData<string, string, SessionProblem> HostileFiles()
    {
        var good = Encoding.UTF8.GetString(SessionCodec.Write(Sample(tabs: 1)));
        var data = new TheoryData<string, string, SessionProblem>
        {
            { "empty", string.Empty, SessionProblem.NotJson },
            { "truncated", good[..(good.Length / 2)], SessionProblem.NotJson },
            { "not an object", "[1,2,3]", SessionProblem.BadType },
            { "binary noise", new string(Enumerable.Range(0, 300).Select(i => (char)(i % 31)).ToArray()), SessionProblem.NotJson },
            { "future version", good.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal), SessionProblem.WrongVersion },
            { "page out of range", good.Replace("\"page\":0", "\"page\":-4", StringComparison.Ordinal), SessionProblem.OutOfRange },
            { "zoom out of range", good.Replace("\"zoom\":1.25", "\"zoom\":1e9", StringComparison.Ordinal), SessionProblem.OutOfRange },
            { "fraction out of range", good.Replace("\"pageFraction\":0", "\"pageFraction\":7", StringComparison.Ordinal), SessionProblem.OutOfRange },
            { "width out of range", good.Replace("\"width\":1100", "\"width\":0", StringComparison.Ordinal), SessionProblem.OutOfRange },
            { "dpi out of range", good.Replace("\"dpi\":96", "\"dpi\":5", StringComparison.Ordinal), SessionProblem.OutOfRange },
            { "wrong type", good.Replace("\"maximized\":false", "\"maximized\":\"no\"", StringComparison.Ordinal), SessionProblem.OutOfRange },
            { "unknown fit", good.Replace("\"fit\":\"Page\"", "\"fit\":\"Sideways\"", StringComparison.Ordinal), SessionProblem.OutOfRange },
            { "document without a path", good.Replace("\"path\":\"C:\\\\Docs\\\\file 0.pdf\",", string.Empty, StringComparison.Ordinal), SessionProblem.BadType },
            { "deeply nested", string.Concat(Enumerable.Repeat("[", 5000)), SessionProblem.NotJson },
            { "too many windows", "{\"version\":1,\"cleanExit\":true,\"saved\":\"2026-11-03T10:30:00.0000000+00:00\",\"activeWindow\":0,\"windows\":[" + string.Join(',', Enumerable.Repeat("{}", 65)) + "]}", SessionProblem.TooManyWindows },
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(HostileFiles))]
    public void TAB19_ACorruptTruncatedOrOutOfRangeFileIsRejectedWithoutACrash(string name, string text, SessionProblem expected)
    {
        var result = SessionCodec.Read(Encoding.UTF8.GetBytes(text));
        Assert.False(result.IsOk, name);
        Assert.Equal(expected, result.Problem);
    }

    [Fact]
    public void TAB19_MoreThanAThousandTabsAndMoreThanAMebibyteAreRejectedAndUnknownKeysAreIgnored()
    {
        var tab = "{\"kind\":\"Document\",\"path\":\"C:\\\\a.pdf\",\"page\":0,\"pageFraction\":0,\"zoom\":1,\"fit\":\"Width\",\"viewTurns\":0,\"suspended\":false,\"future\":{\"x\":1}}";
        string File(int tabs) => "{\"version\":1,\"cleanExit\":true,\"saved\":\"2026-11-03T10:30:00.0000000+00:00\",\"activeWindow\":0,\"extra\":[1],\"windows\":[{\"bounds\":{\"x\":0,\"y\":0,\"width\":800,\"height\":600,\"dpi\":96,\"display\":\"d\"},\"maximized\":false,\"activeTab\":0,\"sidebarOpen\":false,\"sidebarPane\":\"thumbnails\",\"tabs\":[" + string.Join(',', Enumerable.Repeat(tab, tabs)) + "]}]}";
        Assert.True(SessionCodec.Read(Encoding.UTF8.GetBytes(File(SessionCodec.MaxTabs))).IsOk); // exactly at the limit, with unknown keys in it
        Assert.Equal(SessionProblem.TooManyTabs, SessionCodec.Read(Encoding.UTF8.GetBytes(File(SessionCodec.MaxTabs + 1))).Problem);
        Assert.Equal(SessionProblem.TooLarge, SessionCodec.Read(new byte[SessionCodec.MaxBytes + 1]).Problem);
        var path = Path.Combine(_root, "big.json");
        System.IO.File.WriteAllBytes(path, new byte[SessionCodec.MaxBytes + 10]);
        Assert.Equal(SessionProblem.TooLarge, new SessionStore(path).Load().Problem);
        Assert.Equal(SessionProblem.Missing, new SessionStore(Path.Combine(_root, "none.json")).Load().Problem);
    }

    // ---- TAB-20 -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TAB20_WithRestoreSetToNeverNoSessionFileIsWrittenAndAnOldOneIsRemoved()
    {
        var path = Path.Combine(_root, "session.json");
        var mode = RestoreMode.Always;
        var recorder = new SessionRecorder(new SessionStore(path), () => mode);
        Assert.True(recorder.Record(Sample()));
        Assert.True(File.Exists(path));
        mode = RestoreMode.Never;
        Assert.False(recorder.Record(Sample()));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData(RestoreMode.Never, true, 3, RestoreAction.StartFresh)]
    [InlineData(RestoreMode.Always, true, 1, RestoreAction.Restore)]
    [InlineData(RestoreMode.Always, false, 3, RestoreAction.Restore)]
    [InlineData(RestoreMode.Ask, false, 2, RestoreAction.AskThenRestore)]
    [InlineData(RestoreMode.Ask, true, 2, RestoreAction.Restore)]
    [InlineData(RestoreMode.Ask, true, 1, RestoreAction.StartFresh)]
    [InlineData(RestoreMode.Ask, true, 0, RestoreAction.StartFresh)]
    public void TheRestoreDecisionFollowsTheSettingAndHowTheLastRunEnded(RestoreMode mode, bool clean, int tabs, RestoreAction expected)
    {
        var session = new SessionFile(clean, DateTimeOffset.UnixEpoch, 0, tabs == 0 ? [] : [Sample(tabs: tabs).Windows[0]]);
        Assert.Equal(expected, SessionPolicy.Decide(mode, session));
        Assert.Equal(RestoreAction.StartFresh, SessionPolicy.Decide(RestoreMode.Always, null));
        Assert.Equal(RestoreMode.Ask, SessionPolicy.Parse("nonsense"));
    }

    // ---- TAB-16 -------------------------------------------------------------------------------------------------------------

    private static readonly MonitorInfo Primary = new(0, 0, 1920, 1040, 96, "P");
    private static readonly MonitorInfo Second = new(1920, 0, 3840, 1040, 96, "S");
    private static readonly MonitorInfo High = new(3840, 0, 5760, 1040, 192, "H");

    private static void AssertInside(PixelRect r, MonitorInfo m)
    {
        Assert.True(r.X >= m.Left && r.Y >= m.Top && r.X + r.Width <= m.Right && r.Y + r.Height <= m.Bottom, $"{r} is not inside {m}");
    }

    [Fact]
    public void TAB16_AWindowOnAMonitorThatIsGoneMovesOntoAVisibleOneAndFitsItsWorkArea()
    {
        var gone = new StoredBounds(5000, 200, 1100, 820, 96, "S");
        var placed = WindowPlacement.Resolve(gone, [Primary]);
        AssertInside(placed, Primary);
        Assert.Equal((1100, 820), (placed.Width, placed.Height));
        var huge = WindowPlacement.Resolve(new StoredBounds(-500, -500, 9000, 9000, 96, "S"), [Primary]);
        AssertInside(huge, Primary);
        Assert.Equal((1920, 1040), (huge.Width, huge.Height));
    }

    [Fact]
    public void TAB16_AWindowThatOverlapsTwoMonitorsGoesToTheOneItOverlapsMostAndAVisibleOneIsLeftAlone()
    {
        var straddling = new StoredBounds(1700, 100, 800, 600, 96, "P");
        var placed = WindowPlacement.Resolve(straddling, [Primary, Second]);
        AssertInside(placed, Second); // 580 of its 800 pixels are on the second monitor
        var fine = new StoredBounds(200, 100, 800, 600, 96, "P");
        Assert.Equal(new PixelRect(200, 100, 800, 600), WindowPlacement.Resolve(fine, [Primary, Second]));
        Assert.Equal(new PixelRect(5, 6, 7, 8), WindowPlacement.Resolve(new StoredBounds(5, 6, 7, 8, 96, string.Empty), []));
    }

    [Fact]
    public void TAB16_ADifferentDpiKeepsTheLogicalSizeNotThePixelSize()
    {
        var placed = WindowPlacement.Resolve(new StoredBounds(4000, 100, 800, 400, 96, "S"), [Primary, High]);
        AssertInside(placed, High);
        Assert.Equal((1600, 800), (placed.Width, placed.Height)); // 96 -> 192 dpi: twice the pixels, the same size on the glass
    }

    [Fact]
    public void TAB16_MaximisedIsKeptInTheFile()
    {
        var read = SessionCodec.Read(SessionCodec.Write(Sample())).Session!;
        Assert.False(read.Windows[0].Maximized);
        Assert.True(read.Windows[1].Maximized);
    }

    // ---- FA-01, FA-02, FA-03, TAB-29 --------------------------------------------------------------------------------------------

    [Fact]
    public void FA01_NormalDriveUncAndLongPathsAndUnicodeNamesAreAccepted()
    {
        var unicode = Path.Combine(_root, "日本語 ñ.pdf");
        File.WriteAllText(unicode, "x");
        var ok = ActivationPathValidator.Validate(unicode);
        Assert.True(ok.IsOk, ok.Problem.ToString());
        Assert.Equal(unicode, ok.FullPath);
        Assert.True(ActivationPathValidator.Validate(unicode.ToUpperInvariant()).IsOk); // the file system ignores case
        var extended = ActivationPathValidator.Validate(@"\\?\" + unicode);
        Assert.True(extended.IsOk, extended.Problem.ToString());
        Assert.Equal(unicode, extended.FullPath); // normalised: the \\?\ prefix is gone
        Assert.True(ActivationPathValidator.Validate(@"\\server\share\folder\a.pdf", checkDisk: false).IsOk);
        Assert.True(ActivationPathValidator.Validate(@"\\?\UNC\server\share\a.pdf", checkDisk: false).IsOk);
        Assert.True(ActivationPathValidator.Validate(@"C:\" + string.Join('\\', Enumerable.Repeat(new string('a', 100), 20)) + @"\x.pdf", checkDisk: false).IsOk);
    }

    public static TheoryData<string?, PathProblem> Hostile() => new()
    {
        { null, PathProblem.Empty },
        { "   ", PathProblem.Empty },
        { "C:\\a\0b.pdf", PathProblem.ControlCharacter },
        { "C:\\a\nb.pdf", PathProblem.ControlCharacter },
        { @"\\.\pipe\x", PathProblem.DeviceNamespace },
        { @"\\.\PhysicalDrive0", PathProblem.DeviceNamespace },
        { @"\\?\GLOBALROOT\Device\HarddiskVolume1\a.pdf", PathProblem.DeviceNamespace },
        { @"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\a.pdf", PathProblem.DeviceNamespace },
        { @"\??\C:\a.pdf", PathProblem.DeviceNamespace },
        { "//./pipe/x", PathProblem.DeviceNamespace },
        { "CON", PathProblem.NotFullyQualified },
        { @"C:\dir\NUL", PathProblem.ReservedName },
        { @"C:\dir\COM1.pdf", PathProblem.ReservedName },
        { @"C:\dir\nul .pdf", PathProblem.ReservedName },
        { @"C:\dir\lpt9.txt.pdf", PathProblem.ReservedName },
        { @"C:\a.pdf:stream", PathProblem.AlternateStream },
        { @"C:\a.pdf:stream:$DATA", PathProblem.AlternateStream },
        { @"relative\a.pdf", PathProblem.NotFullyQualified },
        { @"\rooted.pdf", PathProblem.NotFullyQualified },
        { "--page", PathProblem.NotFullyQualified },
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void FA02_TAB29_HostilePathsAreRefusedBeforeAnythingIsOpened(string? raw, PathProblem expected)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = ActivationPathValidator.Validate(raw);
        Assert.Equal(expected, result.Problem);
        Assert.True(clock.ElapsedMilliseconds < 200, "FA-03: refusing a path never blocks"); // the pipe, the volume and the device are never touched
    }

    [Fact]
    public void FA02_TAB29_AFortyThousandCharacterPathAndAFolderAndAMissingFileAreRefused()
    {
        Assert.Equal(PathProblem.TooLong, ActivationPathValidator.Validate(@"C:\" + new string('a', 40_000)).Problem);
        Assert.Equal(PathProblem.IsFolder, ActivationPathValidator.Validate(_root).Problem);
        Assert.Equal(PathProblem.NotFound, ActivationPathValidator.Validate(Path.Combine(_root, "nothing.pdf")).Problem);
        var cap = ActivationPathValidator.Validate(@"C:\" + new string('a', ActivationPathValidator.MaxLength - 3), checkDisk: false);
        Assert.NotEqual(PathProblem.TooLong, cap.Problem); // 32,767 is the longest that is looked at
    }

    [Fact]
    public void FA02_ALinkToAPipeIsRefusedWithoutBeingOpened()
    {
        var link = Path.Combine(_root, "pipe-link.pdf");
        try
        {
            File.CreateSymbolicLink(link, @"\\.\pipe\mikepdf-never-opened");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Assert.Skip("a symbolic link cannot be made without Developer Mode or elevation");
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = ActivationPathValidator.Validate(link);
        Assert.NotEqual(PathProblem.None, result.Problem);
        Assert.True(clock.ElapsedMilliseconds < 200);
    }

    // ---- FA-04 --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void FA04_PathsWithSpacesAndQuotesWorkNewWindowIsRecognisedAndUnknownSwitchesAreNeverFiles()
    {
        var request = LaunchArguments.Parse([@"C:\My Docs\a b.pdf", "--new-window", "-x", "/y", "--page", "3", @"\\server\share\c.pdf", "--open-dialog", "/", string.Empty]);
        Assert.Equal([@"C:\My Docs\a b.pdf", @"\\server\share\c.pdf"], request.Paths);
        Assert.True(request.NewWindow);
        Assert.True(request.OpenDialog);
        var none = LaunchArguments.Parse(["--page", "3", "-x", "/y"]);
        Assert.Empty(none.Paths);
        Assert.True(none.IsEmpty);
        Assert.Equal(ActivationLimits.Maximum, LaunchArguments.Parse(Enumerable.Range(0, 500).Select(i => $@"C:\{i}.pdf")).Paths.Count);
    }
}
