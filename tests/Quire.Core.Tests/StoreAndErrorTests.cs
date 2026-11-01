using Quire.Core.Errors;
using Quire.Core.Layout;
using Quire.Core.Persistence;
using Quire.Protocol;

namespace Quire.Core.Tests;

public sealed class DocumentStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "quire-core-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeTime _time = new();

    private string StorePath => Path.Combine(_directory, "documents.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void RecentFilesAreNewestFirstWithoutDuplicates()
    {
        var store = new DocumentStore(StorePath, _time);
        store.AddRecent(@"C:\docs\a.pdf");
        _time.Advance();
        store.AddRecent(@"C:\docs\b.pdf");
        _time.Advance();
        store.AddRecent(@"C:\DOCS\A.pdf"); // same file, other case

        Assert.Equal(2, store.Recent.Count);
        Assert.Equal(@"C:\DOCS\A.pdf", store.Recent[0].Path);
        Assert.Equal(@"C:\docs\b.pdf", store.Recent[1].Path);
    }

    [Fact]
    public void OnlyTheTwentyNewestAreKept()
    {
        var store = new DocumentStore(StorePath, _time);
        for (var i = 0; i < 30; i++)
        {
            store.AddRecent($@"C:\docs\{i}.pdf");
            _time.Advance();
        }

        Assert.Equal(DocumentStore.MaxRecent, store.Recent.Count);
        Assert.Equal(@"C:\docs\29.pdf", store.Recent[0].Path);
    }

    [Fact]
    public void RemovingAndClearingWork()
    {
        var store = new DocumentStore(StorePath, _time);
        store.AddRecent(@"C:\docs\a.pdf");
        store.AddRecent(@"C:\docs\b.pdf");
        store.RemoveRecent(@"C:\docs\a.pdf");
        store.RemoveRecent(@"C:\docs\never.pdf");
        Assert.Single(store.Recent);
        store.ClearRecent();
        Assert.Empty(store.Recent);
    }

    [Fact]
    public void PositionsSurviveARestart()
    {
        var store = new DocumentStore(StorePath, _time);
        store.SavePosition(@"C:\docs\a.pdf", 12, 0.25, 2.5, FitMode.None, 1);
        store.AddRecent(@"C:\docs\a.pdf");

        var again = new DocumentStore(StorePath, _time);
        var p = again.GetPosition(@"C:\DOCS\a.pdf");
        Assert.NotNull(p);
        Assert.Equal(12, p.Page);
        Assert.Equal(0.25, p.PageFraction);
        Assert.Equal(2.5, p.Zoom);
        Assert.Equal(FitMode.None, p.Fit);
        Assert.Equal(1, p.ViewTurns);
        Assert.Single(again.Recent);
        Assert.Null(again.GetPosition(@"C:\docs\other.pdf"));
    }

    [Fact]
    public void SavedValuesAreSanitised()
    {
        var store = new DocumentStore(StorePath, _time);
        store.SavePosition(@"C:\docs\a.pdf", -4, 7, 9999, FitMode.Width, 0);
        var p = store.GetPosition(@"C:\docs\a.pdf")!;
        Assert.Equal(0, p.Page);
        Assert.Equal(1, p.PageFraction);
        Assert.Equal(64.0, p.Zoom);
    }

    [Fact]
    public void OnlyTheLatestTwoHundredPositionsAreKept()
    {
        var store = new DocumentStore(StorePath, _time);
        for (var i = 0; i < 220; i++)
        {
            store.SavePosition($@"C:\docs\{i}.pdf", 1, 0, 1, FitMode.None, 0);
            _time.Advance();
        }

        Assert.Null(store.GetPosition(@"C:\docs\0.pdf"));
        Assert.NotNull(store.GetPosition(@"C:\docs\219.pdf"));
    }

    [Fact]
    public void ADamagedFileIsTreatedAsEmptyAndReplacedOnTheNextSave()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(StorePath, "{ this is not json");
        var store = new DocumentStore(StorePath, _time);
        Assert.Empty(store.Recent);
        store.AddRecent(@"C:\docs\a.pdf");
        Assert.Single(new DocumentStore(StorePath, _time).Recent);
        Assert.False(File.Exists(StorePath + ".tmp"));
    }

    [Fact]
    public void AnUnwritableLocationNeverThrows()
    {
        // The "directory" is a file, so the store cannot create its folder.
        Directory.CreateDirectory(_directory);
        var blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "x");
        var store = new DocumentStore(Path.Combine(blocker, "documents.json"), _time);
        store.AddRecent(@"C:\docs\a.pdf");
        Assert.Single(store.Recent); // kept in memory for the session
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

        public void Advance() => _now = _now.AddSeconds(1);

        public override DateTimeOffset GetUtcNow() => _now;
    }
}

public sealed class DocumentErrorTests
{
    [Theory]
    [InlineData(Status.PasswordRequired, DocumentErrorKind.PasswordRequired)]
    [InlineData(Status.PasswordIncorrect, DocumentErrorKind.PasswordIncorrect)]
    [InlineData(Status.UnsupportedEncryption, DocumentErrorKind.UnsupportedEncryption)]
    [InlineData(Status.Damaged, DocumentErrorKind.Damaged)]
    [InlineData(Status.Timeout, DocumentErrorKind.Timeout)]
    [InlineData(Status.EngineCrashed, DocumentErrorKind.EngineCrashed)]
    [InlineData(Status.PermissionDenied, DocumentErrorKind.PermissionDenied)]
    [InlineData(Status.Unsupported, DocumentErrorKind.Unsupported)]
    [InlineData(Status.Ok, DocumentErrorKind.None)]
    [InlineData(Status.PartiallyRendered, DocumentErrorKind.None)]
    [InlineData(Status.Cancelled, DocumentErrorKind.None)]
    [InlineData(Status.ProtocolError, DocumentErrorKind.Unknown)]
    [InlineData(Status.InvalidArgument, DocumentErrorKind.Unknown)]
    public void EveryEngineStatusMapsToAnError(Status status, DocumentErrorKind expected) => Assert.Equal(expected, DocumentErrorInfo.FromStatus(status));

    [Fact]
    public void EveryKindHasAMessageKeyAndTheRightActions()
    {
        foreach (var kind in Enum.GetValues<DocumentErrorKind>().Where(k => k != DocumentErrorKind.None))
        {
            var info = DocumentErrorInfo.For(kind);
            Assert.StartsWith("Error_", info.ResourceKey, StringComparison.Ordinal);
        }

        Assert.True(DocumentErrorInfo.For(DocumentErrorKind.Timeout).CanRetry);
        Assert.False(DocumentErrorInfo.For(DocumentErrorKind.Timeout).BlocksDocument);
        Assert.False(DocumentErrorInfo.For(DocumentErrorKind.Damaged).CanRetry);
        Assert.True(DocumentErrorInfo.For(DocumentErrorKind.EngineCrashed).CanRetry);
        Assert.False(DocumentErrorInfo.For(DocumentErrorKind.PermissionDenied).BlocksDocument);
    }

    [Fact]
    public void ThePasswordDialogGivesUpAfterTheLimit()
    {
        var attempts = new PasswordAttempts(3);
        Assert.False(attempts.Exhausted);
        attempts.Record();
        attempts.Record();
        Assert.Equal(1, attempts.Remaining);
        attempts.Record();
        Assert.True(attempts.Exhausted);
        Assert.Equal(0, attempts.Remaining);
        attempts.Reset();
        Assert.False(attempts.Exhausted);
    }
}
