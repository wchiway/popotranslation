namespace PopoTranslation.Windows.Editing;

public enum CaptureMode { Selection, WholeControl }

public enum CaptureStatus
{
    Captured,
    NoFocusedEditor,
    Protected,
    ReadOnly,
    Unsupported,
    Empty,
    Unavailable,
    Busy
}

// Text and selection offsets are UTF-16 offsets within the same TextPattern document.
public sealed record EditorSnapshot(
    nint WindowHandle,
    int ProcessId,
    long ProcessStartTimeUtcTicks,
    string ElementRuntimeId,
    string DocumentText,
    int SelectionStart,
    int SelectionEnd,
    CaptureMode Mode)
{
    public string SourceText => Mode == CaptureMode.Selection
        ? DocumentText[SelectionStart..SelectionEnd]
        : DocumentText;
}

public sealed record CaptureResult(CaptureStatus Status, EditorSnapshot? Snapshot = null);

public enum ReplacementStatus { Applied, SubmittedUnconfirmed, ManualApply, Failed }
public sealed record ReplacementResult(ReplacementStatus Status, string? Reason = null);

public interface IFocusedEditor
{
    Task<CaptureResult> CaptureAsync(CaptureMode mode, nint expectedForegroundHwnd, CancellationToken cancellationToken = default);
    Task<ReplacementResult> ReplaceAsync(EditorSnapshot snapshot, string translatedText,
        Func<Func<ReplacementResult>, ReplacementResult> executeIfCurrent,
        CancellationToken cancellationToken = default);
}
