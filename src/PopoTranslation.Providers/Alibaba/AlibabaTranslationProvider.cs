using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PopoTranslation.Core;

namespace PopoTranslation.Providers.Alibaba;

public sealed class AlibabaTranslationProvider : MachineTranslationProviderBase
{
    private readonly string accessKeyId;
    private readonly string accessKeySecret;

    public AlibabaTranslationProvider(ProviderProfile profile, string credentialIdOrKey, string? secret, Uri endpoint, HttpClient httpClient)
        : base(profile, endpoint, httpClient)
    {
        if (string.IsNullOrWhiteSpace(credentialIdOrKey)) throw new ArgumentException("AccessKey ID is required.", nameof(credentialIdOrKey));
        if (string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("AccessKey secret is required.", nameof(secret));
        if (endpoint.AbsolutePath != "/") throw new ArgumentException("Alibaba RPC endpoint must use the root path.", nameof(endpoint));
        accessKeyId = credentialIdOrKey;
        accessKeySecret = secret;
    }

    protected override async Task<TranslationResult> TranslateCoreAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["AccessKeyId"] = accessKeyId, ["Action"] = "TranslateGeneral", ["Version"] = "2018-10-12",
            ["Format"] = "JSON", ["SignatureMethod"] = "HMAC-SHA1", ["SignatureVersion"] = "1.0",
            ["SignatureNonce"] = Guid.NewGuid().ToString("D"), ["Timestamp"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["FormatType"] = "text", ["Scene"] = "general", ["SourceText"] = request.SourceText,
            ["SourceLanguage"] = string.IsNullOrWhiteSpace(request.SourceLanguage) ? "auto" : Language(request.SourceLanguage, false, true),
            ["TargetLanguage"] = Language(request.TargetLanguage, false, false)
        };
        var canonical = string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        var stringToSign = "POST&%2F&" + Uri.EscapeDataString(canonical);
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(accessKeySecret + "&"));
        parameters["Signature"] = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(stringToSign)));
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new FormUrlEncodedContent(parameters) };
        using var response = await HttpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return HttpError(response);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("Code", out var code) || !code.TryGetInt32(out var status) || status != 200)
            return Failed("provider_error", "The provider did not complete the translation.");
        if (!root.TryGetProperty("Data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("Translated", out var translated) || translated.ValueKind != JsonValueKind.String)
            return Failed("invalid_response", "The provider returned an invalid response.");
        return TextResult(translated.GetString());
    }
}
