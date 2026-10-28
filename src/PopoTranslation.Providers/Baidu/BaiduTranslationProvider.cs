using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PopoTranslation.Core;

namespace PopoTranslation.Providers.Baidu;

public sealed class BaiduTranslationProvider(ProviderProfile profile, string credentialId, string secret, Uri endpoint, HttpClient httpClient)
    : SignedTranslationProvider(profile, credentialId, secret, endpoint, httpClient)
{
    protected override string? Language(string language) => CommonLanguage(language, false);

    protected override async Task<TranslationResult> TranslateCoreAsync(string source, string from, string to, CancellationToken cancellationToken)
    {
        var salt = Guid.NewGuid().ToString("N");
        var signature = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(CredentialId + source + salt + Secret)));
        using var document = await PostAsync(new Dictionary<string, string>
        {
            ["q"] = source, ["from"] = from, ["to"] = to, ["appid"] = CredentialId, ["salt"] = salt, ["sign"] = signature
        }, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.TryGetProperty("error_code", out _))
            return Failure("provider_error", "The translation provider rejected the request. Check the service configuration.");
        if (!root.TryGetProperty("trans_result", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            return Failure("incomplete", "The provider returned an incomplete translation.");

        var segments = new List<(string Source, string Destination)>();
        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("src", out var src) || src.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("dst", out var dst) || dst.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(dst.GetString()))
                return Failure("incomplete", "The provider returned an incomplete translation.");
            segments.Add((src.GetString()!, dst.GetString()!));
        }
        if (segments.Count == 1 && segments[0].Source == source)
        {
            if (!SameLineBreaks(source, segments[0].Destination)) return Failure("incomplete", "The provider returned an incomplete translation.");
            return Text(segments[0].Destination);
        }

        var parts = Regex.Split(source, "(\\r\\n|\\r|\\n)");
        var output = new StringBuilder();
        var index = 0;
        foreach (var part in parts)
        {
            if (part is "\r" or "\n" or "\r\n" || part.Length == 0) output.Append(part);
            else if (index < segments.Count && segments[index].Source == part &&
                !segments[index].Destination.Contains('\r') && !segments[index].Destination.Contains('\n'))
                output.Append(segments[index++].Destination);
            else return Failure("incomplete", "The provider returned an incomplete translation.");
        }
        return index == segments.Count ? Text(output.ToString()) : Failure("incomplete", "The provider returned an incomplete translation.");
    }

    private static bool SameLineBreaks(string source, string destination) =>
        Regex.Matches(source, "\\r\\n|\\r|\\n").Count == Regex.Matches(destination, "\\r\\n|\\r|\\n").Count;
}
