using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PopoTranslation.Core;
using PopoTranslation.Providers.Alibaba;
using PopoTranslation.Providers.DeepL;

namespace PopoTranslation.Providers.Tests;

public sealed class MachineTranslationProviderTests
{
    private const string Source = "第一行 & + 😀\r\n\r\nsecond\n";
    private static readonly ProviderProfile Profile = new("id", "test", ApiProtocol.OpenAiResponses, "", "", "", 10);
    private static readonly TranslationRequest Request = new(Source, "繁體中文", null, null);

    [Fact]
    public async Task AlibabaSignsAllParametersAndSendsWholeMultilineTextOnce()
    {
        var handler = new CaptureHandler("""{"Code":200,"Data":{"Translated":"  譯文\r\n\r\n末行\n"}}""");
        var provider = Alibaba(handler);
        var result = await provider.TranslateAsync(Request with { TargetLanguage = "English" }, CancellationToken.None);
        Assert.Equal(TranslationStatus.Completed, result.Status);
        Assert.Equal("  譯文\r\n\r\n末行\n", result.Text);
        Assert.Equal(1, handler.Count);
        Assert.Equal("https://mt.example.test/", handler.Uri!.ToString());
        var parameters = ParseForm(handler.Body!);
        Assert.Equal(Source, parameters["SourceText"]);
        Assert.Equal("auto", parameters["SourceLanguage"]);
        Assert.Equal("en", parameters["TargetLanguage"]);
        Assert.Equal("general", parameters["Scene"]);
        Assert.Equal("text", parameters["FormatType"]);
        Assert.Equal("TranslateGeneral", parameters["Action"]);
        Assert.Equal("2018-10-12", parameters["Version"]);
        var signature = parameters["Signature"];
        parameters.Remove("Signature");
        var canonical = string.Join("&", parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes("test-secret&"));
        Assert.Equal(Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes("POST&%2F&" + Uri.EscapeDataString(canonical)))), signature);
        Assert.DoesNotContain("test-secret", handler.Body!);
        Assert.Empty(handler.Uri.Query);
    }

    [Theory]
    [InlineData("https://api-free.deepl.com", "/v2/translate")]
    [InlineData("https://api.deepl.com/v2", "/v2/translate")]
    [InlineData("https://example.test/prefix", "/prefix/v2/translate")]
    public async Task DeepLUsesAuthHeaderAndSingleMultilineText(string endpoint, string path)
    {
        var handler = new CaptureHandler("""{"translations":[{"text":"  譯文\r\n\r\n末行\n"}]}""");
        var result = await DeepL(handler, endpoint).TranslateAsync(Request, CancellationToken.None);
        Assert.Equal(TranslationStatus.Completed, result.Status);
        Assert.Equal("  譯文\r\n\r\n末行\n", result.Text);
        Assert.Equal(1, handler.Count);
        Assert.Equal(path, handler.Uri!.AbsolutePath);
        Assert.Equal("DeepL-Auth-Key test-key", handler.Authorization);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal(Source, json.RootElement.GetProperty("text")[0].GetString());
        Assert.Equal(1, json.RootElement.GetProperty("text").GetArrayLength());
        Assert.Equal("ZH-HANT", json.RootElement.GetProperty("target_lang").GetString());
        Assert.False(json.RootElement.TryGetProperty("source_lang", out _));
        Assert.Equal("nonewlines", json.RootElement.GetProperty("split_sentences").GetString());
        Assert.True(json.RootElement.GetProperty("preserve_formatting").GetBoolean());
    }

    [Theory]
    [InlineData(false, "first\nsecond", TranslationStatus.Failed)]
    [InlineData(true, "first\nsecond", TranslationStatus.Failed)]
    [InlineData(false, "first\n\nsecond\n", TranslationStatus.Completed)]
    [InlineData(true, "first\n\nsecond\n", TranslationStatus.Completed)]
    [InlineData(false, "first\n\nsecond\nextra\n", TranslationStatus.Failed)]
    [InlineData(true, "first\n\nsecond\nextra\n", TranslationStatus.Failed)]
    public async Task LineBreakCountMustMatch(bool deepL, string translated, TranslationStatus expected)
    {
        var response = deepL
            ? JsonSerializer.Serialize(new { translations = new[] { new { text = translated } } })
            : JsonSerializer.Serialize(new { Code = 200, Data = new { Translated = translated } });
        var handler = new CaptureHandler(response);
        var result = await Create(deepL, handler).TranslateAsync(Request with { TargetLanguage = "English" }, TestContext.Current.CancellationToken);
        Assert.Equal(expected, result.Status);
        Assert.Equal(1, handler.Count);
        if (expected == TranslationStatus.Failed)
        {
            Assert.Null(result.Text);
            Assert.Equal("line_break_mismatch", result.ErrorCode);
            Assert.DoesNotContain(translated, result.ErrorMessage!);
        }
        else Assert.Equal(translated, result.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpFailureDoesNotLeakBodyOrRetry(bool deepL)
    {
        var handler = new CaptureHandler("secret response", HttpStatusCode.Unauthorized);
        var result = await Create(deepL, handler).TranslateAsync(Request with { TargetLanguage = "English" }, CancellationToken.None);
        Assert.Equal(TranslationStatus.Failed, result.Status);
        Assert.Equal("http_401", result.ErrorCode);
        Assert.DoesNotContain("secret response", result.ErrorMessage!);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData(false, "{\"Code\":10007,\"Data\":{\"Translated\":\"bad\"}}", TranslationStatus.Failed)]
    [InlineData(false, "{\"Code\":200,\"Data\":{\"Translated\":\" \"}}", TranslationStatus.Empty)]
    [InlineData(false, "{\"Code\":200}", TranslationStatus.Failed)]
    [InlineData(true, "{\"translations\":[{\"text\":\" \"}]}", TranslationStatus.Empty)]
    [InlineData(true, "{\"translations\":[{\"text\":\"a\"},{\"text\":\"b\"}]}", TranslationStatus.Failed)]
    [InlineData(true, "not json", TranslationStatus.Failed)]
    public async Task RejectsErrorsAndIncompleteResponses(bool deepL, string response, TranslationStatus expected)
    {
        var result = await Create(deepL, new CaptureHandler(response)).TranslateAsync(Request with { TargetLanguage = "English" }, CancellationToken.None);
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationStopsTransport(bool deepL)
    {
        var handler = new BlockingHandler();
        using var cancellation = new CancellationTokenSource();
        var task = Create(deepL, handler).TranslateAsync(Request with { TargetLanguage = "English" }, cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.Equal(TranslationStatus.Cancelled, (await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).Status);
        Assert.True(handler.Cancelled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineStopsTransport(bool deepL)
    {
        var handler = new BlockingHandler();
        var result = await Create(deepL, handler, Profile with { TimeoutSeconds = 1 }).TranslateAsync(Request with { TargetLanguage = "English" }, CancellationToken.None);
        Assert.Equal(TranslationStatus.TimedOut, result.Status);
        Assert.True(handler.Cancelled);
    }

    [Theory]
    [InlineData(false, "繁體中文")]
    [InlineData(false, "zh-tw")]
    [InlineData(false, "unknown")]
    [InlineData(true, "unknown")]
    public async Task UnsupportedTargetDoesNotSend(bool deepL, string language)
    {
        var handler = new CaptureHandler("{}");
        var result = await Create(deepL, handler).TranslateAsync(Request with { TargetLanguage = language }, CancellationToken.None);
        Assert.Equal(TranslationStatus.Failed, result.Status);
        Assert.Equal(0, handler.Count);
    }

    [Fact]
    public void RejectsUnsafeEndpointsAndMissingCredentials()
    {
        var client = new HttpClient(new CaptureHandler(""));
        Assert.Throws<ArgumentException>(() => new AlibabaTranslationProvider(Profile, "id", "secret", new Uri("https://user:secret@example.test"), client));
        Assert.Throws<ArgumentException>(() => new AlibabaTranslationProvider(Profile, "id", null, new Uri("https://example.test"), client));
        Assert.Throws<ArgumentException>(() => new AlibabaTranslationProvider(Profile, "id", "secret", new Uri("https://example.test/path"), client));
        Assert.Throws<ArgumentException>(() => new DeepLTranslationProvider(Profile, "key", null, new Uri("http://example.test"), client));
        Assert.Throws<ArgumentException>(() => new DeepLTranslationProvider(Profile, "key", null, new Uri("https://example.test/v2/translate"), client));
    }

    private static ITranslationProvider Alibaba(HttpMessageHandler handler, ProviderProfile? profile = null) => new AlibabaTranslationProvider(profile ?? Profile, "test-id", "test-secret", new Uri("https://mt.example.test/"), new HttpClient(handler));
    private static ITranslationProvider DeepL(HttpMessageHandler handler, string endpoint = "https://api-free.deepl.com", ProviderProfile? profile = null) => new DeepLTranslationProvider(profile ?? Profile, "test-key", null, new Uri(endpoint), new HttpClient(handler));
    private static ITranslationProvider Create(bool deepL, HttpMessageHandler handler, ProviderProfile? profile = null) => deepL ? DeepL(handler, profile: profile) : Alibaba(handler, profile);
    private static Dictionary<string, string> ParseForm(string body) => body.Split('&').Select(part => part.Split('=', 2)).ToDictionary(part => WebUtility.UrlDecode(part[0]), part => WebUtility.UrlDecode(part[1]));

    private sealed class CaptureHandler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
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
