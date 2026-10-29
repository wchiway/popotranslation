namespace PopoTranslation.Core;

/// <summary>Reads a cached translation before invoking the underlying provider. Use one instance per operation.</summary>
public sealed class CachedTranslationProvider : ITranslationProvider
{
    private readonly ITranslationProvider _inner;
    private readonly ITranslationStore _store;
    private readonly TranslationCacheContext _context;

    public CachedTranslationProvider(ITranslationProvider inner, ITranslationStore store, TranslationCacheContext context)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(context);
        _inner = inner;
        _store = store;
        _context = context;
    }

    public bool CacheHit { get; private set; }

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CacheHit = false;
        cancellationToken.ThrowIfCancellationRequested();
        string? cached;
        try
        {
            cached = await _store.FindCachedAsync(_context with { Request = request }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await _inner.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(cached))
        {
            CacheHit = true;
            return new TranslationResult(TranslationStatus.Completed, cached, null, null);
        }

        return await _inner.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
