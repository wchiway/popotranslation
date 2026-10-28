namespace PopoTranslation.Windows.Editing;

// The backend is called synchronously on the automation thread. Implementations must
// fail closed if they cannot establish the target's identity and editability.
public interface IReplacementBackend
{
    EditorSnapshot? ObserveEligibleTarget();
    void SetValue(string text);
    bool ConfirmValue(string text);
}

public static class EditorReplacer
{
    public static ReplacementResult Replace(EditorSnapshot snapshot, string translatedText,
        IReplacementBackend backend, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(translatedText);
        ArgumentNullException.ThrowIfNull(backend);
        if (snapshot.Mode != CaptureMode.WholeControl || string.IsNullOrWhiteSpace(translatedText) ||
            translatedText.IndexOfAny(['\r', '\n', '\0']) >= 0)
            return new(ReplacementStatus.ManualApply, "仅支持单行输入框的非空整框替换。");
        var submitted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = backend.ObserveEligibleTarget();
            if (current is null || !EditorSnapshotValidator.Matches(snapshot, current))
                return new(ReplacementStatus.ManualApply, "焦点、文本、选区或控件能力已变化。");
            cancellationToken.ThrowIfCancellationRequested();
            submitted = true;
            backend.SetValue(translatedText);
            try
            {
                return backend.ConfirmValue(translatedText)
                    ? new(ReplacementStatus.Applied)
                    : new(ReplacementStatus.SubmittedUnconfirmed, "已提交替换，但无法确认控件最终内容。");
            }
            catch { return new(ReplacementStatus.SubmittedUnconfirmed, "已提交替换，但无法确认控件最终内容。"); }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return submitted
                ? new(ReplacementStatus.SubmittedUnconfirmed, "操作失败或无法确认，请检查目标控件。")
                : new(ReplacementStatus.ManualApply, "无法确认目标控件，请手动应用。");
        }
    }
}
