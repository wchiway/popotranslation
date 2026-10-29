using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PopoTranslation.Core;

namespace PopoTranslation.Providers;

// Runs immediately before the configured transport, so SDK authentication and response handling remain intact.
internal sealed class AdvancedRequestTransport : HttpMessageHandler
{
    private readonly HttpClient inner;
    private readonly Dictionary<string, string> headers;
    private readonly JsonObject? body;
    private readonly Uri endpoint;

    private AdvancedRequestTransport(HttpClient inner, Uri endpoint, Dictionary<string, string> headers, JsonObject? body)
    {
        this.inner = inner;
        this.endpoint = endpoint;
        this.headers = headers;
        this.body = body;
    }

    public static HttpClient Wrap(HttpClient inner, Uri endpoint, ApiProtocol protocol, AdvancedRequestOptions? options)
    {
        if (options is null || (string.IsNullOrWhiteSpace(options.HeadersJson) && string.IsNullOrWhiteSpace(options.BodyJson))) return inner;
        var headers = ParseHeaders(options.HeadersJson);
        var body = ParseObject(options.BodyJson);
        if (body is not null) ValidateBody(body, protocol);
        return new HttpClient(new AdvancedRequestTransport(inner, endpoint, headers, body)) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static void Validate(ApiProtocol protocol, AdvancedRequestOptions options)
    {
        if (protocol is not (ApiProtocol.OpenAiChatCompletions or ApiProtocol.OpenAiResponses or ApiProtocol.AnthropicMessages or ApiProtocol.GeminiGenerateContent))
            throw new ArgumentException("Advanced request configuration is only available for AI protocols.");
        ParseHeaders(options.HeadersJson);
        var body = ParseObject(options.BodyJson);
        if (body is not null) ValidateBody(body, protocol);
    }

    private static JsonObject? ParseObject(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Advanced configuration must be a JSON object.");
            CheckDuplicates(document.RootElement);
            return JsonNode.Parse(value)!.AsObject();
        }
        catch (JsonException)
        {
            throw new ArgumentException("Advanced configuration must be a valid JSON object.");
        }
    }

    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(Normalize(property.Name))) throw new ArgumentException("Advanced configuration contains duplicate property names.");
                CheckDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckDuplicates(item);
    }

    private static Dictionary<string, string> ParseHeaders(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var obj = ParseObject(json);
        if (obj is null) return result;
        using var request = new HttpRequestMessage();
        foreach (var pair in obj)
        {
            var name = pair.Key;
            var normalized = name.ToLowerInvariant();
            var compact = Normalize(name);
            // Arbitrary routing, authentication, content and hop-by-hop headers cannot be safely overridden.
            if (normalized is "authorization" or "proxy-authorization" or "host" or "cookie" or "set-cookie" or "connection" or "transfer-encoding" or "te" or "trailer" or "upgrade" or "keep-alive" or "expect" or "via" or "forwarded" or "accept" or "accept-encoding" or "user-agent" or "anthropic-version" ||
                normalized.StartsWith("content-") || normalized.StartsWith("proxy-") || normalized.StartsWith("sec-") ||
                normalized.StartsWith("x-forwarded-") || normalized.StartsWith("x-original-") || normalized.StartsWith("x-rewrite-") ||
                normalized.StartsWith("x-envoy-") || normalized.StartsWith("x-http-") || normalized.StartsWith("x-goog-") ||
                normalized.Contains("auth") || normalized.Contains("api-key") || normalized.Contains("apikey") ||
                normalized.Contains("token") || normalized.Contains("credential") || normalized.Contains("secret") ||
                normalized is "x-host" or "x-real-ip" || compact.Contains("auth") || compact.Contains("apikey") ||
                compact.Contains("token") || compact.Contains("credential") || compact.Contains("secret") ||
                compact.StartsWith("xforwarded") || compact.StartsWith("xoriginal") || compact.StartsWith("xrewrite") ||
                compact.StartsWith("xenvoy") || compact.StartsWith("xhttp") || compact.StartsWith("xgoog") ||
                compact.Contains("method") || compact.Contains("override") || compact is "xhost" or "xrealip")
                throw new ArgumentException("Advanced headers contain a protected header.");
            if (pair.Value is not JsonValue item || !item.TryGetValue<string>(out var text) || text.Any(char.IsControl))
                throw new ArgumentException("Advanced header values must be strings without control characters.");
            try { request.Headers.Add(name, text); }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
            { throw new ArgumentException("Advanced headers contain an invalid header."); }
            result.Add(name, text);
        }
        return result;
    }

    private static string Normalize(string name) => name.Replace("_", "").Replace("-", "").ToLowerInvariant();

    private static void ValidateBody(JsonObject body, ApiProtocol protocol)
    {
        var protectedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "model", "messages", "input", "instructions", "system", "systeminstruction", "contents", "stream", "streamoptions",
            "tools", "toolchoice", "toolconfig", "functions", "functioncall", "responseformat", "modalities", "audio", "text",
            "stop", "stopsequences", "n", "candidatecount", "background", "previousresponseid", "conversation", "cachedcontent"
        };
        foreach (var pair in body)
        {
            var name = Normalize(pair.Key);
            if (protectedNames.Contains(name) || (protocol == ApiProtocol.OpenAiResponses && name == "store"))
                throw new ArgumentException("Advanced body contains a protected field.");
            if (protocol == ApiProtocol.AnthropicMessages && name == "maxtokens" &&
                (pair.Value is not JsonValue number || !number.TryGetValue<int>(out var max) || max <= 0))
                throw new ArgumentException("max_tokens must be a positive integer.");
            if (protocol == ApiProtocol.GeminiGenerateContent && name == "generationconfig")
            {
                if (pair.Value is not JsonObject config) throw new ArgumentException("generationConfig must be an object.");
                foreach (var option in config)
                    if (Normalize(option.Key) is "candidatecount" or "stopsequences" or "responsemimetype" or "responseschema" or "responsejsonschema" or "responsemodalities")
                        throw new ArgumentException("Advanced body contains a protected generation field.");
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri || !string.Equals(uri.Scheme, endpoint.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.IdnHost, endpoint.IdnHost, StringComparison.OrdinalIgnoreCase) || uri.Port != endpoint.Port ||
            !uri.AbsolutePath.StartsWith(endpoint.AbsolutePath, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Provider request target does not match its configured endpoint.");
        foreach (var (name, value) in headers)
        {
            if (request.Headers.Contains(name)) throw new InvalidOperationException("Advanced header conflicts with an existing header.");
            request.Headers.Add(name, value);
        }
        if (body is not null)
        {
            var original = request.Content ?? throw new InvalidOperationException("Request body is missing.");
            var text = await original.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var target = JsonNode.Parse(text)?.AsObject() ?? throw new InvalidOperationException("Request body must be an object.");
            Merge(target, body);
            var replacement = new StringContent(target.ToJsonString(), Encoding.UTF8, "application/json");
            foreach (var header in original.Headers)
                if (!header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            request.Content = replacement;
            original.Dispose();
        }
        using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
            Content = request.Content
        };
        foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
        foreach (var option in request.Options) forwarded.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
        try { return await inner.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
        finally { forwarded.Content = null; }
    }

    private static void Merge(JsonObject target, JsonObject additions)
    {
        foreach (var (key, value) in additions)
        {
            // Existing protocol fields use canonical spelling; aliases must not create duplicate semantics.
            var existing = target.Select(p => p.Key).FirstOrDefault(k => Normalize(k) == Normalize(key));
            if (existing is not null && target[existing] is JsonObject targetObject && value is JsonObject sourceObject)
                Merge(targetObject, sourceObject);
            else
                target[existing ?? key] = value?.DeepClone();
        }
    }
}
