using System.Threading.Channels;
using PopoTranslation.Windows.Storage;

namespace PopoTranslation.Windows.Diagnostics;

// Only fixed diagnostic events and non-content metadata belong in this log.
public static class DiagnosticLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private const int KeepLogFiles = 10;
    private static readonly Channel<string> Entries = Channel.CreateBounded<string>(new BoundedChannelOptions(1024)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true
    });

    static DiagnosticLog() => _ = Task.Run(WriteLoopAsync);

    public static void Write(string message)
    {
        Entries.Writer.TryWrite($"{DateTimeOffset.Now:O} pid={Environment.ProcessId} {message}");
    }

    /// <summary>Appends synchronously so the line survives a process that is about to terminate.</summary>
    public static void WriteCrash(string message)
    {
        try
        {
            var directory = ApplicationPaths.Current.LogsDirectory;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "crash.log");
            if (new FileInfo(path) is { Exists: true, Length: > MaxBytes }) File.Delete(path);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} {message}{Environment.NewLine}");
        }
        catch { /* Crash logging must never throw. */ }
    }

    /// <summary>Deletes diagnostics files beyond the newest <paramref name="keepNewest"/>. Returns the number removed.</summary>
    public static int PruneLogFiles(string directory, int keepNewest)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keepNewest);
        try
        {
            if (!Directory.Exists(directory)) return 0;
            var deleted = 0;
            foreach (var file in new DirectoryInfo(directory).GetFiles("diagnostics-*.log")
                         .OrderByDescending(file => file.LastWriteTimeUtc).Skip(keepNewest))
            {
                try { file.Delete(); deleted++; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return deleted;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static async Task WriteLoopAsync()
    {
        try
        {
            var directory = ApplicationPaths.Current.LogsDirectory;
            Directory.CreateDirectory(directory);
            PruneLogFiles(directory, KeepLogFiles - 1);
            var file = Path.Combine(directory, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log");
            await using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
            await using var writer = new StreamWriter(stream) { AutoFlush = true };
            await writer.WriteLineAsync($"{DateTimeOffset.Now:O} pid={Environment.ProcessId} event=log-start assembly={typeof(DiagnosticLog).Assembly.GetName().Version}");
            await foreach (var entry in Entries.Reader.ReadAllAsync())
            {
                if (stream.Position >= MaxBytes) break;
                await writer.WriteLineAsync(entry);
            }
        }
        catch { /* Diagnostics must never interrupt input handling. */ }
    }
}
