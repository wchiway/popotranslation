using PopoTranslation.Core;

namespace PopoTranslation.Core.Tests;

public class TranslationCoordinatorTests
{
    private static readonly ProviderProfile Profile = new("1", "test", ApiProtocol.OpenAiResponses, "https://example.com", "model", "key", 10);
    private static readonly TranslationRequest Request = new("source", "中文", null, null);

    [Fact]
    public async Task NewOperationSupersedesOldEvenWhenOldProviderIgnoresCancellation()
    {
        var coordinator = new TranslationCoordinator();
        var first = new DeferredProvider();
        var second = new DeferredProvider();
        var a = coordinator.Start(first, Request, Profile, TestContext.Current.CancellationToken);
        await first.Started.Task;
        var b = coordinator.Start(second, Request, Profile, TestContext.Current.CancellationToken);
        await second.Started.Task;
        second.Complete("new");
        Assert.True((await b.Completion).IsSuccessful);
        first.Complete("old");
        Assert.Equal(TranslationStatus.Cancelled, (await a.Completion).Status);
        Assert.False(coordinator.TryApply(a, _ => true));
        string? applied = null;
        Assert.True(coordinator.TryApply(b, text => { applied = text; return true; }));
        Assert.Equal("new", applied);
        Assert.False(coordinator.TryApply(b, _ => true));
    }

    [Fact]
    public async Task CancelInvalidatesLateResultAndSuccessfulResult()
    {
        var coordinator = new TranslationCoordinator();
        var provider = new DeferredProvider();
        var operation = coordinator.Start(provider, Request, Profile, TestContext.Current.CancellationToken);
        await provider.Started.Task;
        coordinator.Cancel();
        provider.Complete("late");
        Assert.Equal(TranslationStatus.Cancelled, (await operation.Completion).Status);
        Assert.False(coordinator.TryApply(operation, _ => true));
    }

    [Fact]
    public async Task TimeoutReturnsEvenWhenProviderIgnoresCancellation()
    {
        var coordinator = new TranslationCoordinator();
        var provider = new DeferredProvider();
        var operation = coordinator.Start(provider, Request, Profile with { TimeoutSeconds = 1 }, TestContext.Current.CancellationToken);
        await provider.Started.Task;
        Assert.Equal(TranslationStatus.TimedOut, (await operation.Completion).Status);
        provider.Complete("late");
        Assert.False(coordinator.TryApply(operation, _ => true));
    }

    [Fact]
    public async Task ExternalCancellationInvalidatesAnAlreadyCompletedResult()
    {
        var coordinator = new TranslationCoordinator();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var provider = new DeferredProvider();
        var operation = coordinator.Start(provider, Request, Profile, cancellation.Token);
        provider.Complete("text");
        Assert.True((await operation.Completion).IsSuccessful);
        cancellation.Cancel();
        Assert.False(coordinator.TryApply(operation, _ => true));
    }

    [Fact]
    public async Task FailedValidationDoesNotApplyAndCanBeRetried()
    {
        var coordinator = new TranslationCoordinator();
        var provider = new DeferredProvider();
        var operation = coordinator.Start(provider, Request, Profile, TestContext.Current.CancellationToken);
        provider.Complete("text");
        await operation.Completion;
        Assert.False(coordinator.TryApply(operation, _ => false));
        Assert.True(coordinator.TryApply(operation, _ => true));
    }

    [Fact]
    public async Task FinalGateRejectsSupersededOperationAndConsumesSuccessfulEntry()
    {
        var coordinator = new TranslationCoordinator();
        var provider = new DeferredProvider();
        var old = coordinator.Start(provider, Request, Profile, TestContext.Current.CancellationToken);
        provider.Complete("old");
        await old.Completion;
        var replacement = new DeferredProvider();
        var current = coordinator.Start(replacement, Request, Profile, TestContext.Current.CancellationToken);
        Assert.False(coordinator.IsCurrent(old));
        bool invoked = false;
        Assert.False(coordinator.TryExecuteIfCurrent(old, () => { invoked = true; return 1; }, out _));
        Assert.False(invoked);
        replacement.Complete("new");
        await current.Completion;
        Assert.True(coordinator.TryExecuteIfCurrent(current, () => 42, out int result));
        Assert.Equal(42, result);
        Assert.False(coordinator.TryExecuteIfCurrent(current, () => 7, out _));
    }

    [Fact]
    public async Task FinalGateConsumesOperationWhenCallbackThrows()
    {
        var coordinator = new TranslationCoordinator();
        var provider = new DeferredProvider();
        var operation = coordinator.Start(provider, Request, Profile, TestContext.Current.CancellationToken);
        provider.Complete("text");
        await operation.Completion;
        Assert.Throws<InvalidOperationException>(() => coordinator.TryExecuteIfCurrent<int>(operation, () => throw new InvalidOperationException(), out _));
        Assert.False(coordinator.IsCurrent(operation));
    }

    private sealed class DeferredProvider : ITranslationProvider
    {
        private readonly TaskCompletionSource<TranslationResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return _result.Task;
        }
        public void Complete(string text) => _result.TrySetResult(new(TranslationStatus.Completed, text, null, null));
    }
}
