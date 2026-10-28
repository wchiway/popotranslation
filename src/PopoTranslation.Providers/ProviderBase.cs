using PopoTranslation.Core;

namespace PopoTranslation.Providers;

internal abstract class ProviderBase(ProviderProfile profile) : ITranslationProvider
{
    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
        try
        {
            var instruction = TranslationPromptBuilder.Build(request);
            timeout.Token.ThrowIfCancellationRequested();
            var result = await TranslateCoreAsync(request.SourceText, instruction, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(TranslationStatus.Cancelled, "cancelled", "Translation was cancelled.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return Result(TranslationStatus.TimedOut, "timeout", "Translation timed out.");
        }
        catch (OperationCanceledException)
        {
            return Result(TranslationStatus.Failed, "provider_error", "The translation provider request failed. Check the service configuration.");
        }
        catch (System.ClientModel.ClientResultException ex)
        {
            return HttpError(ex.Status);
        }
        catch (global::Anthropic.Exceptions.AnthropicApiException ex)
        {
            return HttpError((int)ex.StatusCode);
        }
        catch (Google.GenAI.ApiException ex)
        {
            return HttpError(ex.StatusCode);
        }
        catch (HttpRequestException ex) when (ex.StatusCode.HasValue)
        {
            return HttpError((int)ex.StatusCode.Value);
        }
        catch (Exception)
        {
            return Result(TranslationStatus.Failed, "provider_error", "The translation provider request failed. Check the service configuration.");
        }
    }

    private static TranslationResult HttpError(int status) => status is >= 100 and <= 599
        ? Result(TranslationStatus.Failed, $"http_{status}", $"The provider returned HTTP {status}. Check the service configuration and try again.")
        : Result(TranslationStatus.Failed, "provider_error", "The translation provider request failed. Check the service configuration.");

    protected abstract Task<TranslationResult> TranslateCoreAsync(string source, string instruction, CancellationToken cancellationToken);

    protected static TranslationResult Result(TranslationStatus status, string? code = null, string? message = null) =>
        new(status, null, code, message);

    protected static TranslationResult TextResult(string? text) => string.IsNullOrWhiteSpace(text)
        ? Result(TranslationStatus.Empty, "empty", "The provider returned no translation.")
        : new(TranslationStatus.Completed, text, null, null);
}
