using Microsoft.UI.Dispatching;
using MikePDF.Broker.Saving;

namespace MikePDF.App.Services;

/// <summary>
/// One save of one tab as the window sees it (docs/features/save-pipeline.md 3.2): what it is called, how to stop it, where it is, whether its strip is up, and, once it ended without saving, what to tell
/// the person. It stays on the tab after a failure so that the strip can be shown again when the tab is shown again; the next save or a successful one replaces it.
/// </summary>
internal sealed class SaveRun(string documentName, SaveMode mode, string? target, CancellationTokenSource cancel) : IDisposable
{
    public string DocumentName { get; } = documentName;

    public SaveMode Mode { get; } = mode;

    /// <summary>The chosen file of a Save as or a flattened copy; null for Save.</summary>
    public string? Target { get; } = target;

    public CancellationTokenSource Cancel { get; } = cancel;

    public bool Running { get; set; } = true;

    public SaveProgressStage Stage { get; set; } = SaveProgressStage.Saving;

    /// <summary>The strip is showing this save (it takes longer than 300 ms, or it ended in a way to be told).</summary>
    public bool StripShown { get; set; }

    /// <summary>The 300 ms timer that puts the strip up.</summary>
    public DispatcherQueueTimer? Delay { get; set; }

    /// <summary>What to tell the person when the save did not happen.</summary>
    public SaveFlowResult? Failure { get; set; }

    public void Dispose()
    {
        Delay?.Stop();
        Cancel.Dispose();
    }
}
