namespace PopoTranslation.Windows.Storage;

/// <summary>Resolves storage without moving or copying existing user data.</summary>
public sealed class ApplicationPaths
{
    private static readonly Lazy<ApplicationPaths> CurrentPaths = new(() => new ApplicationPaths(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));

    public static ApplicationPaths Current => CurrentPaths.Value;

    public ApplicationPaths(string userProfile, string localApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
        var home = Path.Combine(Path.GetFullPath(userProfile), ".PopoTranslation");
        var legacy = Path.Combine(Path.GetFullPath(localApplicationData), "PopoTranslation");
        DataDirectory = DirectoryExists(legacy) ? legacy : home;
        LogsDirectory = Path.Combine(DataDirectory, "logs");
    }

    public string DataDirectory { get; }
    public string LogsDirectory { get; }

    private static bool DirectoryExists(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Directory) != 0;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
