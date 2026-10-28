using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAI.Responses;
using PopoTranslation.Core;

namespace PopoTranslation.Providers.OpenAI;

#pragma warning disable OPENAI001
internal sealed class OpenAiResponsesTranslationProvider : ProviderBase
{
    private readonly ResponsesClient client;
    private readonly string model;

    public OpenAiResponsesTranslationProvider(ProviderProfile profile, string apiKey, Uri endpoint, HttpClient? httpClient) : base(profile)
    {
        model = profile.ModelId;
        var options = new ResponsesClientOptions { Endpoint = endpoint, RetryPolicy = new ClientRetryPolicy(0) };
        if (httpClient is not null) options.Transport = new HttpClientPipelineTransport(httpClient);
        client = new ResponsesClient(new ApiKeyCredential(apiKey), options);
    }

    protected override async Task<TranslationResult> TranslateCoreAsync(string source, string instruction, CancellationToken cancellationToken)
    {
        var options = new CreateResponseOptions { Model = model, Instructions = instruction, StoredOutputEnabled = false };
        options.InputItems.Add(ResponseItem.CreateUserMessageItem(source));
        ResponseResult response = await client.CreateResponseAsync(options, cancellationToken).ConfigureAwait(false);
        if (response.Status == ResponseStatus.Incomplete)
            return Result(TranslationStatus.Truncated, "incomplete", "The provider returned an incomplete translation.");
        if (response.Status != ResponseStatus.Completed)
            return Result(TranslationStatus.Failed, "incomplete", "The provider did not complete the translation.");
        var text = new System.Text.StringBuilder();
        var messageCount = 0;
        foreach (var item in response.OutputItems)
        {
            if (item is ReasoningResponseItem) continue;
            if (item is not MessageResponseItem message || message.Role != MessageRole.Assistant || message.Status.ToString() != "Completed")
                return Result(TranslationStatus.Failed, "unexpected_content", "The provider returned unsupported content.");
            if (++messageCount > 1)
                return Result(TranslationStatus.Failed, "unexpected_content", "The provider returned multiple final messages.");
            foreach (var part in message.Content)
            {
                if (part.Kind == ResponseContentPartKind.Refusal)
                    return Result(TranslationStatus.Refused, "refused", "The provider refused the translation.");
                if (part.Kind != ResponseContentPartKind.OutputText)
                    return Result(TranslationStatus.Failed, "unexpected_content", "The provider returned unsupported content.");
                text.Append(part.Text);
            }
        }
        return TextResult(text.ToString());
    }
}
#pragma warning restore OPENAI001
