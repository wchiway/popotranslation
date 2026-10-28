using System.Text.Json.Serialization;

namespace PopoTranslation.Core;

public sealed record ProviderProfile(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Name,
    [property: JsonRequired] ApiProtocol Protocol,
    [property: JsonRequired] string BaseUrl,
    [property: JsonRequired] string ModelId,
    [property: JsonRequired] string CredentialReference,
    [property: JsonRequired] int TimeoutSeconds,
    string? SecondaryCredentialReference = null);

public static class ProviderCredentialPolicy
{
    public const string QwenDefaultBaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1";

    public static bool CanReuse(ApiProtocol protocol, string? oldBaseUrl, string? newBaseUrl)
    {
        if (protocol != ApiProtocol.QwenMt) return true;
        return TryNormalize(oldBaseUrl, out var oldUrl) && TryNormalize(newBaseUrl, out var newUrl) && oldUrl == newUrl;
    }

    private static bool TryNormalize(string? value, out string normalized)
    {
        normalized = "";
        value = string.IsNullOrWhiteSpace(value) ? QwenDefaultBaseUrl : value.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        normalized = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
        return true;
    }
}
