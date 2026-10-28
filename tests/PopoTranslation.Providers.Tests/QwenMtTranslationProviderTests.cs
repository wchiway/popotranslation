using System.Net;
using System.Text;
using System.Text.Json;
using PopoTranslation.Core;
using PopoTranslation.Providers.Alibaba;

namespace PopoTranslation.Providers.Tests;

public sealed class QwenMtTranslationProviderTests
{
    private static readonly ProviderProfile Profile = new("id", "Qwen", ApiProtocol.OpenAiChatCompletions, "", "qwen-mt-flash", "", 10);
    private const string Source = "第一行\r\n\r\nsecond 😀\n";
    private static readonly TranslationRequest Request = new(Source, "繁體中文", null, null);
    private const string Success = "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"  第一行\\r\\n\\r\\nsecond 😀\\n\",\"refusal\":null,\"tool_calls\":null}}]}";

    [Fact]
    public async Task SendsOneUserMessageWithWholeTextAndTranslationOptions()
    {
        var handler = new Handler(Success);
        var result = await Create(handler).TranslateAsync(Request, CancellationToken.None);
        Assert.Equal(TranslationStatus.Completed, result.Status);
        Assert.Equal("  第一行\r\n\r\nsecond 😀\n", result.Text);
        Assert.Equal(1, handler.Count);
        Assert.Equal("https://workspace.cn-beijing.maas.aliyuncs.com/compatible-mode/v1/chat/completions", handler.Uri!.ToString());
        Assert.Equal("Bearer test-key", handler.Authorization);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("qwen-mt-flash", body.RootElement.GetProperty("model").GetString());
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal(Source, messages[0].GetProperty("content").GetString());
        var options = body.RootElement.GetProperty("translation_options");
        Assert.Equal("auto", options.GetProperty("source_lang").GetString());
        Assert.Equal("zh_tw", options.GetProperty("target_lang").GetString());
    }

    [Fact]
    public async Task FactoryCreatesQwenProviderAndUsesConfiguredEndpoint()
    {
        var handler = new Handler(Success);
        var profile = Profile with { Protocol = ApiProtocol.QwenMt, BaseUrl = "https://workspace.cn-beijing.maas.aliyuncs.com/compatible-mode/v1" };
        var provider = TranslationProviderFactory.Create(profile, "test-key", new HttpClient(handler));
        Assert.IsType<QwenMtTranslationProvider>(provider);
        Assert.Equal(TranslationStatus.Completed, (await provider.TranslateAsync(Request, CancellationToken.None)).Status);
        Assert.Equal("/compatible-mode/v1/chat/completions", handler.Uri!.AbsolutePath);
    }

    [Theory]
    [InlineData("Chinese", "English", "zh", "en")]
    [InlineData("zh-TW", "日本語", "zh_tw", "ja")]
    [InlineData("auto", "French", "auto", "fr")]
    public async Task ConvertsLanguages(string source, string target, string expectedSource, string expectedTarget)
    {
        var handler = new Handler(Success);
        await Create(handler).TranslateAsync(Request with { SourceLanguage = source, TargetLanguage = target }, CancellationToken.None);
        using var json = JsonDocument.Parse(handler.Body!);
        var options = json.RootElement.GetProperty("translation_options");
        Assert.Equal(expectedSource, options.GetProperty("source_lang").GetString());
        Assert.Equal(expectedTarget, options.GetProperty("target_lang").GetString());
    }

