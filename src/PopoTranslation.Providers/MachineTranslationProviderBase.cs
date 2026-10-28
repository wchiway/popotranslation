using PopoTranslation.Core;

namespace PopoTranslation.Providers;

// Protocol references checked 2026-10-28: https://help.aliyun.com/en/sdk/product-overview/rpc-mechanism
// and https://developers.deepl.com/api-reference/translate/request-translation.
public abstract class MachineTranslationProviderBase : ITranslationProvider
{
    private readonly int timeoutSeconds;

    protected MachineTranslationProviderBase(ProviderProfile profile, Uri endpoint, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(httpClient);
        if (profile.TimeoutSeconds <= 0) throw new ArgumentException("Timeout must be positive.", nameof(profile));
        if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != Uri.UriSchemeHttps && !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("Endpoint must be HTTPS or loopback HTTP without credentials, query or fragment.", nameof(endpoint));
        timeoutSeconds = profile.TimeoutSeconds;
        Endpoint = endpoint;
        HttpClient = httpClient;
    }

    protected Uri Endpoint { get; }
    protected HttpClient HttpClient { get; }

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (request is null || string.IsNullOrWhiteSpace(request.SourceText) || string.IsNullOrWhiteSpace(request.TargetLanguage))
                return Failed("invalid_request", "Source text and target language are required.");
            var result = await TranslateCoreAsync(request, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (result.Status == TranslationStatus.Completed && result.Text is not null &&
                CountLineBreaks(request.SourceText) != CountLineBreaks(result.Text))
                return Failed("line_break_mismatch", "The translation did not preserve the number of line breaks.");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(TranslationStatus.Cancelled, null, "cancelled", "Translation was cancelled."); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        { return new(TranslationStatus.TimedOut, null, "timeout", "Translation timed out."); }
        catch (Exception)
        { return Failed("provider_error", "The translation provider request failed. Check the service configuration."); }
    }

    private static int CountLineBreaks(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                count++;
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (text[i] == '\n') count++;
        }
        return count;
    }

    protected abstract Task<TranslationResult> TranslateCoreAsync(TranslationRequest request, CancellationToken cancellationToken);
    protected static TranslationResult Failed(string code, string message) => new(TranslationStatus.Failed, null, code, message);
    protected static TranslationResult HttpError(HttpResponseMessage response) => Failed($"http_{(int)response.StatusCode}", $"The provider returned HTTP {(int)response.StatusCode}. Check the service configuration and try again.");
    protected static TranslationResult TextResult(string? text) => string.IsNullOrWhiteSpace(text)
        ? new(TranslationStatus.Empty, null, "empty", "The provider returned no translation.")
        : new(TranslationStatus.Completed, text, null, null);

    protected static string Language(string value, bool deepL, bool source)
    {
        var code = value.Trim() switch
        {
            "简体中文" or "Chinese" => deepL && !source ? "zh-hans" : "zh",
            "繁體中文" => deepL ? (source ? "zh" : "zh-hant") : "zh-tw",
            "English" => "en", "日本語" => "ja", "한국어" => "ko", "Français" => "fr",
            "Deutsch" => "de", "Español" => "es", "Português" => "pt", "Русский" => "ru",
            var other => other
        };
        code = code.ToLowerInvariant();
        // Restrict inputs to the verified language subset used by the application's selectors.
        if (deepL && source && code is "zh-hans" or "zh-hant") code = "zh";
        var supported = deepL
            ? source ? new[] { "zh", "en", "ja", "ko", "fr", "de", "es", "pt", "ru" }
                     : new[] { "zh", "zh-hans", "zh-hant", "en", "en-us", "en-gb", "ja", "ko", "fr", "de", "es", "pt", "pt-br", "pt-pt", "ru" }
            : new[] { "zh", "en", "ja", "ko", "fr", "de", "es", "pt", "ru" };
        if (!supported.Contains(code, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported language.", nameof(value));
        return deepL ? code.ToUpperInvariant() : code;
    }
}
