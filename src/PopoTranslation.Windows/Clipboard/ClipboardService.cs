using System.Diagnostics;
using System.Runtime.InteropServices;
using PopoTranslation.Windows.Input;
using PopoTranslation.Windows.Diagnostics;

namespace PopoTranslation.Windows.Clipboard;

public enum ClipboardCaptureStatus { Captured, NoSelection, Unavailable }
public enum ClipboardPasteStatus { NotSubmitted, Submitted, Unknown }

public sealed record ClipboardCaptureResult(ClipboardCaptureStatus Status, string? Text = null, string? Reason = null);

// Text clipboard bridge for apps whose message box does not expose an editable
// UI Automation text control. The previous clipboard object is restored best effort.
public sealed class ClipboardService : IDisposable
{
    private const string SentinelPrefix = "PopoTranslation.";
    private const string ExcludeFromClipboardMonitor = "ExcludeClipboardContentFromMonitorProcessing";
    private readonly StaWorker _worker = new();

    public Task<ClipboardCaptureResult> CaptureAsync(nint expectedForeground, bool selectAll,
        bool terminalCopy, CancellationToken cancellationToken = default) =>
        _worker.InvokeAsync(() => CaptureCoreAsync(expectedForeground, selectAll, terminalCopy, cancellationToken),
            cancellationToken);

    public ClipboardCaptureResult Capture(nint expectedForeground, bool selectAll, bool terminalCopy = false,
        CancellationToken cancellationToken = default) =>
        _worker.Invoke(() => CaptureCoreAsync(expectedForeground, selectAll, terminalCopy, cancellationToken),
            cancellationToken);

