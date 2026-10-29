using PopoTranslation.Core;
using PopoTranslation.Infrastructure.Settings;

namespace PopoTranslation.Infrastructure.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PopoTranslation.Tests", Guid.NewGuid().ToString("N"));
    private string PathName => Path.Combine(_directory, "settings.json");
    private static readonly AppSettings Settings = new(1,
        [new("id", "name", ApiProtocol.OpenAiResponses, "https://example.com/v1", "model", "credential-id", 30)],
        "id", "中文", null, null);

    [Fact]
    public async Task SavesReferenceAndRetainsPreviousValidVersion()
    {
        var store = new JsonSettingsStore(PathName);
        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
        await store.SaveAsync(Settings, TestContext.Current.CancellationToken);
        await store.SaveAsync(Settings with { TargetLanguage = "English" }, TestContext.Current.CancellationToken);
        Assert.Equal("English", (await store.LoadAsync(TestContext.Current.CancellationToken))!.TargetLanguage);
        Assert.Equal("中文", (await new JsonSettingsStore(PathName + ".bak").LoadAsync(TestContext.Current.CancellationToken))!.TargetLanguage);
        string json = await File.ReadAllTextAsync(PathName, TestContext.Current.CancellationToken);
        Assert.Contains("credential-id", json);
        Assert.DoesNotContain("secret-value", json);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"SchemaVersion\":1,\"Providers\":[{\"Id\":\"id\",\"Name\":\"n\",\"Protocol\":\"Unknown\",\"BaseUrl\":\"https://example.com\",\"ModelId\":\"m\",\"CredentialReference\":\"ref\",\"TimeoutSeconds\":5}],\"TargetLanguage\":\"x\"}")]
    [InlineData("{\"SchemaVersion\":1,\"Providers\":[{\"Id\":\"id\",\"Name\":\"n\",\"BaseUrl\":\"https://example.com\",\"ModelId\":\"m\",\"CredentialReference\":\"ref\",\"TimeoutSeconds\":5}],\"TargetLanguage\":\"x\"}")]
    [InlineData("{\"SchemaVersion\":2,\"Providers\":[],\"TargetLanguage\":\"x\"}")]
    public async Task InvalidFileFailsWithoutDestroyingIt(string json)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(PathName, json, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => new JsonSettingsStore(PathName).LoadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(json, await File.ReadAllTextAsync(PathName, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveDoesNotReplaceRecoverableBackupWhenCurrentFileIsCorrupt()
    {
        var store = new JsonSettingsStore(PathName);
        await store.SaveAsync(Settings, TestContext.Current.CancellationToken);
        await store.SaveAsync(Settings with { TargetLanguage = "English" }, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(PathName, "broken", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(Settings, TestContext.Current.CancellationToken));
        Assert.Equal("broken", await File.ReadAllTextAsync(PathName, TestContext.Current.CancellationToken));
        Assert.Equal("中文", (await new JsonSettingsStore(PathName + ".bak").LoadAsync(TestContext.Current.CancellationToken))!.TargetLanguage);
    }

    [Fact]
    public async Task HotkeysRoundTripAndOldSettingsRemainReadable()
    {
        var store = new JsonSettingsStore(PathName);
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(PathName, "{\"SchemaVersion\":1,\"Providers\":[],\"TargetLanguage\":\"中文\"}", TestContext.Current.CancellationToken);
        var oldSettings = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Null(oldSettings!.SelectionHotkey);
        Assert.Null(oldSettings.WholeControlHotkey);
        await store.SaveAsync(oldSettings with { SelectionHotkey = "Ctrl+Alt+T", WholeControlHotkey = "Ctrl+Alt+Y" }, TestContext.Current.CancellationToken);
        var saved = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Ctrl+Alt+T", saved!.SelectionHotkey);
        Assert.Equal("Ctrl+Alt+Y", saved.WholeControlHotkey);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(saved with { WholeControlHotkey = "ctrl+alt+t" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MachineProfilesRoundTripReferencesWithoutRequiringModel()
    {
        var store = new JsonSettingsStore(PathName);
        var machine = Settings with
        {
            Providers = [new("id", "Baidu", ApiProtocol.BaiduTranslate, "https://fanyi-api.baidu.com", "", "primary-ref", 30, "secondary-ref")]
        };
        await store.SaveAsync(machine, TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("secondary-ref", loaded!.Providers[0].SecondaryCredentialReference);
        Assert.Equal("", loaded.Providers[0].ModelId);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(machine with
        {
            Providers = [machine.Providers[0] with { SecondaryCredentialReference = null }]
        }, TestContext.Current.CancellationToken));
        var deepl = machine with { Providers = [machine.Providers[0] with { Protocol = ApiProtocol.DeepLTranslate, SecondaryCredentialReference = null }] };
        await store.SaveAsync(deepl, TestContext.Current.CancellationToken);
        Assert.Null((await store.LoadAsync(TestContext.Current.CancellationToken))!.Providers[0].SecondaryCredentialReference);
    }

    [Fact]
    public async Task QwenMtProfileRoundTripsWithOneCredentialAndRequiresModel()
    {
        var store = new JsonSettingsStore(PathName);
        var qwen = Settings with
        {
            Providers = [new("id", "Qwen-MT", ApiProtocol.QwenMt, "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "qwen-mt-flash", "key-ref", 30)]
        };
        await store.SaveAsync(qwen, TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(qwen.Providers[0], loaded!.Providers[0]);
        Assert.Null(loaded.Providers[0].SecondaryCredentialReference);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(qwen with
        {
            Providers = [qwen.Providers[0] with { ModelId = "" }]
        }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OldProfileWithoutSecondaryReferenceRemainsReadable()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(PathName, "{\"SchemaVersion\":1,\"Providers\":[{\"Id\":\"id\",\"Name\":\"name\",\"Protocol\":\"OpenAiResponses\",\"BaseUrl\":\"https://example.com/v1\",\"ModelId\":\"model\",\"CredentialReference\":\"old-ref\",\"TimeoutSeconds\":30}],\"ActiveProviderId\":\"id\",\"TargetLanguage\":\"中文\"}", TestContext.Current.CancellationToken);
        var loaded = await new JsonSettingsStore(PathName).LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("old-ref", loaded!.Providers[0].CredentialReference);
        Assert.Null(loaded.Providers[0].SecondaryCredentialReference);
        Assert.Null(loaded.Providers[0].AdvancedConfigurationReference);
    }

    [Fact]
    public async Task AdvancedConfigurationReferenceRoundTripsAndRejectsEmptyReference()
    {
        var store = new JsonSettingsStore(PathName);
        var settings = Settings with
        {
            Providers = [Settings.Providers[0] with { AdvancedConfigurationReference = "advanced-ref" }]
        };
        await store.SaveAsync(settings, TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("advanced-ref", loaded!.Providers[0].AdvancedConfigurationReference);
        Assert.Contains("advanced-ref", await File.ReadAllTextAsync(PathName, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(settings with
        {
            Providers = [settings.Providers[0] with { AdvancedConfigurationReference = " " }]
        }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelledSavePreservesExistingFile()
    {
        var store = new JsonSettingsStore(PathName);
        await store.SaveAsync(Settings, TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(Settings with { TargetLanguage = "new" }, cancellation.Token));
        Assert.Equal("中文", (await store.LoadAsync(TestContext.Current.CancellationToken))!.TargetLanguage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
