using PopoTranslation.Core;
using PopoTranslation.Infrastructure.Settings;

namespace PopoTranslation.Infrastructure.Tests;

public class ApplicationPreferencesStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PopoTranslation.Tests", Guid.NewGuid().ToString("N"));
    private string PreferencesPath => Path.Combine(_directory, "preferences.json");
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public async Task MissingAndPartialPreferencesUseDefaultsWithoutWriting()
    {
        var store = new JsonApplicationPreferencesStore(PreferencesPath);
        Assert.Equal(new ApplicationPreferences(), await store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.False(File.Exists(PreferencesPath));
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(PreferencesPath, "{\"Theme\":\"Dark\"}", TestContext.Current.CancellationToken);
        Assert.Equal(new ApplicationPreferences(Theme: ApplicationTheme.Dark), await store.LoadAsync(TestContext.Current.CancellationToken));
        const string oldJson = "{\"Language\":\"English\",\"Theme\":\"Light\",\"NotificationsEnabled\":false}";
        await File.WriteAllTextAsync(PreferencesPath, oldJson, TestContext.Current.CancellationToken);
        var oldPreferences = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new ApplicationPreferences(InterfaceLanguage.English, ApplicationTheme.Light, false, true), oldPreferences);
        Assert.Equal(oldJson, await File.ReadAllTextAsync(PreferencesPath, TestContext.Current.CancellationToken));
        await store.SaveAsync(oldPreferences with { VerifyReplacement = false }, TestContext.Current.CancellationToken);
        Assert.Equal(oldPreferences with { VerifyReplacement = false }, await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(InterfaceLanguage.SimplifiedChinese, ApplicationTheme.System, true, false)]
    [InlineData(InterfaceLanguage.English, ApplicationTheme.Light, false, true)]
    [InlineData(InterfaceLanguage.English, ApplicationTheme.Dark, true, true)]
    [InlineData(InterfaceLanguage.English, ApplicationTheme.Dark, false, false)]
    public async Task PreferencesRoundTrip(InterfaceLanguage language, ApplicationTheme theme, bool notifications, bool verifyReplacement)
    {
        var store = new JsonApplicationPreferencesStore(PreferencesPath);
        var preferences = new ApplicationPreferences(language, theme, notifications, verifyReplacement);
        await store.SaveAsync(preferences, TestContext.Current.CancellationToken);
        Assert.Equal(preferences, await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OldSettingsAndPreferencesAreIndependent()
    {
        const string oldJson = "{\"SchemaVersion\":1,\"Providers\":[],\"TargetLanguage\":\"中文\"}";
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(SettingsPath, oldJson, TestContext.Current.CancellationToken);
        var settingsStore = new JsonSettingsStore(SettingsPath);
        var preferencesStore = new JsonApplicationPreferencesStore(PreferencesPath);
        var settings = await settingsStore.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new ApplicationPreferences(), await preferencesStore.LoadAsync(TestContext.Current.CancellationToken));
        await preferencesStore.SaveAsync(new(InterfaceLanguage.English, ApplicationTheme.Dark, false), TestContext.Current.CancellationToken);
        Assert.Equal(oldJson, await File.ReadAllTextAsync(SettingsPath, TestContext.Current.CancellationToken));
        await settingsStore.SaveAsync(settings! with { TargetLanguage = "English" }, TestContext.Current.CancellationToken);
        Assert.Equal(new ApplicationPreferences(InterfaceLanguage.English, ApplicationTheme.Dark, false), await preferencesStore.LoadAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => new JsonApplicationPreferencesStore(SettingsPath).SaveAsync(new(), TestContext.Current.CancellationToken));
        Assert.Equal("English", (await settingsStore.LoadAsync(TestContext.Current.CancellationToken))!.TargetLanguage);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Language\":\"French\"}")]
    [InlineData("{\"Theme\":42}")]
    [InlineData("{\"NotificationsEnabled\":null}")]
    [InlineData("{\"VerifyReplacement\":null}")]
    [InlineData("{\"VerifyReplacement\":\"false\"}")]
    [InlineData("{\"FutureSetting\":true}")]
    public async Task InvalidPreferencesCannotBeOverwritten(string json)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(PreferencesPath, json, TestContext.Current.CancellationToken);
        var store = new JsonApplicationPreferencesStore(PreferencesPath);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new(), TestContext.Current.CancellationToken));
        Assert.Equal(json, await File.ReadAllTextAsync(PreferencesPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BackupAndCancelledOrInvalidSavePreserveData()
    {
        var store = new JsonApplicationPreferencesStore(PreferencesPath);
        var original = new ApplicationPreferences(InterfaceLanguage.English, ApplicationTheme.Light, false);
        await store.SaveAsync(original, TestContext.Current.CancellationToken);
        await store.SaveAsync(original with { Theme = ApplicationTheme.Dark }, TestContext.Current.CancellationToken);
        Assert.Equal(original, await new JsonApplicationPreferencesStore(PreferencesPath + ".bak").LoadAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(original with { Theme = (ApplicationTheme)99 }, TestContext.Current.CancellationToken));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(original, cancellation.Token));
        Assert.Equal(ApplicationTheme.Dark, (await store.LoadAsync(TestContext.Current.CancellationToken)).Theme);
        await File.WriteAllTextAsync(PreferencesPath, "broken", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(original, TestContext.Current.CancellationToken));
        Assert.Equal(original, await new JsonApplicationPreferencesStore(PreferencesPath + ".bak").LoadAsync(TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
