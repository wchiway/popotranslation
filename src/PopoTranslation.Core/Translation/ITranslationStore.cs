namespace PopoTranslation.Core;

/// <summary>All effective inputs that may change a translation. Advanced values are used only to compute the cache key.</summary>
public sealed record TranslationCacheContext(
    TranslationRequest Request,
    string ProviderId,
    ApiProtocol Protocol,
    string BaseUrl,
    string ModelId,
    string CredentialReference,
    string? SecondaryCredentialReference,
    string? AdvancedHeadersJson,
    string? AdvancedBodyJson)
{
    public override string ToString() => "TranslationCacheContext { content redacted }";
}

public sealed record TranslationHistoryEntry(
    long Id,
    DateTimeOffset CreatedAt,
    string SourceText,
    string TranslatedText,
    string ProviderId,
    ApiProtocol Protocol,
    string TargetLanguage,
    string? SourceLanguage,
    string? Style,
    bool CacheHit)
{
    public override string ToString() => "TranslationHistoryEntry { content redacted }";
}

public interface ITranslationStore
{
    Task<string?> FindCachedAsync(TranslationCacheContext context, CancellationToken cancellationToken = default);

    /// <summary>Records a complete successful result, optionally updating the cache in the same transaction.</summary>
    Task SaveSuccessAsync(TranslationCacheContext context, string translatedText, bool cacheHit, bool saveToHistory, bool saveToCache,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TranslationHistoryEntry>> GetHistoryAsync(int limit, CancellationToken cancellationToken = default);
    Task ClearHistoryAsync(CancellationToken cancellationToken = default);
    Task ClearCacheAsync(CancellationToken cancellationToken = default);
}
