using System.Text.Json;
using System.Text.Json.Serialization;
using PopoTranslation.Core;

namespace PopoTranslation.Infrastructure.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<ApiProtocol>(allowIntegerValues: false) }
    };

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSettingsStore(string path) => _path = Path.GetFullPath(path);

    public async Task<AppSettings?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_path)) return null;
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, cancellationToken)
                ?? throw new InvalidDataException("Settings are empty.");
            Validate(settings);
            return settings;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Settings are invalid. The existing file has not been changed.", ex);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);
        await _gate.WaitAsync(cancellationToken);
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_path))
            {
                // Do not overwrite a recoverable backup with a corrupted current file.
                await using (var current = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try
                    {
                        var existing = await JsonSerializer.DeserializeAsync<AppSettings>(current, Options, cancellationToken)
                            ?? throw new InvalidDataException("Existing settings are empty.");
                        Validate(existing);
                    }
                    catch (JsonException ex)
                    {
                        throw new InvalidDataException("Existing settings are invalid. Neither the file nor its backup has been changed.", ex);
                    }
                }
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

    private static bool IsMachineTranslation(ApiProtocol protocol) => protocol is
        ApiProtocol.AlibabaTranslate or ApiProtocol.BaiduTranslate or ApiProtocol.YoudaoTranslate or ApiProtocol.DeepLTranslate;

    private static bool RequiresSecondaryCredential(ApiProtocol protocol) => protocol is
        ApiProtocol.AlibabaTranslate or ApiProtocol.BaiduTranslate or ApiProtocol.YoudaoTranslate;

    private static void Validate(AppSettings settings)
    {
        if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion || settings.Providers is null ||
            string.IsNullOrWhiteSpace(settings.TargetLanguage))
            throw new InvalidDataException("Settings have an unsupported version or missing required fields.");
        if ((settings.SelectionHotkey is not null && string.IsNullOrWhiteSpace(settings.SelectionHotkey)) ||
            (settings.WholeControlHotkey is not null && string.IsNullOrWhiteSpace(settings.WholeControlHotkey)) ||
            (settings.SelectionHotkey is not null && settings.WholeControlHotkey is not null &&
             string.Equals(settings.SelectionHotkey.Trim(), settings.WholeControlHotkey.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The configured hotkeys are empty or conflict.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in settings.Providers)
        {
            if (profile is null || string.IsNullOrWhiteSpace(profile.Id) || !ids.Add(profile.Id) ||
                string.IsNullOrWhiteSpace(profile.Name) || !Enum.IsDefined(profile.Protocol) ||
                !Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var url) ||
                (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment) ||
                (!IsMachineTranslation(profile.Protocol) && string.IsNullOrWhiteSpace(profile.ModelId)) ||
                string.IsNullOrWhiteSpace(profile.CredentialReference) ||
                (RequiresSecondaryCredential(profile.Protocol) && string.IsNullOrWhiteSpace(profile.SecondaryCredentialReference)) ||
                (profile.AdvancedConfigurationReference is not null && string.IsNullOrWhiteSpace(profile.AdvancedConfigurationReference)) ||
                profile.TimeoutSeconds is <= 0 or > 86400)
                throw new InvalidDataException("A provider has invalid or missing fields.");
        }
        if (settings.ActiveProviderId is not null && !ids.Contains(settings.ActiveProviderId))
            throw new InvalidDataException("The active provider does not exist.");
    }
}
