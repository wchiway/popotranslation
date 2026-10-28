namespace PopoTranslation.Core;

public enum TranslationStatus
{
    Completed,
    Empty,
    Truncated,
    Refused,
    Failed,
    TimedOut,
    Cancelled
}

public sealed record TranslationResult(
    TranslationStatus Status,
    string? Text,
    string? ErrorCode,
    string? ErrorMessage)
{
    public bool IsSuccessful => Status == TranslationStatus.Completed && !string.IsNullOrWhiteSpace(Text);
}
