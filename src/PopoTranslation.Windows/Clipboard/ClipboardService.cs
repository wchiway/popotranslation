using System.Runtime.InteropServices;
using PopoTranslation.Windows.Input;

namespace PopoTranslation.Windows.Clipboard;

public enum ClipboardCaptureStatus { Captured, NoSelection, Unavailable }

public sealed record ClipboardCaptureResult(ClipboardCaptureStatus Status, string? Text = null, string? Reason = null);

// Text clipboard bridge for apps whose message box does not expose an editable
// UI Automation text control. The previous clipboard object is restored best effort.
public sealed class ClipboardService : IDisposable
{
    private const string SentinelPrefix = "PopoTranslation.";
    private readonly StaWorker _worker = new();

    public Task<ClipboardCaptureResult> CaptureAsync(nint expectedForeground, bool selectAll,
        bool terminalCopy, CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync(() => CaptureCore(expectedForeground, selectAll, terminalCopy, cancellationToken),
            cancellationToken);

    public ClipboardCaptureResult Capture(nint expectedForeground, bool selectAll, bool terminalCopy = false,
        CancellationToken cancellationToken = default) =>
        _worker.Invoke(() => CaptureCore(expectedForeground, selectAll, terminalCopy, cancellationToken));

    public bool Paste(nint expectedForeground, string text, bool selectAll, bool terminal = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (terminal) return false;
        return _worker.Invoke(() => PasteCore(expectedForeground, text, selectAll));
    }

    private static ClipboardCaptureResult CaptureCore(nint expectedForeground, bool selectAll,
        bool terminalCopy, CancellationToken cancellationToken)
    {
        if (expectedForeground == 0 || GetForegroundWindow() != expectedForeground)
            return new(ClipboardCaptureStatus.Unavailable, Reason: "目标窗口已失去焦点。");
        if (!TryReadClipboard(out var oldData, out var reason))
            return new(ClipboardCaptureStatus.Unavailable, Reason: reason);

        var sentinel = SentinelPrefix + Guid.NewGuid().ToString("N");
        try
        {
            if (!TrySetText(sentinel)) return new(ClipboardCaptureStatus.Unavailable, Reason: "剪贴板正被其他程序占用。");
            var sequence = GetClipboardSequenceNumber();
            if (!KeyboardSimulator.WaitForModifiersReleased(TimeSpan.FromSeconds(1)))
                return new(ClipboardCaptureStatus.Unavailable, Reason: "快捷键仍被按住。");
            if (selectAll) KeyboardSimulator.SendSelectAll();
            var sent = terminalCopy ? KeyboardSimulator.SendTerminalCopy() : KeyboardSimulator.SendCopy();
            if (!sent) return new(ClipboardCaptureStatus.Unavailable, Reason: "无法发送复制按键。");

            var deadline = Environment.TickCount64 + 1200;
            while (Environment.TickCount64 < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (GetForegroundWindow() != expectedForeground)
                    return new(ClipboardCaptureStatus.Unavailable, Reason: "目标窗口已失去焦点。");
                if (GetClipboardSequenceNumber() != sequence && TryGetText(out var text))
                {
                    if (!string.IsNullOrEmpty(text) && text != sentinel)
                    {
                        RestoreClipboard(oldData, text);
                        return new(ClipboardCaptureStatus.Captured, text);
                    }
                }
                Thread.Sleep(20);
            }
            RestoreClipboard(oldData, sentinel);
            return new(ClipboardCaptureStatus.NoSelection, Reason: "没有可读取的选中文字。");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            RestoreClipboard(oldData, sentinel);
            return new(ClipboardCaptureStatus.Unavailable, Reason: "剪贴板读取失败。");
        }
    }

    private static bool PasteCore(nint expectedForeground, string text, bool selectAll)
    {
        if (GetForegroundWindow() != expectedForeground) return false;
        if (!TryReadClipboard(out var oldData, out _)) return false;
        if (!TrySetText(text)) return false;
        if (!KeyboardSimulator.WaitForModifiersReleased(TimeSpan.FromSeconds(1))) return false;
        if (selectAll)
        {
            KeyboardSimulator.SendSelectAll();
            Thread.Sleep(40);
        }
        var sent = KeyboardSimulator.SendPaste();
        if (!sent) return false;
        // Give the target a chance to consume the paste before restoring the old data.
        Thread.Sleep(1200);
        RestoreClipboard(oldData, text);
        return true;
    }

    private static bool TryReadClipboard(out System.Windows.IDataObject? oldData, out string? reason)
    {
        oldData = null;
        reason = null;
        try
        {
            oldData = System.Windows.Clipboard.GetDataObject();
            return true;
        }
        catch
        {
            reason = "无法读取剪贴板。";
            return false;
        }
    }

    private static bool TrySetText(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch { Thread.Sleep(40); }
        }
        return false;
    }

    private static bool TryGetText(out string text)
    {
        try
        {
            text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : string.Empty;
            return true;
        }
        catch { text = string.Empty; return false; }
    }

    private static void RestoreClipboard(System.Windows.IDataObject? oldData, string? expectedCurrent)
    {
        try
        {
            if (expectedCurrent is not null && (!TryGetText(out var current) || current != expectedCurrent)) return;
            if (oldData is not null) System.Windows.Clipboard.SetDataObject(oldData, copy: true);
            else System.Windows.Clipboard.Clear();
        }
        catch { /* Clipboard restoration is best effort. */ }
    }

    public void Dispose() => _worker.Dispose();

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
