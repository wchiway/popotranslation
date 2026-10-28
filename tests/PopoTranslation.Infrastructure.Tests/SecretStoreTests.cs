using System.Security.Cryptography;
using System.Text;
using PopoTranslation.Infrastructure.Secrets;

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

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
