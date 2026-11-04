using MikePDF.Broker;
using MikePDF.Broker.Saving;

namespace MikePDF.App.Services;

/// <summary>
/// The app's save pipeline (docs/features/save-pipeline.md): one sandboxed writer host for the whole app, started the first time something is saved and kept for the next, and the coordinator over it.
/// A host that keeps dying is dropped and a new one starts at the next save. Stage 5's fresh engine is a new engine process for each save, outside the document engines' budget (it lives for the check).
/// </summary>
internal sealed class SaveService(Task<EngineHost> hostTask) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private WriterHostClient? _client;
    private DocumentSaver? _saver;

    /// <summary>The saver, starting the writer host if need be. Null when the writer host is not part of this installation; <see cref="EngineStartException"/> when it could not be started.</summary>
    public async Task<DocumentSaver?> GetSaverAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            lock (_lock)
            {
                if (_saver is not null)
                {
                    return _saver;
                }
            }

            var host = await hostTask.ConfigureAwait(true);
            if (host.WriterHostOptions() is not { } options)
            {
                return null;
            }

            var client = await WriterHostClient.StartAsync(options, cancellationToken).ConfigureAwait(true);
            client.Failed += (_, _) => Drop(client);
            var saver = new DocumentSaver(new SaveCoordinator(new WriterHostSaveHost(client), new EngineSaveCheck(() => host.DocumentOptions())));
            lock (_lock)
            {
                _client = client;
                _saver = saver;
            }

            return saver;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        WriterHostClient? client;
        lock (_lock)
        {
            client = _client;
            _client = null;
            _saver = null;
        }

        if (client is not null)
        {
            _ = client.DisposeAsync().AsTask();
        }
    }

    // The supervisor gave up on the host (it died too often): the next save starts a new one.
    private void Drop(WriterHostClient client)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_client, client))
            {
                _client = null;
                _saver = null;
            }
        }

        _ = client.DisposeAsync().AsTask();
    }
}
