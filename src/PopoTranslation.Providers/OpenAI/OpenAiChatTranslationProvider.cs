using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAI;
using OpenAI.Chat;
using PopoTranslation.Core;

namespace PopoTranslation.Providers.OpenAI;

internal sealed class OpenAiChatTranslationProvider : ProviderBase
{
    private readonly ChatClient client;

    public OpenAiChatTranslationProvider(ProviderProfile profile, string apiKey, Uri endpoint, HttpClient? httpClient) : base(profile)
    {
        var options = new OpenAIClientOptions { Endpoint = endpoint, RetryPolicy = new ClientRetryPolicy(0) };
        if (httpClient is not null) options.Transport = new HttpClientPipelineTransport(httpClient);
        client = new ChatClient(profile.ModelId, new ApiKeyCredential(apiKey), options);
    }

    protected override async Task<TranslationResult> TranslateCoreAsync(string source, string instruction, CancellationToken cancellationToken)
    {
        ChatCompletion response = await client.CompleteChatAsync(
            [new SystemChatMessage(instruction), new UserChatMessage(source)],
            new ChatCompletionOptions(), cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(response.Refusal) || response.FinishReason == ChatFinishReason.ContentFilter)
            return Result(TranslationStatus.Refused, "refused", "The provider refused the translation.");
        if (response.FinishReason == ChatFinishReason.Length)
            return Result(TranslationStatus.Truncated, "truncated", "The provider truncated the translation.");
        if (response.FinishReason != ChatFinishReason.Stop)
            return Result(TranslationStatus.Failed, "incomplete", "The provider did not complete the translation.");
        if (response.ToolCalls.Count > 0)
            return Result(TranslationStatus.Failed, "unexpected_content", "The provider returned unsupported content.");
        if (response.Content.Any(part => part.Kind != ChatMessageContentPartKind.Text))
            return Result(TranslationStatus.Failed, "unexpected_content", "The provider returned unsupported content.");
        return TextResult(string.Concat(response.Content.Select(part => part.Text)));
    }
}