    [Theory]
    [InlineData("length", TranslationStatus.Truncated)]
    [InlineData("content_filter", TranslationStatus.Refused)]
    [InlineData("tool_calls", TranslationStatus.Failed)]
    public async Task RejectsIncompleteResponses(string reason, TranslationStatus expected)
    {
        var response = Success.Replace("\"stop\"", JsonSerializer.Serialize(reason));
        var result = await Create(new Handler(response)).TranslateAsync(Request, CancellationToken.None);
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Text);
    }

    [Theory]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"x\"}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"x\",\"refusal\":\"no\"}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"x\",\"tool_calls\":[{}]}}]}")]
    [InlineData("not json")]
    public async Task RejectsInvalidOrRefusedResponse(string response)
    {
        var result = await Create(new Handler(response)).TranslateAsync(Request, CancellationToken.None);
        Assert.NotEqual(TranslationStatus.Completed, result.Status);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task RejectsChangedLineBreakCount()
    {
        var result = await Create(new Handler(Success.Replace("\\r\\n\\r\\n", "\\n"))).TranslateAsync(Request, CancellationToken.None);
        Assert.Equal("line_break_mismatch", result.ErrorCode);
        Assert.Null(result.Text);
    }

    [Theory]
    [InlineData("\nfirst\nsecond\n")]
    [InlineData("first\nsecond\n\n")]
    [InlineData("first\n\nsecond\nextra")]
    public async Task RejectsShiftedEmptyLinesAndTrailingContent(string translation)
    {
        var response = JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = translation } } } });
        var result = await Create(new Handler(response)).TranslateAsync(Request, CancellationToken.None);
        Assert.Equal("line_break_mismatch", result.ErrorCode);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task HttpErrorsDoNotRetryOrLeakBody()
    {
        var handler = new Handler("sensitive response", HttpStatusCode.Unauthorized);
        var result = await Create(handler).TranslateAsync(Request, CancellationToken.None);
        Assert.Equal("http_401", result.ErrorCode);
        Assert.DoesNotContain("sensitive response", result.ErrorMessage!);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task UnsupportedLanguageAndStyleAreRejectedBeforeSending()
    {
        var handler = new Handler(Success);
        var provider = Create(handler, Profile with { ModelId = "qwen-mt-lite" });
        Assert.Equal("unsupported_language", (await provider.TranslateAsync(Request with { TargetLanguage = "yue" }, CancellationToken.None)).ErrorCode);
        Assert.Equal("unsupported_language", (await provider.TranslateAsync(Request with { TargetLanguage = "auto" }, CancellationToken.None)).ErrorCode);
        Assert.Equal("unsupported_style", (await provider.TranslateAsync(Request with { Style = "formal" }, CancellationToken.None)).ErrorCode);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public async Task CancellationStopsTransport()
    {
        var handler = new BlockingHandler();
        using var cancellation = new CancellationTokenSource();
        var task = Create(handler).TranslateAsync(Request, cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.Equal(TranslationStatus.Cancelled, (await task).Status);
        Assert.True(handler.Cancelled);
    }

    [Fact]
    public async Task DeadlineStopsTransport()
    {
        var handler = new BlockingHandler();
        var result = await Create(handler, Profile with { TimeoutSeconds = 1 }).TranslateAsync(Request, CancellationToken.None);
        Assert.Equal(TranslationStatus.TimedOut, result.Status);
        Assert.True(handler.Cancelled);
    }

    [Fact]
    public async Task EmptyTranslationIsRejected()
    {
        var result = await Create(new Handler(Success.Replace("  第一行\\r\\n\\r\\nsecond 😀\\n", " "))).TranslateAsync(Request, CancellationToken.None);
        Assert.Equal(TranslationStatus.Empty, result.Status);
        Assert.Null(result.Text);
    }

    [Fact]
    public void ValidatesConstructor()
    {
        using var client = new HttpClient(new Handler(Success));
        Assert.Throws<ArgumentException>(() => new QwenMtTranslationProvider(Profile, "", new Uri("https://example.test/compatible-mode/v1"), client));
        Assert.Throws<ArgumentException>(() => new QwenMtTranslationProvider(Profile with { ModelId = "" }, "key", new Uri("https://example.test/compatible-mode/v1"), client));
        Assert.Throws<ArgumentException>(() => new QwenMtTranslationProvider(Profile, "key", new Uri("http://example.test/compatible-mode/v1"), client));
        Assert.Throws<ArgumentException>(() => new QwenMtTranslationProvider(Profile, "key", new Uri("https://example.test/compatible-mode/v1/chat/completions"), client));
    }

    private static QwenMtTranslationProvider Create(HttpMessageHandler handler, ProviderProfile? profile = null) =>
        new(profile ?? Profile, "test-key", new Uri("https://workspace.cn-beijing.maas.aliyuncs.com/compatible-mode/v1"), new HttpClient(handler));

    private sealed class Handler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Count { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            throw new InvalidOperationException();
        }
    }
}
