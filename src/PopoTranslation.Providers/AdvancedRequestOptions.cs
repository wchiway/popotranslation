using PopoTranslation.Core;

namespace PopoTranslation.Providers;

/// <summary>Runtime-only request customization. Callers are responsible for protecting these values at rest.</summary>
public sealed record AdvancedRequestOptions(string? HeadersJson = null, string? BodyJson = null)
{
    /// <summary>Validates the configuration without transmitting it. Errors never include configuration values.</summary>
    public void Validate(ApiProtocol protocol) => AdvancedRequestTransport.Validate(protocol, this);

    public override string ToString() => nameof(AdvancedRequestOptions);
}
