using System.Collections.Concurrent;

namespace PopoTranslation.Windows.Automation;

// A single outstanding call prevents stalled providers from creating an unbounded queue.
internal sealed class AutomationWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(1);
    private readonly Thread _thread;
    private int _busy;
    private int _disposed;

    public AutomationWorker()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "PopoTranslation UI Automation" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public async Task<(bool Accepted, T? Value)> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken,
        bool waitForCompletion = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return (false, default);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    completion.TrySetResult(action());
                }
                catch (Exception error) { completion.TrySetException(error); }
                finally { Volatile.Write(ref _busy, 0); }
            });
        }
        catch (InvalidOperationException)
        {
            Volatile.Write(ref _busy, 0);
            return (false, default);
        }
        return (true, await (waitForCompletion ? completion.Task : completion.Task.WaitAsync(cancellationToken)).ConfigureAwait(false));
    }

    private void Run()
    {
        foreach (var action in _queue.GetConsumingEnumerable()) action();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _queue.CompleteAdding();
    }
}
