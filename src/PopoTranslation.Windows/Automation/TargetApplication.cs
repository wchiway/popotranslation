using System.Diagnostics;
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
        try
        {
            GetWindowThreadProcessId(window, out var processId);
            if (processId == 0) return null;
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch { return null; }
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
