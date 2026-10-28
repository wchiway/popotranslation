using System.Text.Json;
using System.Text.Json.Serialization;
using PopoTranslation.Core;

namespace PopoTranslation.Infrastructure.Settings;

public sealed class JsonApplicationPreferencesStore : IApplicationPreferencesStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter<InterfaceLanguage>(allowIntegerValues: false),
            new JsonStringEnumConverter<ApplicationTheme>(allowIntegerValues: false)
        }
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonApplicationPreferencesStore(string path) => _path = Path.GetFullPath(path);

    public async Task<ApplicationPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path)) return new ApplicationPreferences();
            return await ReadAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(ApplicationPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        Validate(preferences);
        await _gate.WaitAsync(cancellationToken);
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, preferences, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_path))
            {
                // Preserve both the current file and its backup if the current file is unreadable.
                await ReadAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Replace(temporaryPath, _path, _path + ".bak");
            }
            else
                File.Move(temporaryPath, _path);
            temporaryPath = null;
        }
        finally
        {
            try
            {
                if (temporaryPath is not null && File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            finally { _gate.Release(); }
        }
    }

    private async Task<ApplicationPreferences> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var preferences = await JsonSerializer.DeserializeAsync<ApplicationPreferences>(stream, Options, cancellationToken)
                ?? throw new InvalidDataException("Application preferences are empty.");
            Validate(preferences);
            return preferences;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Application preferences are invalid. The existing file has not been changed.", ex);
        }
    }

    private static void Validate(ApplicationPreferences preferences)
    {
        if (!Enum.IsDefined(preferences.Language) || !Enum.IsDefined(preferences.Theme))
            throw new InvalidDataException("Application preferences contain an unsupported language or theme.");
    }
}
