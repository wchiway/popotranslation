namespace PopoTranslation.Core;

public enum InterfaceLanguage
{
    SimplifiedChinese,
    English
}

public enum ApplicationTheme
{
    System,
    Light,
    Dark
}

public sealed record ApplicationPreferences(
    InterfaceLanguage Language = InterfaceLanguage.SimplifiedChinese,
    ApplicationTheme Theme = ApplicationTheme.System,
    bool NotificationsEnabled = true);

public interface IApplicationPreferencesStore
{
    Task<ApplicationPreferences> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ApplicationPreferences preferences, CancellationToken cancellationToken = default);
}
