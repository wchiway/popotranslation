using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PopoTranslation.Core;

namespace PopoTranslation.Providers.Youdao;

public sealed class YoudaoTranslationProvider(ProviderProfile profile, string credentialId, string secret, Uri endpoint, HttpClient httpClient)
    : SignedTranslationProvider(profile, credentialId, secret, endpoint, httpClient)
{
    protected override string? Language(string language) => CommonLanguage(language, true);

    protected override async Task<TranslationResult> TranslateCoreAsync(string source, string from, string to, CancellationToken cancellationToken)
    {
        var salt = Guid.NewGuid().ToString();
        var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        // The official JavaScript example uses UTF-16 string length and substring.
        var input = source.Length <= 20 ? source : source[..10] + source.Length.ToString(CultureInfo.InvariantCulture) + source[^10..];
        var signature = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CredentialId + input + salt + time + Secret)));
        using var document = await PostAsync(new Dictionary<string, string>
        {
            ["q"] = source, ["from"] = from, ["to"] = to, ["appKey"] = CredentialId,
            ["salt"] = salt, ["sign"] = signature, ["signType"] = "v3", ["curtime"] = time, ["strict"] = "true"
        }, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("errorCode", out var error) || error.ValueKind != JsonValueKind.String)
            return Failure("incomplete", "The provider returned an incomplete translation.");
        if (error.GetString() != "0")
            return Failure("provider_error", "The translation provider rejected the request. Check the service configuration.");
        if (!root.TryGetProperty("translation", out var translations) || translations.ValueKind != JsonValueKind.Array || translations.GetArrayLength() != 1 ||
            translations[0].ValueKind != JsonValueKind.String)
            return Failure("incomplete", "The provider returned an incomplete translation.");
        var text = translations[0].GetString();
        if (text is not null && Regex.Matches(source, "\\r\\n|\\r|\\n").Count != Regex.Matches(text, "\\r\\n|\\r|\\n").Count)
            return Failure("incomplete", "The provider returned an incomplete translation.");
        return Text(text);
    }
}
