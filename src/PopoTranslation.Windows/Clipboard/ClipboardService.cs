using System.Runtime.InteropServices;
using PopoTranslation.Windows.Input;
using PopoTranslation.Windows.Diagnostics;

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
        _worker.InvokeAsync(() => CaptureCoreAsync(expectedForeground, selectAll, terminalCopy, cancellationToken),
            cancellationToken);

    public ClipboardCaptureResult Capture(nint expectedForeground, bool selectAll, bool terminalCopy = false,
        CancellationToken cancellationToken = default) =>
        _worker.Invoke(() => CaptureCoreAsync(expectedForeground, selectAll, terminalCopy, cancellationToken),
            cancellationToken);

    public bool Paste(nint expectedForeground, string text, bool selectAll, bool terminal = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (terminal) return false;
        return _worker.Invoke(() => PasteCoreAsync(expectedForeground, text, selectAll));
    }

    private static async Task<ClipboardCaptureResult> CaptureCoreAsync(nint expectedForeground, bool selectAll,
        bool terminalCopy, CancellationToken cancellationToken)
    {
        DiagnosticLog.Write($"event=clipboard-start selectAll={selectAll} terminal={terminalCopy} expected={expectedForeground} foreground={GetForegroundWindow()}");
        FocusDiagnostics.Record("clipboard-start");
        if (expectedForeground == 0 || GetForegroundWindow() != expectedForeground)
            return new(ClipboardCaptureStatus.Unavailable, Reason: "目标窗口已失去焦点。");
        if (!TryReadClipboard(out var oldData, out var reason))
        {
            DiagnosticLog.Write("event=clipboard-read-failed");
            return new(ClipboardCaptureStatus.Unavailable, Reason: reason);
        }

        var sentinel = SentinelPrefix + Guid.NewGuid().ToString("N");
        string? restoreIfCurrent = null;
        try
        {
            if (!await TrySetTextAsync(sentinel, cancellationToken))
            {
                DiagnosticLog.Write("event=clipboard-sentinel-failed");
                return new(ClipboardCaptureStatus.Unavailable, Reason: "剪贴板正被其他程序占用。");
            }
            restoreIfCurrent = sentinel;
            var sequence = GetClipboardSequenceNumber();
            var released = await WaitForShortcutKeysReleasedAsync(cancellationToken);
            DiagnosticLog.Write($"event=keys-released success={released} spaceDown={KeyboardSimulator.IsSpaceDown()} modifiersDown={KeyboardSimulator.AreModifiersDown()}");
            if (!released) return new(ClipboardCaptureStatus.Unavailable, Reason: "快捷键仍被按住。");
            cancellationToken.ThrowIfCancellationRequested();
            if (GetForegroundWindow() != expectedForeground)
                return new(ClipboardCaptureStatus.Unavailable, Reason: "目标窗口已失去焦点。");
            if (selectAll)
            {
                var selected = KeyboardSimulator.SendSelectAll();
                DiagnosticLog.Write($"event=send-select-all success={selected} foreground={GetForegroundWindow()}");
                if (!selected) return new(ClipboardCaptureStatus.Unavailable, Reason: "无法发送全选按键。");
                await Task.Delay(80, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (GetForegroundWindow() != expectedForeground)
                return new(ClipboardCaptureStatus.Unavailable, Reason: "目标窗口已失去焦点。");
            FocusDiagnostics.Record("before-copy");
            var sent = terminalCopy ? KeyboardSimulator.SendTerminalCopy() : KeyboardSimulator.SendCopy();
            DiagnosticLog.Write($"event=send-copy success={sent} foreground={GetForegroundWindow()} spaceDown={KeyboardSimulator.IsSpaceDown()} modifiersDown={KeyboardSimulator.AreModifiersDown()}");
            if (!sent) return new(ClipboardCaptureStatus.Unavailable, Reason: "无法发送复制按键。");

            var deadline = Environment.TickCount64 + 2000;
            var sequenceChanged = false;
            while (Environment.TickCount64 < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (GetForegroundWindow() != expectedForeground)
                    return new(ClipboardCaptureStatus.Unavailable, Reason: "目标窗口已失去焦点。");
                sequenceChanged |= GetClipboardSequenceNumber() != sequence;
                // Delayed clipboard rendering may leave the sequence unchanged until
                // the data is requested, so reading cannot depend on that number.
                if (TryGetText(out var text) && !string.IsNullOrEmpty(text) && text != sentinel)
                {
                    sequenceChanged |= GetClipboardSequenceNumber() != sequence;
                    DiagnosticLog.Write($"event=clipboard-result status=captured sequenceChanged={sequenceChanged}");
                    restoreIfCurrent = text;
                    return new(ClipboardCaptureStatus.Captured, text);
                }
                await Task.Delay(20, cancellationToken);
            }
            DiagnosticLog.Write($"event=clipboard-result status=no-text sequenceChanged={sequenceChanged} foreground={GetForegroundWindow()}");
            FocusDiagnostics.Record("copy-timeout");
            return new(ClipboardCaptureStatus.NoSelection, Reason: sequenceChanged
                ? "输入框复制结果不是可用文本；请确认选区或输入内容，并尝试先手动选中文字。"
                : "没有读取到输入框文字；复制快捷键可能未送达，请确认焦点在输入框，且目标应用没有以更高权限运行。");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            DiagnosticLog.Write($"event=clipboard-exception type={error.GetType().Name} hresult={error.HResult:X8}");
            return new(ClipboardCaptureStatus.Unavailable, Reason: "剪贴板读取失败。");
        }
        finally
        {
            if (restoreIfCurrent is not null) RestoreClipboard(oldData, restoreIfCurrent);
        }
    }

    private static async Task<bool> PasteCoreAsync(nint expectedForeground, string text, bool selectAll)
    {
        if (GetForegroundWindow() != expectedForeground) return false;
        if (!TryReadClipboard(out var oldData, out _)) return false;
        if (!await TrySetTextAsync(text, CancellationToken.None)) return false;
        try
        {
            if (!await WaitForShortcutKeysReleasedAsync(CancellationToken.None)) return false;
            if (GetForegroundWindow() != expectedForeground) return false;
            if (selectAll)
            {
                if (!KeyboardSimulator.SendSelectAll()) return false;
                await Task.Delay(40);
            }
            if (GetForegroundWindow() != expectedForeground) return false;
            if (!KeyboardSimulator.SendPaste()) return false;
            // Give the target a chance to consume the paste before restoring the old data.
            await Task.Delay(1200);
            return true;
        }
        finally { RestoreClipboard(oldData, text); }
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

    private static async Task<bool> TrySetTextAsync(string text, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch { await Task.Delay(40, cancellationToken); }
        }
        return false;
    }

    private static async Task<bool> WaitForShortcutKeysReleasedAsync(CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + 1000;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!KeyboardSimulator.AreModifiersDown() && !KeyboardSimulator.IsSpaceDown())
            {
                await Task.Delay(60, cancellationToken);
                return true;
            }
            await Task.Delay(10, cancellationToken);
        }
        return !KeyboardSimulator.AreModifiersDown() && !KeyboardSimulator.IsSpaceDown();
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
