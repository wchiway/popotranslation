namespace PopoTranslation.Core;

public sealed class TranslationCoordinator
{
    private readonly object _gate = new();
    private TranslationOperation? _current;

    public TranslationOperation Start(ITranslationProvider provider, TranslationRequest request, ProviderProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.TimeoutSeconds is <= 0 or > 86400) throw new ArgumentOutOfRangeException(nameof(profile));
        var operation = new TranslationOperation(request, profile, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        TranslationOperation? previous;
        lock (_gate)
        {
            previous = _current;
            _current = operation;
            operation.Completion = Task.Run(() => ExecuteAsync(operation, provider));
        }
        previous?.Cancellation.Cancel();
        return operation;
    }

    public void Cancel()
    {
        TranslationOperation? previous;
        lock (_gate) { previous = _current; _current = null; }
        previous?.Cancellation.Cancel();
    }

    public bool IsCurrent(TranslationOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate) return ReferenceEquals(_current, operation) && !operation.Cancellation.IsCancellationRequested && !operation.Applied;
    }

    // Run on the editor worker immediately before the synchronous final check and write.
    // Starting or cancelling another operation waits until this callback completes.
    public bool TryExecuteIfCurrent<TResult>(TranslationOperation operation, Func<TResult> action, out TResult result)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            if (!IsEligible(operation))
            {
                result = default!;
                return false;
            }
            // Consume the operation even when the action fails or throws: the write may have occurred.
            operation.Applied = true;
            result = action();
            return true;
        }
    }

    private bool IsEligible(TranslationOperation operation) =>
        ReferenceEquals(_current, operation) && !operation.Cancellation.IsCancellationRequested &&
        !operation.Applied && operation.Result is { IsSuccessful: true };

    // The callback must perform its final editor validation and replacement synchronously.
    public bool TryApply(TranslationOperation operation, Func<string, bool> validateAndApply)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(validateAndApply);
        lock (_gate)
        {
            if (!ReferenceEquals(_current, operation) || operation.Cancellation.IsCancellationRequested ||
                operation.Applied || operation.Result is not { IsSuccessful: true, Text: not null } result)
                return false;
            if (!validateAndApply(result.Text)) return false;
            operation.Applied = true;
            return true;
        }
    }

    private async Task<TranslationResult> ExecuteAsync(TranslationOperation operation, ITranslationProvider provider)
    {
        TranslationResult result;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(operation.Profile.TimeoutSeconds));
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(operation.Cancellation.Token, timeout.Token);
        try
        {
            combined.Token.ThrowIfCancellationRequested();
            result = await provider.TranslateAsync(operation.Request, combined.Token).WaitAsync(combined.Token).ConfigureAwait(false)
                ?? new TranslationResult(TranslationStatus.Failed, null, null, null);
        }
        catch (OperationCanceledException)
        {
            result = new TranslationResult(operation.Cancellation.IsCancellationRequested ? TranslationStatus.Cancelled : TranslationStatus.TimedOut, null, null, null);
        }
        catch (Exception)
        {
            result = new TranslationResult(TranslationStatus.Failed, null, null, null);
        }
        lock (_gate)
        {
            if (!ReferenceEquals(_current, operation) || operation.Cancellation.IsCancellationRequested)
                return new TranslationResult(TranslationStatus.Cancelled, null, null, null);
            if (timeout.IsCancellationRequested)
                return new TranslationResult(TranslationStatus.TimedOut, null, null, null);
            operation.Result = result;
            return result;
        }
    }
}
