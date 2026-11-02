using System.Runtime.InteropServices;
using MikePDF.Core.Windowing;

namespace MikePDF.Core.Tests;

/// <summary>Tabs and windows as plain data (docs/features/tabs-windows-session.md): identity, where a file opens, titles, the closed-tab stack, the engine leases.</summary>
public sealed class TabModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mikepdf-tabs-" + Guid.NewGuid().ToString("N")[..8]);

    public TabModelTests() => Directory.CreateDirectory(_root);

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

    private string MakeFile(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class FakeIds(Dictionary<string, string> ids) : IFileIdSource
    {
        public string? TryGetFileId(string fullPath) => ids.TryGetValue(fullPath, out var id) ? id : null;
    }

    // ---- TAB-01 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TAB01_CaseAndTheExtendedPrefixNameTheSameFileAndTwoFilesWithTheSameNameDoNot()
    {
        var a = MakeFile(@"one\Report.pdf");
        var b = MakeFile(@"two\Report.pdf");
        var id = DocumentIdentity.Of(a);
        Assert.StartsWith("id:", id.Key, StringComparison.Ordinal); // the file ID, not the path
        Assert.Equal(id, DocumentIdentity.Of(a.ToUpperInvariant()));
        Assert.Equal(id, DocumentIdentity.Of(a.ToLowerInvariant()));
        Assert.Equal(id, DocumentIdentity.Of(@"\\?\" + a));
        Assert.Equal(id, DocumentIdentity.Of(Path.Combine(_root, "one", "..", "one", "Report.pdf")));
        Assert.NotEqual(id, DocumentIdentity.Of(b));
    }

    [Fact]
    public void TAB01_AnEightDotThreeNameAndAHardLinkAreTheSameFile()
    {
        var a = MakeFile(@"longer folder name\A long file name.pdf");
        var id = DocumentIdentity.Of(a);
        var buffer = new char[1024];
        var length = GetShortPathNameW(a, buffer, buffer.Length);
        if (length > 0 && !string.Equals(new string(buffer, 0, (int)length), a, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(id, DocumentIdentity.Of(new string(buffer, 0, (int)length)));
        }
        else
        {
            Assert.Skip("8.3 names are turned off on this volume");
        }

        var link = Path.Combine(_root, "hard.pdf");
        Assert.True(CreateHardLinkW(link, a, IntPtr.Zero));
        Assert.Equal(id, DocumentIdentity.Of(link));
    }

    [Fact]
    public void TAB01_ASymbolicLinkAndAMappedDriveAreTheSameFile()
    {
        var a = MakeFile(@"real\a.pdf");
        var id = DocumentIdentity.Of(a);
        var ran = false;
        try
        {
            var link = Path.Combine(_root, "link.pdf");
            File.CreateSymbolicLink(link, a);
            Assert.Equal(id, DocumentIdentity.Of(link));
            ran = true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            // Creating a symbolic link needs Developer Mode or an elevated token; reported below if nothing else ran.
        }

        var letter = Enumerable.Range('R', 'Z' - 'R' + 1).Select(c => (char)c + ":").FirstOrDefault(l => !Directory.Exists(l + @"\"));
        if (letter is not null && DefineDosDeviceW(0, letter, Path.Combine(_root, "real")))
        {
            try
            {
                Assert.Equal(id, DocumentIdentity.Of(letter + @"\a.pdf"));
                ran = true;
            }
            finally
            {
                DefineDosDeviceW(2, letter, Path.Combine(_root, "real"));
            }
        }

        Assert.True(ran, "neither a symbolic link nor a mapped drive could be made on this machine");
    }

    [Fact]
    public void TAB01_AFileThatCannotBeAskedFallsBackToItsNormalisedPathAndDifferentPathsStayDifferent()
    {
        var none = new FakeIds([]);
        var a = DocumentIdentity.Of(@"C:\Docs\Report.pdf", none);
        Assert.StartsWith("path:", a.Key, StringComparison.Ordinal);
        Assert.Equal(a, DocumentIdentity.Of(@"c:\docs\.\REPORT.PDF", none));
        Assert.NotEqual(a, DocumentIdentity.Of(@"C:\Other\Report.pdf", none));
        var withId = new FakeIds(new() { [@"C:\Docs\Report.pdf"] = "1:2", [@"D:\Mapped\Report.pdf"] = "1:2" });
        Assert.Equal(DocumentIdentity.Of(@"C:\Docs\Report.pdf", withId), DocumentIdentity.Of(@"D:\Mapped\Report.pdf", withId));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetShortPathNameW(string longPath, [Out] char[] shortPath, int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr security);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DefineDosDeviceW(uint flags, string device, string target);

    // ---- TAB-03, TAB-05, TAB-02 (the decision) ---------------------------------------------------------------------------------

    private static DocumentIdentity Id(string name) => new("path:" + name);

    private static TabModel Doc(string name) => new(TabKind.Document, @"C:\Docs\" + name + ".pdf", Id(name));

    private static WindowModel WindowWith(params string[] names)
    {
        var window = new WindowModel();
        foreach (var name in names)
        {
            window.Insert(window.Tabs.Count, Doc(name), activate: true);
        }

        return window;
    }

    private static WindowModel Fill(WindowModel window, params string[] names)
    {
        window.Remove(window.Tabs[0].Id);
        foreach (var name in names)
        {
            window.Insert(window.Tabs.Count, Doc(name), activate: true);
        }

        return window;
    }

    [Fact]
    public void TAB02_AFileThatHasATabInAnyWindowActivatesThatTabAndThatWindow()
    {
        var app = new AppModel();
        var second = Fill(app.NewWindow(), "c", "d");
        var first = Fill(app.NewWindow(), "a", "b"); // the window used last
        var decision = app.Plan(Id("d"), OpenSource.User, preferred: first);
        Assert.Equal(OpenAction.ActivateExisting, decision.Outcome);
        Assert.Same(second, decision.Window);
        Assert.Same(second.Tabs[1], decision.Tab);
        Assert.Equal(4, app.OpenDocumentCount); // nothing was created
    }

    [Fact]
    public void TAB03_AnEmptyActiveTabIsReusedOtherwiseTheTabGoesRightOfTheActiveOneForUserActionsAndAtTheEndForOutsideOnes()
    {
        var app = new AppModel();
        var window = app.NewWindow();
        var empty = window.Active!;
        var reuse = app.Plan(Id("x"), OpenSource.User, window);
        Assert.Equal(OpenAction.ReuseEmpty, reuse.Outcome);
        Assert.Same(empty, reuse.Tab);

        window.Remove(empty.Id);
        foreach (var name in new[] { "a", "b", "c" })
        {
            window.Insert(window.Tabs.Count, Doc(name), activate: false);
        }

        window.Activate(window.Tabs[0].Id);
        var user = app.Plan(Id("n"), OpenSource.User, window);
        Assert.Equal((OpenAction.NewTab, 1), (user.Outcome, user.Index));
        var outside = app.Plan(Id("n"), OpenSource.Outside, window);
        Assert.Equal((OpenAction.NewTab, 3), (outside.Outcome, outside.Index));
        Assert.Equal(OpenAction.NewWindow, app.Plan(Id("n"), OpenSource.Outside, window, forceNewWindow: true).Outcome);
    }

    [Fact]
    public void TAB05_TheFiftyFirstTabOpensInANewWindow()
    {
        var app = new AppModel();
        var window = app.NewWindow();
        window.Remove(window.Tabs[0].Id);
        for (var i = 0; i < WindowModel.MaxTabs; i++)
        {
            Assert.True(window.Insert(window.Tabs.Count, new TabModel(TabKind.Settings), activate: false)); // not documents: the window fills before the app's document limit
        }

        Assert.True(window.IsFull);
        Assert.False(window.Insert(0, new TabModel(TabKind.Empty), activate: false));
        Assert.Equal(OpenAction.NewWindow, app.Plan(Id("z"), OpenSource.User, window).Outcome);
    }

    [Fact]
    public void TheDocumentLimitRefusesTheOpenAndAPlaceholderDoesNotCountTowardsIt()
    {
        var app = new AppModel();
        var window = app.NewWindow();
        window.Remove(window.Tabs[0].Id);
        for (var i = 0; i < AppModel.MaxOpenDocuments; i++)
        {
            window.Insert(window.Tabs.Count, Doc("d" + i), activate: false);
        }

        Assert.Equal(OpenAction.Refused, app.Plan(Id("new"), OpenSource.User, window).Outcome);
        Assert.Equal(OpenAction.ActivateExisting, app.Plan(Id("d3"), OpenSource.User, window).Outcome); // the same file is still just a switch
        window.Tabs[5].IsSuspended = true;
        Assert.Equal(OpenAction.NewTab, app.Plan(Id("new"), OpenSource.User, window).Outcome);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(10, false)]
    [InlineData(11, true)]
    [InlineData(30, true)]
    public void TAB04_AnActivationOfMoreThanTenFilesAsksFirst(int count, bool asks) => Assert.Equal(asks, ActivationLimits.NeedsConfirmation(count));

    // ---- the window ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ClosingTheActiveTabActivatesTheNeighbourAndTheLastTabLeavesAnEmptyOne()
    {
        var window = WindowWith("a", "b", "c");
        window.Activate(window.Tabs[1].Id);
        window.Remove(window.Tabs[1].Id);
        Assert.Equal("c", window.Active!.Path![^5].ToString()); // the one that was to the right
        window.Remove(window.Active!.Id);
        Assert.Equal("a", window.Active!.Path![^5].ToString()); // last tab closed: the one to the left
        window.Remove(window.Active!.Id);
        Assert.Empty(window.Tabs);
        var empty = window.EnsureNotEmpty();
        Assert.Equal(TabKind.Empty, empty!.Kind);
        Assert.Same(empty, window.Active);
    }

    [Fact]
    public void NextPreviousAndNumbersWrapAndStopWhereTheyShould()
    {
        var window = WindowWith("a", "b", "c");
        window.Activate(window.Tabs[2].Id);
        Assert.Equal(window.Tabs[0], window.Next(+1));
        window.Activate(window.Tabs[0].Id);
        Assert.Equal(window.Tabs[2], window.Next(-1));
        Assert.Equal(window.Tabs[1], window.ByNumber(2));
        Assert.Null(window.ByNumber(4));
        Assert.Equal(window.Tabs[2], window.Last);
        Assert.Null(WindowWith("a").Next(+1));
    }

    [Fact]
    public void MovingATabKeepsTheOthersInOrderAndReorderFollowsTheStrip()
    {
        var window = WindowWith("a", "b", "c", "d");
        var a = window.Tabs[0];
        Assert.True(window.Move(a.Id, 2));
        Assert.Equal(["b", "c", "a", "d"], window.Tabs.Select(t => Path.GetFileNameWithoutExtension(t.Path)));
        Assert.False(window.Move(a.Id, 2));
        window.Reorder(window.Tabs.Reverse().Select(t => t.Id));
        Assert.Equal(["d", "a", "c", "b"], window.Tabs.Select(t => Path.GetFileNameWithoutExtension(t.Path)));
    }

    // ---- TAB-06 -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TAB06_TheTitleIsTheDocumentsOwnElseTheFileNameAndDuplicatesGainTheFolder()
    {
        var withTitle = new TabModel(TabKind.Document, @"C:\x\report.pdf", Id("1")) { DocumentTitle = "  Annual report " };
        var plain = new TabModel(TabKind.Document, @"C:\Contracts\Lease.pdf", Id("2"));
        var dupA = new TabModel(TabKind.Document, @"C:\Alpha\Notes.pdf", Id("3"));
        var dupB = new TabModel(TabKind.Document, @"C:\Beta\notes.PDF", Id("4"));
        var empty = new TabModel(TabKind.Empty);
        var titles = TabTitles.For([withTitle, plain, dupA, dupB, empty], "New tab", "Settings", (t, f) => $"{t} ({f})");
        Assert.Equal(["Annual report", "Lease", "Notes (Alpha)", "notes (Beta)", "New tab"], titles);
        Assert.Equal("Alpha", TabTitles.ParentFolderName(dupA.Path!));
        Assert.Equal(@"C:\", TabTitles.ParentFolderName(@"C:\root.pdf"));
    }

    // ---- TAB-13 -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TAB13_TheLastTenClosedTabsReopenNewestFirstWithTheirViewAndTheEleventhOldestIsGone()
    {
        var stack = new ClosedTabHistory();
        var window = Guid.NewGuid();
        for (var i = 1; i <= 11; i++)
        {
            stack.Push(new ClosedTab(window, @"C:\Docs\" + i + ".pdf", new TabViewState(i, 0.5, 1.5, MikePDF.Core.Layout.FitMode.Page, i % 4)));
        }

        Assert.Equal(ClosedTabHistory.Capacity, stack.Count);
        for (var i = 11; i >= 2; i--)
        {
            var tab = stack.Pop()!;
            Assert.Equal(@"C:\Docs\" + i + ".pdf", tab.Path);
            Assert.Equal(new TabViewState(i, 0.5, 1.5, MikePDF.Core.Layout.FitMode.Page, i % 4), tab.View);
        }

        Assert.Null(stack.Pop()); // tab 1 fell off the end
    }

    // ---- TAB-24 -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TAB24_ATabIsPinnedWhileItIsShownOrASaveOrPrintJobRuns()
    {
        var leases = new TabEngineLeases();
        var changes = new List<bool>();
        leases.PinnedChanged += changes.Add;
        Assert.False(leases.IsPinned);
        var shown = leases.Show();
        var save = leases.BeginJob();
        Assert.True(leases.IsPinned);
        shown.Dispose();
        Assert.True(leases.IsPinned); // in the background, but a save is running: never suspended
        save.Dispose();
        save.Dispose(); // a second dispose changes nothing
        Assert.False(leases.IsPinned);
        Assert.Equal([true, false], changes);
    }

    // ---- TAB-21 -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TAB21_ActivationsThatArriveDuringRestoreRunAfterItInArrivalOrderOneAtATime()
    {
        var order = new List<string>();
        var running = 0;
        var overlapped = false;
        var queue = new ActivationRelay<string>(async item =>
        {
            overlapped |= ++running > 1;
            await Task.Yield();
            order.Add(item);
            running--;
        });
        queue.Post("a");
        queue.Post("b");
        Assert.Empty(order); // not ready: the restore is still running
        queue.SetReady();
        queue.Post("c");
        await WaitUntil(() => order.Count == 3);
        Assert.Equal(["a", "b", "c"], order);
        queue.SetHeld(true); // a modal dialog is open
        queue.Post("d");
        await Task.Delay(50);
        Assert.Equal(3, order.Count);
        queue.SetHeld(false);
        await WaitUntil(() => order.Count == 4);
        Assert.Equal("d", order[3]);
        Assert.False(overlapped);
    }

    [Fact]
    public async Task AFailingActivationDoesNotStopTheOnesBehindIt()
    {
        var done = new List<string>();
        var queue = new ActivationRelay<string>(item => item == "bad" ? throw new InvalidOperationException() : Task.Run(() => done.Add(item)));
        queue.SetReady();
        queue.Post("bad");
        queue.Post("good");
        await WaitUntil(() => done.Count == 1);
        Assert.Equal(["good"], done);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
