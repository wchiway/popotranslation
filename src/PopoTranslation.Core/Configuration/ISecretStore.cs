namespace PopoTranslation.Core;

public interface ISecretStore
{
    Task<string?> ReadAsync(string reference, CancellationToken cancellationToken = default);
    Task WriteAsync(string reference, string secret, CancellationToken cancellationToken = default);
    Task DeleteAsync(string reference, CancellationToken cancellationToken = default);
}
