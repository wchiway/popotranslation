namespace PopoTranslation.Core;

public interface ITranslationProvider
{
    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken);
}
