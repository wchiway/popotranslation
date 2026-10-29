using System.Security.Cryptography;
using System.Text;
using PopoTranslation.Core;
using PopoTranslation.Infrastructure.Secrets;
using PopoTranslation.Infrastructure.Settings;

namespace PopoTranslation.Infrastructure.Tests;

public class SecretStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "PopoTranslation.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SecretsRoundTripAsCurrentUserWithoutPlaintextFile()
    {
        var store = new WindowsSecretStore(_directory);
        Assert.Null(await store.ReadAsync("../reference", TestContext.Current.CancellationToken));
        await store.WriteAsync("../reference", "secret-value-秘密", TestContext.Current.CancellationToken);
        Assert.Equal("secret-value-秘密", await store.ReadAsync("../reference", TestContext.Current.CancellationToken));
        byte[] encrypted = await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(_directory)), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("secret-value", Encoding.UTF8.GetString(encrypted));
        Assert.Equal("secret-value-秘密", Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser)));
        await store.DeleteAsync("../reference", TestContext.Current.CancellationToken);
        Assert.Null(await store.ReadAsync("../reference", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AdvancedConfigurationIsEncryptedAndSettingsContainOnlyReference()
    {
        var secrets = new WindowsSecretStore(Path.Combine(_directory, "secrets"));
        var settingsPath = Path.Combine(_directory, "settings.json");
        var settingsStore = new JsonSettingsStore(settingsPath);
        const string options = "{\"HeadersJson\":\"{\\\"X-Custom\\\":\\\"header-secret\\\"}\",\"BodyJson\":\"{\\\"token\\\":\\\"body-secret\\\"}\"}";
        await secrets.WriteAsync("advanced-ref", options, TestContext.Current.CancellationToken);
        var settings = new AppSettings(1,
            [new("id", "name", ApiProtocol.OpenAiResponses, "https://example.com", "model", "key-ref", 30,
                AdvancedConfigurationReference: "advanced-ref")], "id", "中文", null, null);
        await settingsStore.SaveAsync(settings, TestContext.Current.CancellationToken);
        var loaded = await settingsStore.LoadAsync(TestContext.Current.CancellationToken);
        var plaintext = await secrets.ReadAsync(loaded!.Providers[0].AdvancedConfigurationReference!, TestContext.Current.CancellationToken);
        Assert.Equal(options, plaintext);
        var settingsJson = await File.ReadAllTextAsync(settingsPath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("header-secret", settingsJson);
        Assert.DoesNotContain("body-secret", settingsJson);
        var encrypted = await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(Path.Combine(_directory, "secrets"))), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("header-secret", Encoding.UTF8.GetString(encrypted));
        Assert.DoesNotContain("body-secret", Encoding.UTF8.GetString(encrypted));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
