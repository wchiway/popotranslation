using PopoTranslation.Core;
using PopoTranslation.Providers.Anthropic;
using PopoTranslation.Providers.Gemini;
using PopoTranslation.Providers.OpenAI;

namespace PopoTranslation.Providers;

public static class TranslationProviderFactory
{
    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Creates a translation provider. The optional HTTP client is a testing or custom transport seam.
    /// Its owner must disable automatic redirects, avoid automatic retries, set an infinite HTTP timeout,
    /// and keep it alive for the provider's lifetime. The provider enforces its own request deadline.</summary>
    public static ITranslationProvider Create(ProviderProfile profile, string apiKey, HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API key is required.", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(profile.ModelId)) throw new ArgumentException("Model ID is required.", nameof(profile));
        if (profile.TimeoutSeconds <= 0) throw new ArgumentException("Timeout must be positive.", nameof(profile));
        var endpoint = ProviderEndpoint.Parse(profile.Protocol, profile.BaseUrl);
        httpClient ??= SharedHttpClient;
        return profile.Protocol switch
        {
            ApiProtocol.OpenAiChatCompletions => new OpenAiChatTranslationProvider(profile, apiKey, endpoint, httpClient),
            ApiProtocol.OpenAiResponses => new OpenAiResponsesTranslationProvider(profile, apiKey, endpoint, httpClient),
            ApiProtocol.AnthropicMessages => new AnthropicMessagesTranslationProvider(profile, apiKey, endpoint, httpClient),
            ApiProtocol.GeminiGenerateContent => new GeminiTranslationProvider(profile, apiKey, endpoint, httpClient),
            _ => throw new ArgumentException("Unsupported protocol.", nameof(profile))
        };
    }
}
