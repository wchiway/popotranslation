using System.Net;
using System.Text.Json;
using PopoTranslation.Core;

namespace PopoTranslation.Providers;

// API details checked 2026-10-28: https://fanyi-api.baidu.com/doc/23
// and https://ai.youdao.com/DOCSIRMA/html/trans/api/wbfy/index.html
public abstract class SignedTranslationProvider(ProviderProfile profile, string credentialId, string secret, Uri endpoint, HttpClient httpClient) : ITranslationProvider
{
    protected string CredentialId { get; } = credentialId;
    protected string Secret { get; } = secret;
    protected Uri Endpoint { get; } = endpoint;
    protected HttpClient HttpClient { get; } = httpClient;

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(request.SourceText)) return Failure("empty_input", "The source text is empty.");
            var from = string.IsNullOrWhiteSpace(request.SourceLanguage) ? "auto" : Language(request.SourceLanguage);
            var to = Language(request.TargetLanguage);
            if (from is null || to is null || to == "auto")
                return Failure("unsupported_language", "The selected language is not supported by this provider.");
            var result = await TranslateCoreAsync(request.SourceText, from, to, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(TranslationStatus.Cancelled, null, "cancelled", "Translation was cancelled.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return new(TranslationStatus.TimedOut, null, "timeout", "Translation timed out.");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is >= HttpStatusCode.Continue and <= (HttpStatusCode)599)
        {
            var status = (int)ex.StatusCode.Value;
            return Failure($"http_{status}", $"The provider returned HTTP {status}. Check the service configuration and try again.");
        }
        catch (Exception)
        {
            return Failure("provider_error", "The translation provider request failed. Check the service configuration.");
        }
    }

    protected abstract string? Language(string language);
    protected abstract Task<TranslationResult> TranslateCoreAsync(string source, string from, string to, CancellationToken cancellationToken);

    protected async Task<JsonDocument> PostAsync(IEnumerable<KeyValuePair<string, string>> fields, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var response = await HttpClient.PostAsync(Endpoint, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    protected static TranslationResult Failure(string code, string message) => new(TranslationStatus.Failed, null, code, message);
    protected static TranslationResult Text(string? value) => string.IsNullOrWhiteSpace(value)
        ? new(TranslationStatus.Empty, null, "empty", "The provider returned no translation.")
        : new(TranslationStatus.Completed, value, null, null);

    protected static string? CommonLanguage(string? language, bool youdao)
    {
        return language?.Trim().ToLowerInvariant() switch
        {
            "auto" or "自动识别" => "auto",
            "chinese" or "中文" or "简体中文" or "zh" or "zh-cn" or "zh-hans" or "zh-chs" => youdao ? "zh-CHS" : "zh",
            "繁体中文" or "繁體中文" or "traditional chinese" or "zh-tw" or "zh-hant" or "zh-cht" or "cht" => youdao ? "zh-CHT" : "cht",
            "english" or "英语" or "英文" or "en" => "en",
            "japanese" or "日语" or "日文" or "日本語" or "ja" or "jp" => youdao ? "ja" : "jp",
            "korean" or "한국어" or "韩语" or "韩文" or "ko" or "kor" => youdao ? "ko" : "kor",
            "french" or "français" or "法语" or "fr" or "fra" => youdao ? "fr" : "fra",
            "german" or "deutsch" or "德语" or "de" => "de",
            "spanish" or "español" or "西班牙语" or "es" or "spa" => youdao ? "es" : "spa",
            "russian" or "русский" or "俄语" or "ru" => "ru",
            "italian" or "意大利语" or "it" => "it",
            "portuguese" or "português" or "葡萄牙语" or "pt" => "pt",
            "arabic" or "阿拉伯语" or "ar" or "ara" => youdao ? "ar" : "ara",
            "thai" or "泰语" or "th" or "thailand" => youdao ? "th" : "th",
            "vietnamese" or "越南语" or "vi" or "vie" => youdao ? "vi" : "vie",
            "dutch" or "荷兰语" or "nl" => "nl",
            _ => null
        };
    }
}
