using System.Collections.Concurrent;

namespace PopoTranslation.Windows.Clipboard;

// WPF clipboard APIs require an STA thread. Keeping the worker separate from the
// UI thread means a clipboard owner that is slow cannot freeze the settings window.
internal sealed class StaWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;
    private int _disposed;

    public StaWorker()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "PopoTranslation clipboard" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public T Invoke<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                try { completion.TrySetResult(action()); }
                catch (Exception error) { completion.TrySetException(error); }
            });
        }
        catch (InvalidOperationException) { throw new ObjectDisposedException(nameof(StaWorker)); }
        return completion.Task.GetAwaiter().GetResult();
    }

    public async Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _queue.Add(() =>
            {
                try { completion.TrySetResult(action()); }
                catch (Exception error) { completion.TrySetException(error); }
            });
        }
        catch (InvalidOperationException) { throw new ObjectDisposedException(nameof(StaWorker)); }
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
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
