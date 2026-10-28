using System.Text.Json.Serialization;

namespace PopoTranslation.Core;

public sealed record ProviderProfile(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Name,
    [property: JsonRequired] ApiProtocol Protocol,
    [property: JsonRequired] string BaseUrl,
    [property: JsonRequired] string ModelId,
    [property: JsonRequired] string CredentialReference,
    [property: JsonRequired] int TimeoutSeconds);
