using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PopoTranslation.Core;

namespace PopoTranslation.Infrastructure.Storage;

/// <summary>Local plaintext translation history and cache. The caller controls the database path.</summary>
public sealed class SqliteTranslationStore : ITranslationStore
{
    private readonly string _path;

    public SqliteTranslationStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _path = Path.GetFullPath(databasePath);
    }

    public async Task<string?> FindCachedAsync(TranslationCacheContext context, CancellationToken cancellationToken = default)
    {
        var key = CacheKey(context);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT translated_text FROM translation_cache WHERE cache_key = $key";
        command.Parameters.AddWithValue("$key", key);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    public async Task SaveSuccessAsync(TranslationCacheContext context, string translatedText, bool cacheHit,
        bool saveToHistory, bool saveToCache, CancellationToken cancellationToken = default)
    {
        var key = CacheKey(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(translatedText);
        if (!saveToHistory && !saveToCache) return;
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (saveToHistory)
        {
        command.CommandText = """
            INSERT INTO translation_history(created_at, source_text, translated_text, provider_id, protocol,
                target_language, source_language, style, cache_hit)
            VALUES ($created, $source, $translation, $provider, $protocol, $target, $language, $style, $hit)
            """;
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$source", context.Request.SourceText);
        command.Parameters.AddWithValue("$translation", translatedText);
        command.Parameters.AddWithValue("$provider", context.ProviderId);
        command.Parameters.AddWithValue("$protocol", (int)context.Protocol);
        command.Parameters.AddWithValue("$target", context.Request.TargetLanguage);
        command.Parameters.AddWithValue("$language", (object?)context.Request.SourceLanguage ?? DBNull.Value);
        command.Parameters.AddWithValue("$style", (object?)context.Request.Style ?? DBNull.Value);
        command.Parameters.AddWithValue("$hit", cacheHit ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (saveToCache)
        {
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO translation_cache(cache_key, translated_text) VALUES ($key, $translation)
                ON CONFLICT(cache_key) DO UPDATE SET translated_text = excluded.translated_text
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$translation", translatedText);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    public async Task<IReadOnlyList<TranslationHistoryEntry>> GetHistoryAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, created_at, source_text, translated_text, provider_id, protocol, target_language,
                source_language, style, cache_hit FROM translation_history ORDER BY id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<TranslationHistoryEntry>();
        while (await reader.ReadAsync(cancellationToken))
            entries.Add(new(reader.GetInt64(0), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                reader.GetString(2), reader.GetString(3), reader.GetString(4), (ApiProtocol)reader.GetInt32(5),
                reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.GetInt32(9) != 0));
        return entries;
    }

    public Task ClearHistoryAsync(CancellationToken cancellationToken = default) => ClearAsync("DELETE FROM translation_history", cancellationToken);
    public Task ClearCacheAsync(CancellationToken cancellationToken = default) => ClearAsync("DELETE FROM translation_cache", cancellationToken);

    private async Task ClearAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            using var transaction = connection.BeginTransaction();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
            if (version == 0)
            {
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
                if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) != 0)
                    throw new InvalidDataException("The database has an unknown schema and was not changed.");
                command.CommandText = """
                CREATE TABLE translation_history (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    created_at INTEGER NOT NULL, source_text TEXT NOT NULL, translated_text TEXT NOT NULL,
                    provider_id TEXT NOT NULL, protocol INTEGER NOT NULL, target_language TEXT NOT NULL,
                    source_language TEXT, style TEXT, cache_hit INTEGER NOT NULL
                );
                CREATE TABLE translation_cache (cache_key BLOB PRIMARY KEY, translated_text TEXT NOT NULL);
                PRAGMA user_version = 1;
                """;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            else if (version != 1)
                throw new InvalidDataException("The database schema version is unsupported and was not changed.");
            transaction.Commit();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static byte[] CacheKey(TranslationCacheContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Request);
        ArgumentNullException.ThrowIfNull(context.Request.SourceText);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.Request.TargetLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ProviderId);
        ArgumentNullException.ThrowIfNull(context.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.CredentialReference);
        if (!Enum.IsDefined(context.Protocol) || !Uri.TryCreate(context.BaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Invalid provider protocol or endpoint.", nameof(context));
        // JSON array serialization preserves nulls and string boundaries; version permits future invalidation.
        var data = JsonSerializer.SerializeToUtf8Bytes(new object?[] { 1, context.Request.SourceText,
            context.ProviderId, (int)context.Protocol, context.BaseUrl, context.ModelId,
            context.CredentialReference, context.SecondaryCredentialReference, context.Request.TargetLanguage, context.Request.SourceLanguage, context.Request.Style,
            context.AdvancedHeadersJson, context.AdvancedBodyJson });
        return SHA256.HashData(data);
    }
}
