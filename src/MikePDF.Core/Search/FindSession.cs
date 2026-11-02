using MikePDF.Core.Reading;
using MikePDF.Protocol;

namespace MikePDF.Core.Search;

/// <summary>Searches one page. <see cref="DocumentReader"/> does, through the engine; tests do it with scripts.</summary>
public interface ISearchSource
{
    Task<ReadReply<PageMatches>> SearchAsync(int page, SearchOptions options, CancellationToken cancellationToken);
}

public readonly record struct FoundMatch(int Page, int IndexOnPage, PageMatch Match);

/// <summary>How far a search has got.</summary>
public readonly record struct FindProgress(int MatchCount, int PagesSearched, int PageCount, int PagesFailed, bool IsComplete, bool IsCancelled, bool QueryRefused, bool AnyTruncated)
{
    public double Fraction => PageCount == 0 ? 1 : (double)PagesSearched / PageCount;
}

/// <summary>
/// One search of a document for one phrase (Ctrl+F). It searches a page at a time, starting at the page the person is on and going round to
/// the pages before it, so the first match is the nearest one and results arrive while the rest is still being searched. Matches are numbered
/// in that order. It can be cancelled at any moment: the page being searched is abandoned, and nothing further is asked of the engine.
/// <para>
/// <see cref="Changed"/> may be raised from any thread; a handler that touches the UI marshals. All members are thread-safe.
/// </para>
/// </summary>
public sealed class FindSession : IDisposable
{
    private const int RetriesPerPage = 3;

    private readonly object _gate = new();
    private readonly ISearchSource _source;
    private readonly int _pageCount;
    private readonly int _startPage;
    private readonly CancellationTokenSource _cancel = new();
    private readonly List<FoundMatch> _found = [];
    private readonly Dictionary<int, PageMatch[]> _byPage = [];
    private TaskCompletionSource _progressed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _current = -1;
    private int _searched;
    private int _failed;
    private bool _complete;
    private bool _refused;
    private bool _truncated;
    private int _disposed;
    private Task? _run;

    public FindSession(ISearchSource source, int pageCount, SearchOptions options, int startPage)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        _source = source;
        _pageCount = pageCount;
        Options = options;
        _startPage = pageCount == 0 ? 0 : Math.Clamp(startPage, 0, pageCount - 1);
    }

    public SearchOptions Options { get; }

    /// <summary>How long to wait before asking again after the engine restarted or was too slow for a page.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    public event Action? Changed;

    public FindProgress Progress
    {
        get
        {
            lock (_gate)
            {
                return new FindProgress(_found.Count, _searched, _pageCount, _failed, _complete, _cancel.IsCancellationRequested && !_complete, _refused, _truncated);
            }
        }
    }

    /// <summary>The match the person is on, or null before one has been chosen.</summary>
    public FoundMatch? Current
    {
        get
        {
            lock (_gate)
            {
                return _current >= 0 ? _found[_current] : null;
            }
        }
    }

    /// <summary>The number of <see cref="Current"/> among the matches found so far, counting from 1; 0 if none is chosen.</summary>
    public int CurrentOrdinal
    {
        get
        {
            lock (_gate)
            {
                return _current + 1;
            }
        }
    }

    /// <summary>Starts searching. Calling it again returns the same task.</summary>
    public Task StartAsync()
    {
        lock (_gate)
        {
            return _run ??= Task.Run(RunAsync);
        }
    }

    /// <summary>The matches found on a page so far.</summary>
    public IReadOnlyList<PageMatch> MatchesOnPage(int page)
    {
        lock (_gate)
        {
            return _byPage.TryGetValue(page, out var matches) ? matches : [];
        }
    }

    /// <summary>
    /// Moves to the next match. Past the last one found so far it waits for the search to find another; when the search is complete it goes
    /// back to the first. Null if the search ended without a match, or was cancelled.
    /// </summary>
    public async Task<FoundMatch?> NextAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_current + 1 < _found.Count)
                {
                    _current++;
                    break;
                }

                if (_complete)
                {
                    if (_found.Count == 0)
                    {
                        return null;
                    }

                    _current = 0;
                    break;
                }

                if (_cancel.IsCancellationRequested)
                {
                    return null;
                }

                wait = _progressed.Task;
            }

            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        Changed?.Invoke();
        return Current;
    }

    /// <summary>Moves to the previous match; before the first it goes to the last one found so far.</summary>
    public FoundMatch? Previous()
    {
        lock (_gate)
        {
            if (_found.Count == 0)
            {
                return null;
            }

            _current = _current <= 0 ? _found.Count - 1 : _current - 1;
        }

        Changed?.Invoke();
        return Current;
    }

    /// <summary>Stops the search. What was found stays.</summary>
    public void Cancel()
    {
        try
        {
            _cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return; // already disposed, which cancelled it
        }

        Signal();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Cancel();
        _cancel.Dispose();
    }

    private async Task RunAsync()
    {
        var token = _cancel.Token;
        try
        {
            for (var rank = 0; rank < _pageCount && !token.IsCancellationRequested; rank++)
            {
                var page = (_startPage + rank) % _pageCount;
                var reply = await SearchPageWithRetriesAsync(page, token).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                {
                    break;
                }

                lock (_gate)
                {
                    _searched++;
                    if (reply is { IsOk: true, Value: { } matches })
                    {
                        _truncated |= matches.Truncated;
                        if (matches.Matches.Count > 0)
                        {
                            var array = matches.Matches.ToArray();
                            _byPage[page] = array;
                            for (var i = 0; i < array.Length; i++)
                            {
                                _found.Add(new FoundMatch(page, i, array[i]));
                            }
                        }
                    }
                    else if (reply.Status == Status.InvalidArgument && rank == 0)
                    {
                        _refused = true; // every page would be refused for the same reason
                        _searched = _pageCount;
                        break;
                    }
                    else
                    {
                        _failed++;
                    }
                }

                Signal();
                Changed?.Invoke();
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            // Cancelled: whatever was found stays.
        }
        finally
        {
            lock (_gate)
            {
                _complete = !token.IsCancellationRequested;
            }

            Signal();
            Changed?.Invoke();
        }
    }

    private async Task<ReadReply<PageMatches>> SearchPageWithRetriesAsync(int page, CancellationToken token)
    {
        ReadReply<PageMatches> reply = default;
        for (var attempt = 0; attempt <= RetriesPerPage; attempt++)
        {
            reply = await _source.SearchAsync(page, Options, token).ConfigureAwait(false);
            if (reply.Status is not (Status.EngineCrashed or Status.Timeout))
            {
                return reply;
            }

            await Task.Delay(RetryDelay, token).ConfigureAwait(false); // a new engine is on its way
        }

        return reply;
    }

    private void Signal()
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            old = _progressed;
            _progressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        old.TrySetResult();
    }
}
