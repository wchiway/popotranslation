using PopoTranslation.Core;

namespace PopoTranslation.Core.Tests;

public class CachedTranslationProviderTests
{
    private static readonly TranslationRequest Request = new("source", "中文", null, null);
    private static readonly TranslationCacheContext Context = new(Request, "provider", ApiProtocol.OpenAiResponses,
        "https://example.com", "model", "credential", null, null, null);
    private static readonly ProviderProfile Profile = new("1", "test", ApiProtocol.OpenAiResponses,
        "https://example.com", "model", "key", 10);

    [Fact]
    public async Task HitUsesActualRequestWithoutCallingProviderOrSaving()
    {
        var store = new FakeStore { Read = (_, _) => Task.FromResult<string?>("cached") };
        var inner = new FakeProvider();
        var provider = new CachedTranslationProvider(inner, store, Context);
        var request = Request with { SourceText = "different" };

        var result = await provider.TranslateAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("cached", result.Text);
        Assert.True(result.IsSuccessful);
        Assert.True(provider.CacheHit);
        Assert.Equal(request, store.LastContext!.Request);
        Assert.Equal(0, inner.Calls);
        Assert.Equal(0, store.Saves);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \t")]
    public async Task EmptyCacheFallsBack(string? cached)
    {
        var store = new FakeStore { Read = (_, _) => Task.FromResult(cached) };
        var inner = new FakeProvider();
        var provider = new CachedTranslationProvider(inner, store, Context);

        var result = await provider.TranslateAsync(Request, TestContext.Current.CancellationToken);

        Assert.Equal("provider", result.Text);
        Assert.False(provider.CacheHit);
        Assert.Equal(1, inner.Calls);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task CacheFailureFallsBackToProvider()
    {
        var store = new FakeStore { Read = (_, _) => throw new InvalidOperationException() };
        var inner = new FakeProvider();
        var provider = new CachedTranslationProvider(inner, store, Context);

        Assert.Equal("provider", (await provider.TranslateAsync(Request, TestContext.Current.CancellationToken)).Text);
        Assert.Equal(1, inner.Calls);
        Assert.False(provider.CacheHit);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task CacheCancellationDoesNotFallBack()
    {
        var store = new FakeStore { Read = (_, _) => throw new OperationCanceledException() };
        var inner = new FakeProvider();
        var provider = new CachedTranslationProvider(inner, store, Context);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.TranslateAsync(Request, TestContext.Current.CancellationToken));
        Assert.Equal(0, inner.Calls);
        Assert.False(provider.CacheHit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringIgnoringStoreNeverReturnsOrFallsBack(bool throws)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeStore { Read = async (_, _) =>
        {
            started.SetResult();
            await release.Task;
            if (throws) throw new InvalidOperationException();
            return "cached";
        } };
        var inner = new FakeProvider();
        var provider = new CachedTranslationProvider(inner, store, Context);
        var task = provider.TranslateAsync(Request, cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, inner.Calls);
        Assert.False(provider.CacheHit);
    }

    [Fact]
    public async Task CachedResultStillRequiresCoordinatorValidationAndCanOnlyApplyOnce()
    {
        var store = new FakeStore { Read = (_, _) => Task.FromResult<string?>("cached") };
        var provider = new CachedTranslationProvider(new FakeProvider(), store, Context);
        var coordinator = new TranslationCoordinator();
        var operation = coordinator.Start(provider, Request, Profile, TestContext.Current.CancellationToken);

        Assert.True((await operation.Completion).IsSuccessful);
        Assert.True(provider.CacheHit);
        Assert.False(coordinator.TryApply(operation, _ => false));
        string? applied = null;
        Assert.True(coordinator.TryApply(operation, text => { applied = text; return true; }));
        Assert.Equal("cached", applied);
        Assert.False(coordinator.TryApply(operation, _ => true));
    }

    [Fact]
    public async Task CoordinatorCancellationRejectsLateCachedResult()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeStore { Read = (_, _) => { started.SetResult(); return release.Task; } };
        var inner = new FakeProvider();
        var provider = new CachedTranslationProvider(inner, store, Context);
        var coordinator = new TranslationCoordinator();
        var operation = coordinator.Start(provider, Request, Profile, TestContext.Current.CancellationToken);
        await started.Task;
        coordinator.Cancel();
        Assert.Equal(TranslationStatus.Cancelled, (await operation.Completion).Status);
        release.SetResult("late cached");

        Assert.False(coordinator.TryApply(operation, _ => true));
        Assert.Equal(0, inner.Calls);
    }

    private sealed class FakeProvider : ITranslationProvider
    {
        public int Calls { get; private set; }
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new TranslationResult(TranslationStatus.Completed, "provider", null, null));
        }
    }

    private sealed class FakeStore : ITranslationStore
    {
        public required Func<TranslationCacheContext, CancellationToken, Task<string?>> Read { get; init; }
        public TranslationCacheContext? LastContext { get; private set; }
        public int Saves { get; private set; }
        public Task<string?> FindCachedAsync(TranslationCacheContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return Read(context, cancellationToken);
        }
        public Task SaveSuccessAsync(TranslationCacheContext context, string translatedText, bool cacheHit, bool saveToHistory, bool saveToCache, CancellationToken cancellationToken = default)
        {
            Saves++;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<TranslationHistoryEntry>> GetHistoryAsync(int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearHistoryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ClearCacheAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
