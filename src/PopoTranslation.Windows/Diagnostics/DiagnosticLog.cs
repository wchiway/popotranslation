using System.Threading.Channels;

namespace PopoTranslation.Windows.Diagnostics;

// Only fixed diagnostic events and non-content metadata belong in this log.
public static class DiagnosticLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
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

    private static async Task WriteLoopAsync()
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PopoTranslation", "logs");
            Directory.CreateDirectory(directory);
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
