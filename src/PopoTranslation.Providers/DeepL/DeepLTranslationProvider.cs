using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using PopoTranslation.Core;

namespace PopoTranslation.Providers.DeepL;

public sealed class DeepLTranslationProvider : MachineTranslationProviderBase
{
    private readonly string apiKey;
    private readonly Uri operation;

    public DeepLTranslationProvider(ProviderProfile profile, string credentialIdOrKey, string? secret, Uri endpoint, HttpClient httpClient)
        : base(profile, endpoint, httpClient)
    {
        if (string.IsNullOrWhiteSpace(credentialIdOrKey)) throw new ArgumentException("API key is required.", nameof(credentialIdOrKey));
        if (!string.IsNullOrEmpty(secret)) throw new ArgumentException("DeepL uses one API key.", nameof(secret));
        var path = endpoint.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/translate", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Endpoint must be a base URL.", nameof(endpoint));
        if (!path.EndsWith("/v2", StringComparison.OrdinalIgnoreCase)) path += "/v2";
        operation = new UriBuilder(endpoint) { Path = path + "/translate" }.Uri;
        apiKey = credentialIdOrKey;
    }

    protected override async Task<TranslationResult> TranslateCoreAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object>
        {
            ["text"] = new[] { request.SourceText },
            ["target_lang"] = Language(request.TargetLanguage, true, false),
            ["split_sentences"] = "nonewlines",
            ["preserve_formatting"] = true
        };
        if (!string.IsNullOrWhiteSpace(request.SourceLanguage) && !request.SourceLanguage.Equals("auto", StringComparison.OrdinalIgnoreCase))
            body["source_lang"] = Language(request.SourceLanguage, true, true);
        using var message = new HttpRequestMessage(HttpMethod.Post, operation) { Content = JsonContent.Create(body) };
        message.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);
        using var response = await HttpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return HttpError(response);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("translations", out var translations) || translations.ValueKind != JsonValueKind.Array || translations.GetArrayLength() != 1 ||
            translations[0].ValueKind != JsonValueKind.Object || !translations[0].TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
            return Failed("invalid_response", "The provider returned an invalid response.");
        return TextResult(text.GetString());
    }
}
