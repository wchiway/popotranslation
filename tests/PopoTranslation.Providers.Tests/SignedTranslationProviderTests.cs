using System.Net;
using System.Security.Cryptography;
using System.Text;
using PopoTranslation.Core;
using PopoTranslation.Providers.Baidu;
using PopoTranslation.Providers.Youdao;

namespace PopoTranslation.Providers.Tests;

public sealed class SignedTranslationProviderTests
{
    private const string Id = "test-id";
    private const string Secret = "test-secret";
    private static ProviderProfile Profile => new("id", "name", default, "", "", "", 10);
    private static readonly Uri Endpoint = new("https://example.test/operation");

    [Fact]
    public async Task BaiduSignsRawTextAndReconstructsVerifiedSegments()
    {
        const string source = "Hello & world\r\n\r\n第二行\n";
        var handler = new FakeHandler("""{"trans_result":[{"src":"Hello & world","dst":"你好"},{"src":"第二行","dst":"second line"}]}""");
        var result = await new BaiduTranslationProvider(Profile, Id, Secret, Endpoint, new HttpClient(handler))
            .TranslateAsync(new(source, "English", null, null), TestContext.Current.CancellationToken);
        Assert.Equal(TranslationStatus.Completed, result.Status);
        Assert.Equal("你好\r\n\r\nsecond line\n", result.Text);
        Assert.Equal("POST", handler.Method);
        Assert.Equal(Endpoint, handler.Uri);
        Assert.Equal(source, handler.Form["q"]);
        Assert.Equal("auto", handler.Form["from"]);
        Assert.Equal("en", handler.Form["to"]);
        Assert.Equal(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(Id + source + handler.Form["salt"] + Secret))), handler.Form["sign"]);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData("short😀")]
    [InlineData("1234567890abcdefghijklmnopqrstuvwxyz😀")]
    public async Task YoudaoSignsUnencodedTextAndPostsOnce(string source)
    {
        var handler = new FakeHandler("""{"errorCode":"0","translation":["译文"]}""");
        var result = await new YoudaoTranslationProvider(Profile, Id, Secret, Endpoint, new HttpClient(handler))
            .TranslateAsync(new(source, "简体中文", "English", null), TestContext.Current.CancellationToken);
        Assert.Equal(TranslationStatus.Completed, result.Status);
        var input = source.Length <= 20 ? source : source[..10] + source.Length + source[^10..];
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Id + input + handler.Form["salt"] + handler.Form["curtime"] + Secret))), handler.Form["sign"]);
        Assert.Equal(source, handler.Form["q"]);
        Assert.Equal("zh-CHS", handler.Form["to"]);
        Assert.Equal("en", handler.Form["from"]);
        Assert.Equal("true", handler.Form["strict"]);
        Assert.Equal("v3", handler.Form["signType"]);
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData(false, """{"error_code":"54001","error_msg":"private-text"}""")]
    [InlineData(false, """{"trans_result":[{"src":"wrong","dst":"partial"}]}""")]
    [InlineData(true, """{"errorCode":"202","message":"private-text"}""")]
    [InlineData(true, """{"errorCode":"0","translation":["first","second"]}""")]
    [InlineData(true, """{"errorCode":"0","translation":["one line"]}""")]
    public async Task RejectsErrorsAndUnverifiableResults(bool youdao, string json)
    {
        var handler = new FakeHandler(json);
        var result = await Create(youdao, handler).TranslateAsync(new("first\nsecond", "English", null, null), TestContext.Current.CancellationToken);
        Assert.Equal(TranslationStatus.Failed, result.Status);
        Assert.Null(result.Text);
        Assert.DoesNotContain("private-text", result.ErrorMessage!);
    }

    [Theory]
    [InlineData("繁體中文", "cht", "zh-CHT")]
    [InlineData("한국어", "kor", "ko")]
    [InlineData("Français", "fra", "fr")]
    [InlineData("Deutsch", "de", "de")]
    [InlineData("Español", "spa", "es")]
    [InlineData("Português", "pt", "pt")]
    [InlineData("Русский", "ru", "ru")]
    public async Task MapsLanguageAliases(string language, string baidu, string youdao)
    {
        foreach (var (isYoudao, code) in new[] { (false, baidu), (true, youdao) })
        {
            var handler = new FakeHandler(isYoudao
                ? """{"errorCode":"0","translation":["result"]}"""
                : """{"trans_result":[{"src":"text","dst":"result"}]}""");
            var result = await Create(isYoudao, handler).TranslateAsync(new("text", language, null, null), TestContext.Current.CancellationToken);
            Assert.Equal(TranslationStatus.Completed, result.Status);
            Assert.Equal(code, handler.Form["to"]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectsUnsupportedLanguageWithoutSending(bool youdao)
    {
        var handler = new FakeHandler("{}");
        var result = await Create(youdao, handler).TranslateAsync(new("text", "not a language", null, null), TestContext.Current.CancellationToken);
        Assert.Equal("unsupported_language", result.ErrorCode);
        Assert.Equal(0, handler.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledAndHttpErrorAreSafe(bool youdao)
    {
        var handler = new FakeHandler("private-text", HttpStatusCode.ServiceUnavailable);
        var provider = Create(youdao, handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(TranslationStatus.Cancelled, (await provider.TranslateAsync(new("text", "English", null, null), cts.Token)).Status);
        Assert.Equal(0, handler.Count);
        var result = await provider.TranslateAsync(new("text", "English", null, null), TestContext.Current.CancellationToken);
        Assert.Equal("http_503", result.ErrorCode);
        Assert.DoesNotContain("private-text", result.ErrorMessage!);
        Assert.Equal(1, handler.Count);
    }

    private static ITranslationProvider Create(bool youdao, FakeHandler handler) => youdao
        ? new YoudaoTranslationProvider(Profile, Id, Secret, Endpoint, new HttpClient(handler))
        : new BaiduTranslationProvider(Profile, Id, Secret, Endpoint, new HttpClient(handler));

    private sealed class FakeHandler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Count { get; private set; }
        public string? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public Dictionary<string, string> Form { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            Method = request.Method.Method;
            Uri = request.RequestUri;
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Form = body.Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(pair => Decode(pair[0]), pair => Decode(pair[1]));
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private static string Decode(string value) => System.Uri.UnescapeDataString(value.Replace('+', ' '));
    }
}
