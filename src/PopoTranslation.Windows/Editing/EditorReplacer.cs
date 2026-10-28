namespace PopoTranslation.Windows.Editing;

// The backend is called synchronously on the automation thread. Implementations must
// fail closed if they cannot establish the target's identity and editability.
public interface IReplacementBackend
{
    bool SupportsSelection => false;
    bool SupportsMultiline => false;
    EditorSnapshot? ObserveEligibleTarget();
    void SetValue(string text);
    bool ConfirmValue(string text);
}

public static class EditorReplacer
{
    public static ReplacementResult Replace(EditorSnapshot snapshot, string translatedText,
        IReplacementBackend backend, CancellationToken cancellationToken = default, Func<bool>? isCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(translatedText);
        ArgumentNullException.ThrowIfNull(backend);
        if ((!backend.SupportsSelection && snapshot.Mode != CaptureMode.WholeControl) ||
            string.IsNullOrWhiteSpace(translatedText) ||
            (!backend.SupportsMultiline && translatedText.IndexOfAny(['\r', '\n', '\0']) >= 0))
            return new(ReplacementStatus.ManualApply, "当前控件或译文不支持自动替换，请手动应用。");
        var submitted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = backend.ObserveEligibleTarget();
            if (current is null || !EditorSnapshotValidator.Matches(snapshot, current))
                return new(ReplacementStatus.ManualApply, "焦点、文本、选区或控件能力已变化。");
            cancellationToken.ThrowIfCancellationRequested();
            if (isCurrent is not null && !isCurrent())
                return new(ReplacementStatus.ManualApply, "翻译任务已失效。");
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
