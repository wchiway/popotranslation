using System.Net;
using System.Text;
using System.Text.Json;
using PopoTranslation.Core;
using PopoTranslation.Providers;

namespace PopoTranslation.Providers.Tests;

public sealed class AdvancedRequestTests
{
    [Theory]
    [InlineData(ApiProtocol.OpenAiChatCompletions)]
    [InlineData(ApiProtocol.OpenAiResponses)]
    [InlineData(ApiProtocol.AnthropicMessages)]
    [InlineData(ApiProtocol.GeminiGenerateContent)]
    public async Task AddsHeadersAndBodyWithoutChangingInputOrResponseValidation(ApiProtocol protocol)
    {
        var handler = new CaptureHandler(protocol);
        var body = protocol switch
        {
            ApiProtocol.GeminiGenerateContent => "{\"generationConfig\":{\"temperature\":0.3,\"topP\":0.8}}",
            ApiProtocol.AnthropicMessages => "{\"temperature\":0.3,\"max_tokens\":2048}",
            _ => "{\"temperature\":0.3}"
        };
        var options = new AdvancedRequestOptions("{\"X-Custom-Feature\":\"enabled\"}", body);
        options.Validate(protocol);
        var provider = TranslationProviderFactory.Create(Profile(protocol), "test-key", new HttpClient(handler), options);
        var result = await provider.TranslateAsync(new TranslationRequest("original", "Chinese", null, null), TestContext.Current.CancellationToken);
        Assert.Equal(TranslationStatus.Completed, result.Status);
        Assert.Equal("translated", result.Text);
        Assert.Equal("enabled", handler.Header);
        using var json = JsonDocument.Parse(handler.Body!);
        var root = json.RootElement;
        Assert.Equal(0.3, (protocol == ApiProtocol.GeminiGenerateContent ? root.GetProperty("generationConfig") : root).GetProperty("temperature").GetDouble());
        Assert.Contains("original", handler.Body);
        if (protocol == ApiProtocol.AnthropicMessages) Assert.Equal(2048, root.GetProperty("max_tokens").GetInt32());
        if (protocol == ApiProtocol.OpenAiResponses) Assert.False(root.GetProperty("store").GetBoolean());
        var second = await provider.TranslateAsync(new TranslationRequest("again", "Chinese", null, null), TestContext.Current.CancellationToken);
        Assert.Equal(TranslationStatus.Completed, second.Status);
        Assert.Contains("again", handler.Body);
        Assert.Equal(2, handler.Count);
    }

    [Theory]
    [InlineData("{\"Authorization\":\"secret\"}")]
    [InlineData("{\"X-Api-Key\":\"secret\"}")]
    [InlineData("{\"Host\":\"evil.test\"}")]
    [InlineData("{\"X-Forwarded-Host\":\"evil.test\"}")]
    [InlineData("{\"X-Method-Override\":\"DELETE\"}")]
    [InlineData("{\"Content-Length\":\"1\"}")]
    [InlineData("{\"X-Test\":\"bad\\r\\nvalue\"}")]
    [InlineData("{\"X-Test\":1}")]
    [InlineData("{\"X-Test\":\"a\",\"x-test\":\"b\"}")]
    [InlineData("[]")]
    public void RejectsUnsafeHeadersBeforeSending(string headers)
    {
        var options = new AdvancedRequestOptions(headers, null);
        Assert.Throws<ArgumentException>(() => options.Validate(ApiProtocol.OpenAiChatCompletions));
        Assert.Throws<ArgumentException>(() => TranslationProviderFactory.Create(Profile(ApiProtocol.OpenAiChatCompletions), "key", new HttpClient(new CaptureHandler(ApiProtocol.OpenAiChatCompletions)), options));
    }

    [Theory]
    [InlineData(ApiProtocol.OpenAiChatCompletions, "{\"messages\":[]}")]
    [InlineData(ApiProtocol.OpenAiChatCompletions, "{\"stream\":true}")]
    [InlineData(ApiProtocol.OpenAiResponses, "{\"instructions\":\"override\"}")]
    [InlineData(ApiProtocol.OpenAiResponses, "{\"store\":true}")]
    [InlineData(ApiProtocol.OpenAiResponses, "{\"tools\":[]}")]
    [InlineData(ApiProtocol.AnthropicMessages, "{\"system\":\"override\"}")]
    [InlineData(ApiProtocol.AnthropicMessages, "{\"max_tokens\":null}")]
    [InlineData(ApiProtocol.GeminiGenerateContent, "{\"contents\":[]}")]
    [InlineData(ApiProtocol.GeminiGenerateContent, "{\"generationConfig\":{\"responseMimeType\":\"application/json\"}}")]
    [InlineData(ApiProtocol.GeminiGenerateContent, "{\"generation_config\":{\"candidate_count\":2}}")]
    [InlineData(ApiProtocol.GeminiGenerateContent, "{\"generationConfig\":null}")]
    [InlineData(ApiProtocol.OpenAiChatCompletions, "{\"temperature\":1,\"Temperature\":2}")]
    [InlineData(ApiProtocol.OpenAiChatCompletions, "null")]
    public void RejectsProtectedOrMalformedBodies(ApiProtocol protocol, string body)
    {
        var options = new AdvancedRequestOptions(null, body);
        Assert.Throws<ArgumentException>(() => options.Validate(protocol));
    }

    [Fact]
    public void ValidationErrorsDoNotContainInput()
    {
        var exception = Assert.Throws<ArgumentException>(() => new AdvancedRequestOptions(null, "{\"private-value\":").Validate(ApiProtocol.OpenAiResponses));
        Assert.DoesNotContain("private-value", exception.ToString());
        Assert.DoesNotContain("private-value", new AdvancedRequestOptions("private-value", "private-value").ToString());
    }

    private static ProviderProfile Profile(ApiProtocol protocol) => new("id", "test", protocol, "https://example.test/prefix", "test-model", "ref", 30);

    private sealed class CaptureHandler(ApiProtocol protocol) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? Header { get; private set; }
        public int Count { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Header = request.Headers.GetValues("X-Custom-Feature").Single();
            var json = protocol switch
            {
                ApiProtocol.OpenAiChatCompletions => """{"id":"chat-1","object":"chat.completion","created":1,"model":"test-model","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"translated"}}]}""",
                ApiProtocol.OpenAiResponses => """{"id":"resp-1","object":"response","created_at":1,"model":"test-model","status":"completed","output":[{"type":"message","id":"m1","role":"assistant","status":"completed","content":[{"type":"output_text","text":"translated","annotations":[]}]}]}""",
                ApiProtocol.AnthropicMessages => """{"id":"msg-1","type":"message","role":"assistant","model":"test-model","stop_reason":"end_turn","content":[{"type":"text","text":"translated"}],"usage":{"input_tokens":1,"output_tokens":1}}""",
                _ => """{"candidates":[{"content":{"role":"model","parts":[{"text":"translated"}]},"finishReason":"STOP","index":0}]}"""
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
