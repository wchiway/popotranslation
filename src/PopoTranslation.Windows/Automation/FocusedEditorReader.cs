using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using PopoTranslation.Windows.Editing;

namespace PopoTranslation.Windows.Automation;

public sealed class FocusedEditorReader : IFocusedEditor, IDisposable
{
    private const int MaxTextLength = 1_000_000;
    private readonly AutomationWorker _worker = new();

    public static nint GetForegroundWindowAtTrigger() => GetForegroundWindow();

    public async Task<CaptureResult> CaptureAsync(CaptureMode mode, nint expectedForegroundHwnd,
        CancellationToken cancellationToken = default)
    {
        if (expectedForegroundHwnd == 0) return new(CaptureStatus.NoFocusedEditor);
        try
        {
            var (accepted, result) = await _worker.InvokeAsync(() => CaptureStable(mode, expectedForegroundHwnd), cancellationToken);
            return accepted ? result! : new(CaptureStatus.Busy);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(CaptureStatus.Unavailable); }
    }

    // The caller must synchronously execute the callback only while the task is current.
    public async Task<ReplacementResult> ReplaceAsync(EditorSnapshot snapshot, string translatedText,
        Func<Func<ReplacementResult>, ReplacementResult> executeIfCurrent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(translatedText);
        ArgumentNullException.ThrowIfNull(executeIfCurrent);
        try
        {
            var (accepted, result) = await _worker.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return executeIfCurrent(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return EditorReplacer.Replace(snapshot, translatedText, new NativeReplacementBackend(), cancellationToken);
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

        // Ignore UIA defaults: an unsupported password/enabled property is not proof of safety.
        var password = element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true);
        if (password is not bool isPassword || isPassword) return new(CaptureStatus.Protected);
        var enabled = element.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty, true);
        if (enabled is not bool isEnabled || !isEnabled) return new(CaptureStatus.ReadOnly);
        if (element.Current.ControlType != ControlType.Edit) return new(CaptureStatus.Unsupported);

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

    private sealed class NativeReplacementBackend : IReplacementBackend
    {
        private ValuePattern? _value;
        private EditorSnapshot? _before;

        public EditorSnapshot? ObserveEligibleTarget()
        {
            var foreground = GetForegroundWindow();
            var captured = CaptureStable(CaptureMode.WholeControl, foreground);
            if (captured.Status != CaptureStatus.Captured) return null;
            var element = AutomationElement.FocusedElement;
            if (element is null || string.Join(",", element.GetRuntimeId()) != captured.Snapshot!.ElementRuntimeId ||
                !IsSafeSingleLineEdit(element, captured.Snapshot.WindowHandle) ||
                !element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ||
                pattern is not ValuePattern value || value.Current.IsReadOnly) return null;
            var final = CaptureOnce(CaptureMode.WholeControl, foreground);
            if (final.Status != CaptureStatus.Captured ||
                !EditorSnapshotValidator.Matches(captured.Snapshot, final.Snapshot!) ||
                !IsSafeSingleLineEdit(element, captured.Snapshot.WindowHandle)) return null;
            _value = value;
            return _before = final.Snapshot;
        }

        public void SetValue(string text) => _value!.SetValue(text);

        public bool ConfirmValue(string text)
        {
            var element = AutomationElement.FocusedElement;
            return _before is not null && element is not null &&
                GetForegroundWindow() == _before.WindowHandle &&
                element.Current.ProcessId == _before.ProcessId &&
                string.Join(",", element.GetRuntimeId()) == _before.ElementRuntimeId &&
                _value!.Current.Value == text;
        }
    }

    private static bool IsSafeSingleLineEdit(AutomationElement element, nint foreground)
    {
        var handle = (nint)element.Current.NativeWindowHandle;
        if (handle == 0) return false;
        var thread = GetWindowThreadProcessId(handle, out var pid);
        if (thread == 0 || pid != (uint)element.Current.ProcessId) return false;
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(thread, ref info) || info.Focus != handle || GetForegroundWindow() != foreground)
            return false;
        var name = new System.Text.StringBuilder(128);
        if (GetClassName(handle, name, name.Capacity) == 0 || name.ToString() != "Edit") return false;
        var style = GetWindowLong(handle, -16);
        if ((style & (0x0004 | 0x0020 | 0x0800)) != 0) return false; // multiline, password, read-only
        var layout = GetKeyboardLayout(thread);
        // Only a plain English keyboard layout is accepted; IME/TSF composition otherwise is unknown.
        if (((long)layout & 0x3ff) != 0x09 || ImmIsIME(layout)) return false;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    public void Dispose() => _worker.Dispose();

    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, System.Text.StringBuilder name, int count);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint thread);
    [DllImport("imm32.dll")] private static extern bool ImmIsIME(nint layout);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
