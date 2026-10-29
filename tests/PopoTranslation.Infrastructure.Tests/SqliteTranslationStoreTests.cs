using PopoTranslation.Core;
using PopoTranslation.Infrastructure.Storage;

namespace PopoTranslation.Infrastructure.Tests;

public sealed class SqliteTranslationStoreTests
{
    private static TranslationCacheContext Context() => new(
        new TranslationRequest("Hello '世界'", "zh", "en", "formal"), "provider", ApiProtocol.OpenAiResponses,
        "https://example.test/v1", "model", "credential-ref", "secondary-ref", "{\"X-Test\":\"secret-header\"}", "{\"temperature\":0.5}");

    [Fact]
    public async Task SuccessfulResultsPersistAndClearIndependently()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "history.db");
            var store = new SqliteTranslationStore(path);
            var context = Context();
            await store.SaveSuccessAsync(context, "你好", false, true, true);
            await store.SaveSuccessAsync(context, "你好", true, true, false);
            var reopened = new SqliteTranslationStore(path);
            Assert.Equal("你好", await reopened.FindCachedAsync(context));
            var history = await reopened.GetHistoryAsync(1);
            Assert.Single(history);
            Assert.True(history[0].CacheHit);
            Assert.Equal(context.Request.SourceText, history[0].SourceText);
            Assert.Equal(2, (await reopened.GetHistoryAsync(10)).Count);
            await reopened.ClearHistoryAsync();
            Assert.Empty(await reopened.GetHistoryAsync(10));
            Assert.Equal("你好", await reopened.FindCachedAsync(context));
            await reopened.ClearCacheAsync();
            Assert.Null(await reopened.FindCachedAsync(context));
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.DoesNotContain("secret-header", System.Text.Encoding.UTF8.GetString(bytes));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task RejectsUnknownDatabaseWithoutChangingIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "unknown.db");
            await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE existing_data (value TEXT); INSERT INTO existing_data VALUES ('preserved');";
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            var store = new SqliteTranslationStore(path);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.GetHistoryAsync(10, TestContext.Current.CancellationToken));
            await using var check = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await check.OpenAsync(TestContext.Current.CancellationToken);
            await using var query = check.CreateCommand();
            query.CommandText = "SELECT value FROM existing_data";
            Assert.Equal("preserved", await query.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CacheSeparatesEveryResultAffectingInput()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SqliteTranslationStore(Path.Combine(directory, "cache.db"));
            var context = Context();
            await store.SaveSuccessAsync(context, "translated", false, true, true);
            TranslationCacheContext[] variants =
            [
                context with { Request = context.Request with { SourceText = "different" } },
                context with { Request = context.Request with { TargetLanguage = "en" } },
                context with { Request = context.Request with { SourceLanguage = null } },
                context with { Request = context.Request with { Style = null } },
                context with { ProviderId = "other" },
                context with { Protocol = ApiProtocol.OpenAiChatCompletions },
                context with { BaseUrl = "https://other.test/v1" },
                context with { ModelId = "other" },
                context with { CredentialReference = "other" },
                context with { SecondaryCredentialReference = null },
                context with { AdvancedHeadersJson = null },
                context with { AdvancedBodyJson = null }
            ];
            foreach (var variant in variants) Assert.Null(await store.FindCachedAsync(variant));
            Assert.Equal("translated", await store.FindCachedAsync(context));
            await Assert.ThrowsAsync<ArgumentException>(() => store.SaveSuccessAsync(context, " ", false, true, true));
            Assert.Single(await store.GetHistoryAsync(10));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
