using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using PopoTranslation.Core;

namespace PopoTranslation.Providers.Anthropic;

internal sealed class AnthropicMessagesTranslationProvider : ProviderBase
{
    private readonly AnthropicClient client;
    private readonly string model;

    public AnthropicMessagesTranslationProvider(ProviderProfile profile, string apiKey, Uri endpoint, HttpClient? httpClient) : base(profile)
    {
        model = profile.ModelId;
        var options = new ClientOptions { ApiKey = apiKey, BaseUrl = endpoint.ToString(), MaxRetries = 0 };
        if (httpClient is not null) options = options with { HttpClient = httpClient };
        client = new AnthropicClient(options);
    }

    protected override async Task<TranslationResult> TranslateCoreAsync(string source, string instruction, CancellationToken cancellationToken)
    {
        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = 4096,
            System = instruction,
            Messages = [new MessageParam { Role = Role.User, Content = source }]
        }, cancellationToken).ConfigureAwait(false);
        var reason = response.StopReason?.Raw();
        if (reason == "refusal") return Result(TranslationStatus.Refused, "refused", "The provider refused the translation.");
        if (reason == "max_tokens") return Result(TranslationStatus.Truncated, "truncated", "The provider truncated the translation.");
        if (reason != "end_turn") return Result(TranslationStatus.Failed, "incomplete", "The provider did not complete the translation.");
        var text = new System.Text.StringBuilder();
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var textBlock)) text.Append(textBlock.Text);
            else if (!block.TryPickThinking(out _) && !block.TryPickRedactedThinking(out _))
                return Result(TranslationStatus.Failed, "unexpected_content", "The provider returned unsupported content.");
        }
        return TextResult(text.ToString());
    }
}
