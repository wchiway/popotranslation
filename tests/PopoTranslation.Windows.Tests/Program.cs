using PopoTranslation.Windows.Automation;
using PopoTranslation.Windows.Editing;
using PopoTranslation.Windows.Hotkeys;
using PopoTranslation.Windows.Messaging;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var snapshot = new EditorSnapshot((nint)7, 10, 15, "1,2,3", "重复重复", 0, 2, CaptureMode.Selection);
Check(snapshot.SourceText == "重复", "选区读取错误");
Check(EditorSnapshotValidator.Matches(snapshot, snapshot), "相同快照必须匹配");
Check(!EditorSnapshotValidator.Matches(snapshot, snapshot with { SelectionStart = 2, SelectionEnd = 4 }), "相同文字的不同选区不得匹配");
Check(!EditorSnapshotValidator.Matches(snapshot, snapshot with { ProcessStartTimeUtcTicks = 16 }), "PID 复用不得匹配");
Check(!EditorSnapshotValidator.Matches(snapshot, snapshot with { ElementRuntimeId = "1,2,4" }), "不同控件不得匹配");
Check(!EditorSnapshotValidator.Matches(snapshot, snapshot with { DocumentText = "重复重复!" }), "文本变化不得匹配");
Check(!EditorSnapshotValidator.Matches(snapshot, snapshot with { WindowHandle = (nint)8 }), "窗口变化不得匹配");
var whole = snapshot with { Mode = CaptureMode.WholeControl };
var backend = new FakeReplacementBackend { Current = whole };
Check(EditorReplacer.Replace(snapshot, "译文", backend).Status == ReplacementStatus.ManualApply && backend.Writes == 0,
    "选区不可自动替换");
backend.Current = whole with { SelectionStart = 2, SelectionEnd = 4 };
Check(EditorReplacer.Replace(whole, "译文", backend).Status == ReplacementStatus.ManualApply && backend.Writes == 0,
    "选区变化不可写入");
backend.Current = whole with { DocumentText = "新文本" };
Check(EditorReplacer.Replace(whole, "译文", backend).Status == ReplacementStatus.ManualApply && backend.Writes == 0,
    "文本变化不可写入");
backend.Current = whole;
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try { EditorReplacer.Replace(whole, "译文", backend, cancelled.Token); throw new InvalidOperationException("应取消"); }
    catch (OperationCanceledException) { }
    Check(backend.Writes == 0, "取消的任务不可写入");
}
using (var cancelledDuringObservation = new CancellationTokenSource())
{
    backend.OnObserve = cancelledDuringObservation.Cancel;
    try { EditorReplacer.Replace(whole, "译文", backend, cancelledDuringObservation.Token); throw new InvalidOperationException("应取消"); }
    catch (OperationCanceledException) { }
    backend.OnObserve = null;
    Check(backend.Writes == 0, "最终验证期间取消不可写入");
}
Check(EditorReplacer.Replace(whole, "译文\n后续", backend).Status == ReplacementStatus.ManualApply && backend.Writes == 0,
    "多行译文不可写入单行框");
Check(EditorReplacer.Replace(whole, "译文", backend).Status == ReplacementStatus.Applied && backend.Writes == 1,
    "一致时应提交且确认");
backend.Confirmed = false;
Check(EditorReplacer.Replace(whole, "译文", backend).Status == ReplacementStatus.SubmittedUnconfirmed,
    "写入未确认时不得报告成功");
backend.ThrowOnWrite = true;
Check(EditorReplacer.Replace(whole, "译文", backend).Status == ReplacementStatus.SubmittedUnconfirmed,
    "写入抛错不能假定未发生写入");
using (var reader = new FocusedEditorReader())
{
    Check((await reader.CaptureAsync(CaptureMode.Selection, 0)).Status == CaptureStatus.NoFocusedEditor,
        "缺失触发时的前台句柄不得取文");
    Check((await reader.CaptureAsync(CaptureMode.Selection, (nint)(-1))).Status == CaptureStatus.NoFocusedEditor,
        "前台句柄不匹配不得取文");
    var result = await reader.ReplaceAsync(whole, "译文", _ => new(ReplacementStatus.ManualApply));
    Check(result.Status == ReplacementStatus.ManualApply, "任务失效时不得回填");
}

using (var window = new NativeMessageWindow())
{
    var native = new FakeHotkeyNative();
    using var hotkeys = new GlobalHotkeyService(window, native);
    var original = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x54);
    Check(hotkeys.Configure(HotkeyAction.Selection, original).Success, "初始注册失败");
    var originalId = native.Active.Single();
    native.Fail = true;
    Check(!hotkeys.Configure(HotkeyAction.Selection, new(HotkeyModifiers.Alt, 0x54)).Success, "冲突必须报告失败");
    Check(native.Active.SetEquals([originalId]), "冲突时必须保留原注册");
    native.Fail = false;
    Check(hotkeys.Configure(HotkeyAction.Selection, new(HotkeyModifiers.Alt, 0x54)).Success, "重新注册失败");
    Check(native.Active.Count == 1 && !native.Active.Contains(originalId), "成功更新后必须释放旧注册");
    var other = new Hotkey(HotkeyModifiers.Control, 0x59);
    Check(hotkeys.ConfigurePair(original, other).Success, "成对注册失败");
    var ids = native.Active.ToHashSet();
    Check(hotkeys.ConfigurePair(other, original).Success && native.Active.SetEquals(ids), "互换应复用注册");
    native.FailKey = 0x5A;
    Check(!hotkeys.ConfigurePair(new(HotkeyModifiers.Alt, 0x58), new(HotkeyModifiers.Alt, 0x5A)).Success,
        "第二键冲突必须失败");
    Check(native.Active.SetEquals(ids), "第二键冲突必须保留之前组合并撤回新增注册");
    Check(hotkeys.Clear() && native.Active.Count == 0, "清除必须释放全部注册");
    Check(hotkeys.ConfigurePair(original, other).Success, "清除后应允许重新注册");
    hotkeys.Dispose();
    Check(native.Active.Count == 0, "退出时必须释放注册");
}
Console.WriteLine("Windows 安全快照与快捷键检查通过。");

sealed class FakeReplacementBackend : IReplacementBackend
{
    public EditorSnapshot? Current { get; set; }
    public int Writes { get; private set; }
    public bool Confirmed { get; set; } = true;
    public bool ThrowOnWrite { get; set; }
    public Action? OnObserve { get; set; }
    public EditorSnapshot? ObserveEligibleTarget() { OnObserve?.Invoke(); return Current; }
    public void SetValue(string text)
    {
        Writes++;
        if (ThrowOnWrite) throw new InvalidOperationException();
    }
    public bool ConfirmValue(string text) => Confirmed;
}

sealed class FakeHotkeyNative : IHotkeyNative
{
    public HashSet<int> Active { get; } = [];
    public bool Fail { get; set; }
    public uint FailKey { get; set; }
    public bool Register(nint window, int id, uint modifiers, uint key, out int error)
    {
        var fail = Fail || key == FailKey;
        error = fail ? 1409 : 0;
        if (fail) return false;
        Active.Add(id);
        return true;
    }
    public bool Unregister(nint window, int id) => Active.Remove(id);
}
