using PopoTranslation.Core;

namespace PopoTranslation.Providers;

internal static class ProviderEndpoint
{
    public static Uri Parse(ApiProtocol protocol, string? baseUrl)
    {
        var defaultUrl = protocol switch
        {
            ApiProtocol.OpenAiChatCompletions or ApiProtocol.OpenAiResponses => "https://api.openai.com/v1",
            ApiProtocol.AnthropicMessages => "https://api.anthropic.com",
            ApiProtocol.GeminiGenerateContent => "https://generativelanguage.googleapis.com",
            ApiProtocol.AlibabaTranslate => "https://mt.cn-hangzhou.aliyuncs.com",
            ApiProtocol.BaiduTranslate => "https://fanyi-api.baidu.com",
            ApiProtocol.YoudaoTranslate => "https://openapi.youdao.com",
            ApiProtocol.DeepLTranslate => "https://api-free.deepl.com",
            _ => throw new ArgumentException("Unsupported protocol.", nameof(protocol))
        };
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = defaultUrl;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Base URL must be HTTPS, or HTTP on loopback, without credentials, query or fragment.", nameof(baseUrl));

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(":generateContent", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Base URL must be an API base path, not an operation URL.", nameof(baseUrl));

        if ((protocol == ApiProtocol.OpenAiChatCompletions || protocol == ApiProtocol.OpenAiResponses) && string.IsNullOrEmpty(path))
            path = "/v1";
        if (protocol == ApiProtocol.AnthropicMessages && path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            path = path[..^3];
        if (protocol == ApiProtocol.GeminiGenerateContent && path.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase))
            path = path[..^7];
        else if (protocol == ApiProtocol.GeminiGenerateContent && path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Gemini requires the v1beta API base path.", nameof(baseUrl));
        return new UriBuilder(uri) { Path = path.TrimEnd('/') + "/" }.Uri;
    }
}
