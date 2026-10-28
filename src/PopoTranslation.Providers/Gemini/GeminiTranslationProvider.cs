using Google.GenAI;
using Google.GenAI.Types;
using PopoTranslation.Core;

namespace PopoTranslation.Providers.Gemini;

internal sealed class GeminiTranslationProvider : ProviderBase
{
    private readonly Client client;
    private readonly string model;

    public GeminiTranslationProvider(ProviderProfile profile, string apiKey, Uri endpoint, HttpClient? httpClient) : base(profile)
    {
        model = profile.ModelId;
        var options = new HttpOptions { BaseUrl = endpoint.ToString().TrimEnd('/'), ApiVersion = "v1beta", RetryOptions = new HttpRetryOptions { Attempts = 1 } };
        var clientOptions = httpClient is null ? null : new ClientOptions { HttpClientFactory = () => httpClient };
        client = new Client(enterprise: false, vertexAI: false, apiKey: apiKey, httpOptions: options, clientOptions: clientOptions);
    }

    protected override async Task<TranslationResult> TranslateCoreAsync(string source, string instruction, CancellationToken cancellationToken)
    {
        var response = await client.Models.GenerateContentAsync(model, source, new GenerateContentConfig
        {
            SystemInstruction = new Content { Parts = [new Part { Text = instruction }] }
        }, cancellationToken).ConfigureAwait(false);
        if (response.PromptFeedback?.BlockReason is not null)
            return Result(TranslationStatus.Refused, "refused", "The provider blocked the translation request.");
        var candidate = response.Candidates?.FirstOrDefault();
        if (candidate is null) return Result(TranslationStatus.Empty, "empty", "The provider returned no translation.");
        var reason = candidate.FinishReason?.ToString();
        if (reason == "MAX_TOKENS") return Result(TranslationStatus.Truncated, "truncated", "The provider truncated the translation.");
        if (reason is "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII")
            return Result(TranslationStatus.Refused, "refused", "The provider blocked the translation.");
        if (reason != "STOP") return Result(TranslationStatus.Failed, "incomplete", "The provider did not complete the translation.");
        var text = new System.Text.StringBuilder();
        foreach (var part in candidate.Content?.Parts ?? [])
        {
            if (part.FunctionCall is not null || part.FunctionResponse is not null || part.ToolCall is not null ||
                part.ToolResponse is not null || part.ExecutableCode is not null || part.CodeExecutionResult is not null ||
                part.InlineData is not null || part.FileData is not null)
                return Result(TranslationStatus.Failed, "unexpected_content", "The provider returned unsupported content.");
            if (part.Thought == true) continue;
            if (part.Text is null) return Result(TranslationStatus.Failed, "unexpected_content", "The provider returned unsupported content.");
            text.Append(part.Text);
        }
        return TextResult(text.ToString());
    }
}
