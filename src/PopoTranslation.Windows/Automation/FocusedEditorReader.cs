using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using PopoTranslation.Windows.Clipboard;
using PopoTranslation.Windows.Editing;

namespace PopoTranslation.Windows.Automation;

public sealed class FocusedEditorReader : IFocusedEditor, IDisposable
{
    private const int MaxTextLength = 1_000_000;
    private readonly AutomationWorker _worker = new();
    private readonly ClipboardService _clipboard = new();

    public static nint GetForegroundWindowAtTrigger() => GetForegroundWindow();

    public async Task<CaptureResult> CaptureAsync(CaptureMode mode, nint expectedForegroundHwnd,
        CancellationToken cancellationToken = default)
    {
        if (expectedForegroundHwnd == 0) return new(CaptureStatus.NoFocusedEditor);
        var targetKind = TargetApplication.ClassifyWindow(expectedForegroundHwnd);
        if (targetKind == TargetKind.Terminal && mode == CaptureMode.WholeControl)
            return new(CaptureStatus.Unsupported);
        try
        {
            var (accepted, uia) = await _worker.InvokeAsync(
                () => CaptureStable(mode, expectedForegroundHwnd), cancellationToken);
            if (accepted && uia is not null)
            {
                if (uia.Status == CaptureStatus.Captured)
                    return new(CaptureStatus.Captured, uia.Snapshot! with { TargetKind = targetKind });

                // Never bypass a protected or read-only refusal with clipboard simulation.
                if (uia.Status is not (CaptureStatus.Protected or CaptureStatus.ReadOnly))
                {
                    var clipboard = await TryClipboardCaptureAsync(
                        mode, expectedForegroundHwnd, targetKind, cancellationToken);
                    if (clipboard is not null) return clipboard;
                }
                return uia;
            }
            return new(CaptureStatus.Busy);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(CaptureStatus.Unavailable); }
    }

    private async Task<CaptureResult?> TryClipboardCaptureAsync(CaptureMode mode, nint expectedForeground,
        TargetKind targetKind, CancellationToken cancellationToken)
    {
        if (targetKind == TargetKind.Standard) return null;
        if (targetKind == TargetKind.Terminal && mode == CaptureMode.WholeControl) return null;
        var capture = await _clipboard.CaptureAsync(expectedForeground,
            selectAll: mode == CaptureMode.WholeControl, terminalCopy: targetKind == TargetKind.Terminal,
            cancellationToken);
        if (capture.Status != ClipboardCaptureStatus.Captured || capture.Text is null) return null;

        var processId = 0;
        var start = 0L;
        try
        {
            GetWindowThreadProcessId(expectedForeground, out var pid);
            processId = (int)pid;
            using var process = Process.GetProcessById(processId);
            start = process.StartTime.ToUniversalTime().Ticks;
        }
        catch { }

        var snapshot = new EditorSnapshot(expectedForeground, processId, start, "clipboard",
            capture.Text, 0, mode == CaptureMode.Selection ? capture.Text.Length : 0, mode,
            CaptureMethod.Clipboard, targetKind);
        return new(CaptureStatus.Captured, snapshot);
    }