    public ClipboardPasteStatus Paste(nint expectedForeground, string text, bool selectAll, bool terminal = false,
        CancellationToken cancellationToken = default, Func<bool>? isCurrent = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (terminal) return ClipboardPasteStatus.NotSubmitted;
        return _worker.Invoke(() => PasteCoreAsync(expectedForeground, text, selectAll, cancellationToken, isCurrent), cancellationToken);
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
            if ((KeyboardSimulator.AreModifiersDown() || KeyboardSimulator.IsSpaceDown()) &&
                !await WaitForShortcutKeysReleasedAsync(cancellationToken))
            {
                DiagnosticLog.Write("event=clipboard-result status=keys-held");
                return new(ClipboardCaptureStatus.Unavailable, Reason: "快捷键仍被按住。");
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

    private static async Task<ClipboardPasteStatus> PasteCoreAsync(nint expectedForeground, string text, bool selectAll,
        CancellationToken cancellationToken, Func<bool>? isCurrent)
    {
        DiagnosticLog.Write($"event=paste-start selectAll={selectAll} foregroundMatches={GetForegroundWindow() == expectedForeground}");
        cancellationToken.ThrowIfCancellationRequested();
        if (GetForegroundWindow() != expectedForeground) return ClipboardPasteStatus.NotSubmitted;
        if (!TryReadClipboard(out var oldData, out _)) { DiagnosticLog.Write("event=paste-result stage=read-clipboard"); return ClipboardPasteStatus.NotSubmitted; }
        if (!await TrySetTextAsync(text, cancellationToken)) { DiagnosticLog.Write("event=paste-result stage=set-clipboard"); return ClipboardPasteStatus.NotSubmitted; }
        try
        {
            if (!await WaitForShortcutKeysReleasedAsync(cancellationToken)) { DiagnosticLog.Write("event=paste-result stage=keys-held"); return ClipboardPasteStatus.NotSubmitted; }
            cancellationToken.ThrowIfCancellationRequested();
            if (GetForegroundWindow() != expectedForeground) { DiagnosticLog.Write("event=paste-result stage=focus"); return ClipboardPasteStatus.NotSubmitted; }
            if (isCurrent is not null && !isCurrent()) { DiagnosticLog.Write("event=paste-result stage=stale"); return ClipboardPasteStatus.NotSubmitted; }
            if (selectAll)
            {
                if (!KeyboardSimulator.SendSelectAll()) { DiagnosticLog.Write("event=paste-result stage=select-all"); return ClipboardPasteStatus.NotSubmitted; }
                await Task.Delay(40, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (GetForegroundWindow() != expectedForeground) { DiagnosticLog.Write("event=paste-result stage=focus"); return ClipboardPasteStatus.NotSubmitted; }
            if (KeyboardSimulator.AreModifiersDown() || KeyboardSimulator.IsSpaceDown()) { DiagnosticLog.Write("event=paste-result stage=keys-held"); return ClipboardPasteStatus.NotSubmitted; }
            if (isCurrent is not null && !isCurrent()) { DiagnosticLog.Write("event=paste-result stage=stale"); return ClipboardPasteStatus.NotSubmitted; }
            if (!KeyboardSimulator.SendPaste()) { DiagnosticLog.Write("event=paste-result stage=send"); return ClipboardPasteStatus.Unknown; }
            DiagnosticLog.Write("event=paste-result stage=submitted");
            // After submission always finish restoration; cancellation cannot undo a sent paste.
            await Task.Delay(1200);
            return ClipboardPasteStatus.Submitted;
        }
        finally { RestoreClipboard(oldData, text); }
    }

    private static bool TryReadClipboard(out System.Windows.IDataObject? oldData, out string? reason)
    {
        oldData = null;
        reason = null;
        var timer = Stopwatch.StartNew();
        try
        {
            oldData = System.Windows.Clipboard.GetDataObject();
            DiagnosticLog.Write($"event=clipboard-operation operation=get-data-object status=success elapsedMs={timer.ElapsedMilliseconds} hasData={oldData is not null}");
            return true;
        }
        catch (Exception error)
        {
            DiagnosticLog.Write($"event=clipboard-operation operation=get-data-object status=failed elapsedMs={timer.ElapsedMilliseconds} type={error.GetType().Name} hresult={error.HResult:X8}");
            reason = "无法读取剪贴板。";
            return false;
        }
    }

    private static async Task<bool> TrySetTextAsync(string text, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timer = Stopwatch.StartNew();
            try
            {
                var data = new System.Windows.DataObject();
                data.SetText(text);
                // Windows ignores all formats in this item when this registered format is present.
                data.SetData(ExcludeFromClipboardMonitor, new System.IO.MemoryStream(new byte[] { 1 }), autoConvert: false);
                System.Windows.Clipboard.SetDataObject(data, copy: true);
                DiagnosticLog.Write($"event=clipboard-operation operation=set-temporary status=success attempt={attempt + 1} elapsedMs={timer.ElapsedMilliseconds}");
                return true;
            }
            catch (Exception error)
            {
                DiagnosticLog.Write($"event=clipboard-operation operation=set-temporary status=failed attempt={attempt + 1} elapsedMs={timer.ElapsedMilliseconds} type={error.GetType().Name} hresult={error.HResult:X8}");
                await Task.Delay(40, cancellationToken);
            }
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
                if (!KeyboardSimulator.AreModifiersDown() && !KeyboardSimulator.IsSpaceDown()) return true;
            }
            await Task.Delay(10, cancellationToken);
        }
        return false;
    }

    private static bool TryGetText(out string text, bool logFailure = false)
    {
        var timer = logFailure ? Stopwatch.StartNew() : null;
        try
        {
            text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : string.Empty;
            return true;
        }
        catch (Exception error)
        {
            if (logFailure)
                DiagnosticLog.Write($"event=clipboard-operation operation=check-current-text status=failed elapsedMs={timer!.ElapsedMilliseconds} type={error.GetType().Name} hresult={error.HResult:X8}");
            text = string.Empty;
            return false;
        }
    }

    private static void RestoreClipboard(System.Windows.IDataObject? oldData, string? expectedCurrent)
    {
        var timer = Stopwatch.StartNew();
        var operation = "check-current";
        try
        {
            if (expectedCurrent is not null && (!TryGetText(out var current, logFailure: true) || current != expectedCurrent))
            {
                DiagnosticLog.Write($"event=clipboard-operation operation={operation} status=skipped elapsedMs={timer.ElapsedMilliseconds}");
                return;
            }
            DiagnosticLog.Write($"event=clipboard-operation operation={operation} status=matched elapsedMs={timer.ElapsedMilliseconds}");
            operation = oldData is not null ? "restore-data-object" : "restore-clear";
            timer.Restart();
            if (oldData is not null) System.Windows.Clipboard.SetDataObject(oldData, copy: true);
            else System.Windows.Clipboard.Clear();
            DiagnosticLog.Write($"event=clipboard-operation operation={operation} status=success elapsedMs={timer.ElapsedMilliseconds}");
        }
        catch (Exception error)
        {
            DiagnosticLog.Write($"event=clipboard-operation operation={operation} status=failed elapsedMs={timer.ElapsedMilliseconds} type={error.GetType().Name} hresult={error.HResult:X8}");
        }
    }

    public void Dispose() => _worker.Dispose();

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
