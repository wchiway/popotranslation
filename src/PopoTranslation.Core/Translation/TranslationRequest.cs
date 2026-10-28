namespace PopoTranslation.Core;

public sealed record TranslationRequest(
    string SourceText,
    string TargetLanguage,
    string? SourceLanguage,
    string? Style);