    // The caller must synchronously execute the callback only while the task is current.
    public async Task<ReplacementResult> ReplaceAsync(EditorSnapshot snapshot, string translatedText,
        Func<Func<ReplacementResult>, ReplacementResult> executeIfCurrent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(translatedText);
        ArgumentNullException.ThrowIfNull(executeIfCurrent);
        if (snapshot.TargetKind == TargetKind.Terminal)
            return new(ReplacementStatus.ManualApply, "终端内容不会被自动替换，请复制译文后手动粘贴。");
        IReplacementBackend backend = snapshot.Method == CaptureMethod.Clipboard || snapshot.Mode == CaptureMode.Selection
            ? new ClipboardReplacementBackend(snapshot, _clipboard)
            : new NativeReplacementBackend(snapshot, _clipboard);
        try
        {
            var (accepted, result) = await _worker.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return executeIfCurrent(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return EditorReplacer.Replace(snapshot, translatedText, backend, cancellationToken);
                });
            }, cancellationToken, waitForCompletion: true);
            return accepted ? result! : new(ReplacementStatus.ManualApply, "辅助功能线程繁忙，请手动应用。");
        }
        catch (OperationCanceledException) { throw; }
        catch { return new(ReplacementStatus.ManualApply, "无法验证任务状态，请手动应用。"); }
    }

    private static CaptureResult CaptureStable(CaptureMode mode, nint expectedForegroundHwnd)
    {
        var first = CaptureOnce(mode, expectedForegroundHwnd);
        if (first.Status != CaptureStatus.Captured) return first;
        var second = CaptureOnce(mode, expectedForegroundHwnd);
        return second.Status == CaptureStatus.Captured &&
               EditorSnapshotValidator.Matches(first.Snapshot!, second.Snapshot!)
            ? second : new(CaptureStatus.Unavailable);
    }

    private static CaptureResult CaptureOnce(CaptureMode mode, nint expectedForegroundHwnd)
    {
        var window = GetForegroundWindow();
        if (window == 0 || window != expectedForegroundHwnd) return new(CaptureStatus.NoFocusedEditor);
        var element = AutomationElement.FocusedElement;
        if (element is null) return new(CaptureStatus.NoFocusedEditor);

        // Ignore UIA defaults: an unsupported protected/enabled property is not proof of safety.
        var passwordProperty = element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true);
        if (passwordProperty is not bool isPassword || isPassword) return new(CaptureStatus.Protected);
        var enabled = element.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true);
        if (enabled is not bool isEnabled || !isEnabled) return new(CaptureStatus.ReadOnly);
        var controlType = element.Current.ControlType;
        if (controlType != ControlType.Edit &&
            !(mode == CaptureMode.Selection && controlType == ControlType.Document))
            return new(CaptureStatus.Unsupported);

        ValuePattern? value = null;
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) &&
            valueObject is ValuePattern valuePattern)
            value = valuePattern;
        TextPattern? text = null;
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textObject) &&
            textObject is TextPattern textPattern &&
            textPattern.SupportedTextSelection != SupportedTextSelection.None)
            text = textPattern;

        // Reading a selection only needs TextPattern. ValuePattern is required for a whole
        // control read and is checked again at the moment of a write.
        if (text is null && mode == CaptureMode.Selection) return new(CaptureStatus.Unsupported);
        if (value is null && text is null) return new(CaptureStatus.Unsupported);
        if (value is not null && value.Current.IsReadOnly &&
            (mode == CaptureMode.WholeControl || text is null)) return new(CaptureStatus.ReadOnly);

        var processId = element.Current.ProcessId;
        GetWindowThreadProcessId(window, out var windowProcessId);
        if (processId <= 0 || windowProcessId != (uint)processId ||
            processId == Environment.ProcessId) return new(CaptureStatus.Unsupported);
        using var process = Process.GetProcessById(processId);
        var startTime = process.StartTime.ToUniversalTime().Ticks;
        var runtimeId = element.GetRuntimeId();
        if (runtimeId is null || runtimeId.Length == 0) return new(CaptureStatus.Unsupported);

        var document = text?.DocumentRange;
        var full = document?.GetText(MaxTextLength + 1) ?? value!.Current.Value;
        if (full.Length > MaxTextLength) return new(CaptureStatus.Unsupported);
        if (document is not null && value is not null && full != value.Current.Value)
            return new(CaptureStatus.Unsupported);

        var selectionStart = 0;
        var selectionEnd = 0;
        if (document is not null)
        {
            var selections = text!.GetSelection();
            if (selections.Length > 1) return new(CaptureStatus.Unsupported);
            if (selections.Length == 1)
            {
                var selection = selections[0];
                var prefix = document.Clone();
                prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection, TextPatternRangeEndpoint.Start);
                var suffix = document.Clone();
                suffix.MoveEndpointByRange(TextPatternRangeEndpoint.Start, selection, TextPatternRangeEndpoint.End);
                var before = prefix.GetText(MaxTextLength + 1);
                var selected = selection.GetText(MaxTextLength + 1);
                var after = suffix.GetText(MaxTextLength + 1);
                if (before + selected + after != full) return new(CaptureStatus.Unavailable);
                selectionStart = before.Length;
                selectionEnd = before.Length + selected.Length;
            }
            else if (mode == CaptureMode.Selection)
                return new(CaptureStatus.Empty);
        }

        if ((mode == CaptureMode.Selection && selectionEnd <= selectionStart) ||
            (mode == CaptureMode.WholeControl && full.Length == 0)) return new(CaptureStatus.Empty);
        if (GetForegroundWindow() != window) return new(CaptureStatus.Unavailable);

        return new(CaptureStatus.Captured, new EditorSnapshot(window, processId, startTime,
            string.Join(",", runtimeId), full, selectionStart, selectionEnd, mode));
    }

    private sealed class NativeReplacementBackend(EditorSnapshot snapshot, ClipboardService clipboard) : IReplacementBackend
    {
        private ValuePattern? _value;
        private EditorSnapshot? _before;
        private bool _usedClipboard;

        public bool SupportsSelection => true;
        public bool SupportsMultiline => true;

        public EditorSnapshot? ObserveEligibleTarget()
        {
            var foreground = GetForegroundWindow();
            if (foreground != snapshot.WindowHandle) return null;
            var captured = CaptureStable(snapshot.Mode, foreground);
            if (captured.Status != CaptureStatus.Captured) return null;
            var element = AutomationElement.FocusedElement;
            if (element is null || string.Join(",", element.GetRuntimeId()) != captured.Snapshot!.ElementRuntimeId)
                return null;
            if (element.Current.ControlType != ControlType.Edit) return null;
            var passwordProperty = element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true);
            if (passwordProperty is not bool isPassword || isPassword) return null;

            // ValuePattern cannot replace a selection; use clipboard for a selection.
            _value = null;
            if (snapshot.Mode == CaptureMode.WholeControl &&
                element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) &&
                pattern is ValuePattern value && !value.Current.IsReadOnly)
                _value = value;

            var final = CaptureOnce(snapshot.Mode, foreground);
            if (final.Status != CaptureStatus.Captured ||
                !EditorSnapshotValidator.Matches(captured.Snapshot, final.Snapshot!)) return null;
            _before = final.Snapshot;
            return _before;
        }

        public void SetValue(string text)
        {
            if (_value is not null)
            {
                _value.SetValue(text);
                _usedClipboard = false;
                return;
            }
            _usedClipboard = true;
            if (!clipboard.Paste(snapshot.WindowHandle, text, snapshot.Mode == CaptureMode.WholeControl))
                throw new InvalidOperationException("无法提交剪贴板粘贴。");
        }

        public bool ConfirmValue(string text)
        {
            if (_usedClipboard) return false;
            var element = AutomationElement.FocusedElement;
            return _before is not null && element is not null &&
                GetForegroundWindow() == _before.WindowHandle &&
                element.Current.ProcessId == _before.ProcessId &&
                string.Join(",", element.GetRuntimeId()) == _before.ElementRuntimeId &&
                _value!.Current.Value == text;
        }
    }

    private sealed class ClipboardReplacementBackend(EditorSnapshot snapshot, ClipboardService clipboard) : IReplacementBackend
    {
        public bool SupportsSelection => true;
        public bool SupportsMultiline => true;

        public EditorSnapshot? ObserveEligibleTarget()
        {
            if (GetForegroundWindow() != snapshot.WindowHandle) return null;
            GetWindowThreadProcessId(snapshot.WindowHandle, out var processId);
            if ((int)processId != snapshot.ProcessId) return null;
            var current = clipboard.Capture(snapshot.WindowHandle,
                selectAll: snapshot.Mode == CaptureMode.WholeControl);
            if (current.Status != ClipboardCaptureStatus.Captured || current.Text is null) return null;
            var expected = snapshot.Mode == CaptureMode.Selection ? snapshot.SourceText : snapshot.DocumentText;
            return current.Text == expected ? snapshot : null;
        }

        public void SetValue(string text)
        {
            if (!clipboard.Paste(snapshot.WindowHandle, text, snapshot.Mode == CaptureMode.WholeControl))
                throw new InvalidOperationException("无法提交剪贴板粘贴。");
        }

        // A generic clipboard paste has no portable acknowledgement from the target app.
        public bool ConfirmValue(string text) => false;
    }

    public void Dispose()
    {
        _worker.Dispose();
        _clipboard.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
