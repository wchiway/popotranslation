using System.Runtime.InteropServices;

namespace PopoTranslation.Windows.Automation;

public enum TargetKind { Standard, Social, TextEditor, Terminal }

public static class TargetApplication
{
    private static readonly HashSet<string> SocialProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "QQ", "QQNT", "QQEX", "Weixin", "WeChat", "WeChatAppEx",
        "Discord", "Telegram", "TelegramDesktop"
    };

    private static readonly HashSet<string> TextEditorProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "notepad", "notepad++", "notepad3"
    };

    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal", "wt", "conhost", "OpenConsole", "cmd", "powershell", "pwsh",
        "mintty", "alacritty", "wezterm", "conemu", "conemu64", "tabby", "hyper"
    };

    public static TargetKind ClassifyWindow(nint window)
    {
        var name = GetProcessName(window);
        return name is null ? TargetKind.Standard : ClassifyProcessName(name);
    }

    public static TargetKind ClassifyProcessName(string processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName[..^4] : processName;
        if (SocialProcesses.Contains(name)) return TargetKind.Social;
        if (TextEditorProcesses.Contains(name)) return TargetKind.TextEditor;
        if (TerminalProcesses.Contains(name)) return TargetKind.Terminal;
        return TargetKind.Standard;
    }

    public static string? GetProcessName(nint window)
    {
        if (window == 0) return null;
        GetWindowThreadProcessId(window, out var processId);
        return processId == 0 ? null : GetProcessName(processId);
    }

    // Opens the process with query-limited rights only; no process enumeration is involved.
    public static string? GetProcessName(uint processId)
    {
        if (processId == 0) return null;
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0) return null;
        try
        {
            var buffer = new char[1024];
            var length = (uint)buffer.Length;
            if (!QueryFullProcessImageName(process, 0, buffer, ref length) || length == 0) return null;
            return Path.GetFileNameWithoutExtension(new string(buffer, 0, (int)length));
        }
        finally { CloseHandle(process); }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, [Out] char[] exeName, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
