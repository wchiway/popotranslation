using System.Text.Json.Serialization;

namespace PopoTranslation.Core;

public sealed record AppSettings(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] IReadOnlyList<ProviderProfile> Providers,
    string? ActiveProviderId,
    [property: JsonRequired] string TargetLanguage,
    string? SourceLanguage,
    string? Style,
    string? SelectionHotkey = null,
    string? WholeControlHotkey = null)
{
    public const int CurrentSchemaVersion = 1;
}
