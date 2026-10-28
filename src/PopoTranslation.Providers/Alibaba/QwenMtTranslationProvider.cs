using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PopoTranslation.Core;

namespace PopoTranslation.Providers.Alibaba;

// Official contract checked 2026-10-28: https://docs.bailian.console.aliyun.com/zh/model-studio/machine-translation
// and https://docs.bailian.console.aliyun.com/zh/model-studio/qwen-mt-api.
public sealed class QwenMtTranslationProvider : MachineTranslationProviderBase
{
    private readonly string apiKey;
    private readonly string model;
    private readonly Uri operation;

    public QwenMtTranslationProvider(ProviderProfile profile, string apiKey, Uri endpoint, HttpClient httpClient)
        : base(profile, endpoint, httpClient)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API key is required.", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(profile.ModelId)) throw new ArgumentException("Model ID is required.", nameof(profile));
        var path = endpoint.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Endpoint must be a compatible API base URL.", nameof(endpoint));
        if (!path.EndsWith("/compatible-mode/v1", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Endpoint must end in /compatible-mode/v1.", nameof(endpoint));
        operation = new UriBuilder(endpoint) { Path = path + "/chat/completions" }.Uri;
        this.apiKey = apiKey;
        model = profile.ModelId;
    }

    protected override async Task<TranslationResult> TranslateCoreAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.Style))
            return Failed("unsupported_style", "Qwen-MT does not support this translation style setting.");
        var sourceLanguage = string.IsNullOrWhiteSpace(request.SourceLanguage) ? "auto" : Language(request.SourceLanguage, true);
        var targetLanguage = Language(request.TargetLanguage, false);
        if (sourceLanguage is null || targetLanguage is null ||
            (model.Equals("qwen-mt-lite", StringComparison.OrdinalIgnoreCase) &&
             ((sourceLanguage != "auto" && !LiteLanguages.Contains(sourceLanguage)) || !LiteLanguages.Contains(targetLanguage))))
            return Failed("unsupported_language", "The language is not supported by this provider.");

        var body = new
        {
            model,
            messages = new[] { new { role = "user", content = request.SourceText } },
            translation_options = new { source_lang = sourceLanguage, target_lang = targetLanguage }
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, operation) { Content = JsonContent.Create(body) };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await HttpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return HttpError(response);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1 || choices[0].ValueKind != JsonValueKind.Object)
            return Failed("invalid_response", "The provider returned an invalid response.");
        var choice = choices[0];
        if (!choice.TryGetProperty("finish_reason", out var finish) || finish.ValueKind != JsonValueKind.String)
            return Failed("incomplete", "The provider did not complete the translation.");
        var reason = finish.GetString();
        if (reason == "length") return new(TranslationStatus.Truncated, null, "truncated", "The provider truncated the translation.");
        if (reason == "content_filter") return new(TranslationStatus.Refused, null, "refused", "The provider refused the translation.");
        if (reason != "stop") return Failed("incomplete", "The provider did not complete the translation.");
        if (!choice.TryGetProperty("message", out var result) || result.ValueKind != JsonValueKind.Object)
            return Failed("invalid_response", "The provider returned an invalid response.");
        if (result.TryGetProperty("refusal", out var refusal) && refusal.ValueKind != JsonValueKind.Null &&
            (refusal.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(refusal.GetString())))
            return new(TranslationStatus.Refused, null, "refused", "The provider refused the translation.");
        if ((result.TryGetProperty("tool_calls", out var tools) && tools.ValueKind != JsonValueKind.Null) ||
            (result.TryGetProperty("function_call", out var function) && function.ValueKind != JsonValueKind.Null) ||
            !result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            return Failed("invalid_response", "The provider returned an invalid response.");
        var translated = content.GetString();
        if (!string.IsNullOrWhiteSpace(translated) && !SameLineStructure(request.SourceText, translated))
            return Failed("line_break_mismatch", "The translation did not preserve the line structure.");
        return TextResult(translated);
    }

    private static bool SameLineStructure(string source, string translated)
    {
        static string[] Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var originalLines = Lines(source);
        var translatedLines = Lines(translated);
        return originalLines.Length == translatedLines.Length &&
            originalLines.Zip(translatedLines).All(pair => string.IsNullOrWhiteSpace(pair.First) == string.IsNullOrWhiteSpace(pair.Second));
    }

    private static string? Language(string value, bool source)
    {
        value = value.Trim();
        if (source && value.Equals("auto", StringComparison.OrdinalIgnoreCase)) return "auto";
        var alias = value switch
        {
            "简体中文" => "zh", "繁體中文" or "繁体中文" or "zh-tw" or "zh-TW" => "zh_tw",
            "日本語" => "ja", "한국어" => "ko", "Français" => "fr", "Deutsch" => "de",
            "Español" => "es", "Português" => "pt", "Русский" => "ru", _ => value
        };
        // The official language table permits either the English name or the listed code.
        foreach (var entry in Languages)
        {
            var parts = entry.Split('|');
            if (alias.Equals(parts[0], StringComparison.OrdinalIgnoreCase) || alias.Equals(parts[1], StringComparison.OrdinalIgnoreCase))
                return parts[1];
        }
        return null;
    }

    private static readonly string[] LiteLanguages =
    [
        "en", "zh", "zh_tw", "ru", "ja", "ko", "es", "fr", "pt", "de", "it", "th", "vi", "id", "ms",
        "ar", "hi", "he", "ur", "bn", "pl", "nl", "tr", "km", "cs", "sv", "hu", "da", "fi", "tl", "fa"
    ];

    private static readonly string[] Languages =
    [
        "English|en", "Chinese|zh", "Traditional Chinese|zh_tw", "Russian|ru", "Japanese|ja", "Korean|ko",
        "Spanish|es", "French|fr", "Portuguese|pt", "German|de", "Italian|it", "Thai|th", "Vietnamese|vi",
        "Indonesian|id", "Malay|ms", "Arabic|ar", "Hindi|hi", "Hebrew|he", "Burmese|my", "Tamil|ta",
        "Urdu|ur", "Bengali|bn", "Polish|pl", "Dutch|nl", "Romanian|ro", "Turkish|tr", "Khmer|km",
        "Lao|lo", "Cantonese|yue", "Czech|cs", "Greek|el", "Swedish|sv", "Hungarian|hu", "Danish|da",
        "Finnish|fi", "Ukrainian|uk", "Bulgarian|bg", "Serbian|sr", "Telugu|te", "Afrikaans|af", "Armenian|hy",
        "Assamese|as", "Asturian|ast", "Basque|eu", "Belarusian|be", "Bosnian|bs", "Catalan|ca", "Cebuano|ceb",
        "Croatian|hr", "Egyptian Arabic|arz", "Estonian|et", "Galician|gl", "Georgian|ka", "Gujarati|gu",
        "Icelandic|is", "Javanese|jv", "Kannada|kn", "Kazakh|kk", "Latvian|lv", "Lithuanian|lt",
        "Luxembourgish|lb", "Macedonian|mk", "Maithili|mai", "Maltese|mt", "Marathi|mr", "Mesopotamian Arabic|acm",
        "Moroccan Arabic|ary", "Najdi Arabic|ars", "Nepali|ne", "North Azerbaijani|az", "North Levantine Arabic|apc",
        "Northern Uzbek|uz", "Norwegian Bokmål|nb", "Norwegian Nynorsk|nn", "Tagalog|tl", "fa|fa"
    ];
}
