using MikePDF.Broker;
using MikePDF.Core.Printing;
using MikePDF.Printing;

namespace MikePDF.App.Services;

/// <summary>What printing needs of an open document: whether the owner allows it, what the file itself asks for, and an engine of its own to print and preview with.</summary>
internal sealed partial class DocumentSession
{
    /// <summary>The owner has not forbidden printing (permission bit 3 of /P). True for an unprotected document and after the owner password.</summary>
    public bool OwnerAllowsPrinting => (Permissions & 0x4) != 0;

    /// <summary>
    /// A print session: a document engine of its own from the budget (so printing never evicts the document being read), with a ring sized for bands, the same file opened read-only, and the engine
    /// pinned for the life of the session. Dispose it when the sheet closes without printing, or when the job is over.
    /// </summary>
    public Task<EngineResult<PrintSession>> OpenPrintSessionAsync(PrintOptions options, bool printingAllowedByPolicy, CancellationToken cancellationToken) =>
        PrintSession.OpenAsync(_host.Budget, Path, _password, options, printingAllowedByPolicy, cancellationToken);

    /// <summary>
    /// What the document asks for in <c>/ViewerPreferences</c> (print scaling, copies, sides, page range), turned into the sheet's starting values. Never forced: they are defaults, clamped (1 to 99 copies,
    /// a range that does not fit the document is ignored). <see cref="PrintDefaults.Plain"/> when the file says nothing or the engine cannot answer.
    /// </summary>
    public async Task<PrintDefaults> GetPrintDefaultsAsync(CancellationToken cancellationToken)
    {
        if (_document is null || _disposed)
        {
            return PrintDefaults.Plain;
        }

        try
        {
            var reply = await _document.GetViewerPreferencesAsync(cancellationToken).ConfigureAwait(true);
            return reply.IsOk && reply.Value is { } prefs
                ? PrintDefaults.FromViewerPreferences(prefs.PrintScaling, prefs.NumCopies, prefs.Duplex, prefs.RangeFirsts, prefs.RangeLasts, Controller.PageCount)
                : PrintDefaults.Plain;
        }
        catch (OperationCanceledException)
        {
            return PrintDefaults.Plain;
        }
    }
}
