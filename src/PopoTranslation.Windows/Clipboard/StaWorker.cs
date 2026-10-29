using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace PopoTranslation.Windows.Clipboard;

// Run clipboard operations on a dedicated STA with a message pump so COM and
// delayed clipboard rendering can dispatch messages while a transaction waits.
internal sealed class StaWorker : IDisposable
{
    private readonly TaskCompletionSource<Dispatcher> _dispatcher = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _transaction = new(1, 1);
    private readonly object _sync = new();
    private bool _disposed;
    private int _pending;

    public StaWorker()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "PopoTranslation clipboard" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public T Invoke<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        if (_dispatcher.Task.IsCompletedSuccessfully && _dispatcher.Task.Result.CheckAccess())
            throw new InvalidOperationException("Cannot synchronously invoke the clipboard worker from its own thread.");
        return InvokeAsync(action, cancellationToken).GetAwaiter().GetResult();
    }

    public async Task<T> InvokeAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending++;
        }

        try
        {
            await _transaction.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var dispatcher = await _dispatcher.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return await dispatcher.InvokeAsync(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return action();
                }).Task.Unwrap().ConfigureAwait(false);
            }
            finally { _transaction.Release(); }
        }
        finally
        {
            lock (_sync)
            {
                _pending--;
                if (_disposed && _pending == 0) Shutdown();
            }
        }
    }

    private void Run()
    {
        var initialized = false;
        try
        {
            Marshal.ThrowExceptionForHR(OleInitialize(0));
            initialized = true;
            _dispatcher.TrySetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }
        catch (Exception error) { _dispatcher.TrySetException(error); }
        finally { if (initialized) OleUninitialize(); }
    }

    private void Shutdown()
    {
        _ = _dispatcher.Task.ContinueWith(task =>
        {
            if (task.Status == TaskStatus.RanToCompletion)
                task.Result.BeginInvokeShutdown(DispatcherPriority.Background);
        }, TaskScheduler.Default);
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(nint reserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_pending == 0) Shutdown();
        }
    }
}
