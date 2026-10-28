namespace PopoTranslation.Core;

public sealed class TranslationOperation
{
    internal TranslationOperation(TranslationRequest request, ProviderProfile profile, CancellationTokenSource cancellation)
    {
        Request = request;
        Profile = profile;
        Cancellation = cancellation;
    }

    public TranslationRequest Request { get; }
    public ProviderProfile Profile { get; }
    public Task<TranslationResult> Completion { get; internal set; } = null!;
    internal CancellationTokenSource Cancellation { get; }
    internal TranslationResult? Result { get; set; }
    internal bool Applied { get; set; }
}
