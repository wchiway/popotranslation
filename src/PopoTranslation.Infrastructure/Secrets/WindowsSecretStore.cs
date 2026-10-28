using System.Security.Cryptography;
using System.Text;
using PopoTranslation.Core;

namespace PopoTranslation.Infrastructure.Secrets;

public sealed class WindowsSecretStore : ISecretStore
{
    private readonly string _directory;

    public WindowsSecretStore(string directory) => _directory = Path.GetFullPath(directory);

    public async Task<string?> ReadAsync(string reference, CancellationToken cancellationToken = default)
    {
        string path = GetPath(reference);
        if (!File.Exists(path)) return null;
        byte[] encrypted = await File.ReadAllBytesAsync(path, cancellationToken);
        byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task WriteAsync(string reference, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        string path = GetPath(reference);
        byte[] plain = Encoding.UTF8.GetBytes(secret);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(_directory);
        string temporary = Path.Combine(_directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task DeleteAsync(string reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(GetPath(reference));
        return Task.CompletedTask;
    }

    private string GetPath(string reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(reference));
        return Path.Combine(_directory, Convert.ToHexString(hash) + ".dat");
    }
}
