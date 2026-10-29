using PopoTranslation.Windows.Automation;
using PopoTranslation.Windows.Clipboard;
using PopoTranslation.Windows.Editing;
using PopoTranslation.Windows.Hotkeys;
using PopoTranslation.Windows.Messaging;
using PopoTranslation.Windows.Storage;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var clipboardAttempts = 0;
var backup = await ClipboardService.TryReadClipboardAsync(() => 7, () =>
{
    if (++clipboardAttempts == 1) throw new System.Runtime.InteropServices.COMException("busy", unchecked((int)0x800401D0));
    return null;
}, () => 0, CancellationToken.None);
Check(clipboardAttempts == 2 && backup is { Data: null, Sequence: 7 }, "占用后应重试并识别稳定的空剪贴板");
Check(await ClipboardService.TryReadClipboardAsync(() => 7, () => null, () => 1, CancellationToken.None) is null,
    "有格式时不得将 null 数据视为空剪贴板");
var sequenceCalls = 0;
Check(await ClipboardService.TryReadClipboardAsync(() => (uint)++sequenceCalls, () => null, () => 0, CancellationToken.None) is null,
    "序列不稳定时不得备份");
clipboardAttempts = 0;
Check(await ClipboardService.TryReadClipboardAsync(() => 7, () =>
{
    clipboardAttempts++;
    throw new System.Runtime.InteropServices.COMException("busy", unchecked((int)0x800401D0));
}, () => 0, CancellationToken.None) is null && clipboardAttempts == 2, "读取占用重试必须有界");
var restores = 0;
await ClipboardService.RestoreClipboardAsync(backup!, "临时", () => 9, () => "临时", _ =>
{
    restores++;
    throw new System.Runtime.InteropServices.COMException("busy", unchecked((int)0x800401D0));
});
Check(restores == 2, "恢复失败应有界重试");
restores = 0;
await ClipboardService.RestoreClipboardAsync(backup!, "临时", () => 9, () => "其他", _ => restores++);
Check(restores == 0, "剪贴板被更改后不得恢复");
await ClipboardService.RestoreClipboardAsync(backup!, "临时", () => 9, () => "临时", data => { Check(data is null, "空备份必须恢复为空"); restores++; });
Check(restores == 1, "匹配时应恢复空剪贴板");

var pathTestRoot = Path.Combine(Path.GetTempPath(), $"popo-paths-{Guid.NewGuid():N}");
try
{
    var profile = Path.Combine(pathTestRoot, "profile");
    var local = Path.Combine(pathTestRoot, "local");
    var home = Path.Combine(profile, ".PopoTranslation");
    var legacy = Path.Combine(local, "PopoTranslation");
    var paths = new ApplicationPaths(profile, local);
    Check(paths.DataDirectory == home && paths.LogsDirectory == Path.Combine(home, "logs"), "首次使用应选择用户主目录");
    Check(!Directory.Exists(home), "解析路径不应创建目录");
    Directory.CreateDirectory(Path.Combine(legacy, "logs"));
    File.WriteAllText(Path.Combine(legacy, "logs", "old.log"), "old");
    paths = new ApplicationPaths(profile, local);
    Check(paths.DataDirectory == legacy && paths.LogsDirectory == Path.Combine(legacy, "logs"), "旧目录仅含日志时仍需保留");
    Directory.CreateDirectory(home);
    File.WriteAllText(Path.Combine(home, "settings.json"), "new");
    File.WriteAllText(Path.Combine(legacy, "settings.json"), "old");
    paths = new ApplicationPaths(profile, local);
    Check(paths.DataDirectory == legacy, "两个目录同时存在时旧数据优先");
    Check(File.ReadAllText(Path.Combine(home, "settings.json")) == "new" &&
          File.ReadAllText(Path.Combine(legacy, "settings.json")) == "old", "解析路径不得迁移或覆盖数据");
}
finally
{
    if (Directory.Exists(pathTestRoot)) Directory.Delete(pathTestRoot, recursive: true);
}

Check(FocusedEditorReader.IsProtectedPasswordProperty(true, allowUnknownPassword: true) &&
      FocusedEditorReader.IsProtectedPasswordProperty(true, allowUnknownPassword: false),
    "显式受保护控件必须拒绝读取，包括兼容目标的 Pane");
Check(!FocusedEditorReader.IsProtectedPasswordProperty(false, allowUnknownPassword: false) &&
      FocusedEditorReader.IsProtectedPasswordProperty(null, allowUnknownPassword: false) &&
      !FocusedEditorReader.IsProtectedPasswordProperty(null, allowUnknownPassword: true),
    "未受保护与未知属性应遵守目标兼容策略");
var snapshot = new EditorSnapshot((nint)7, 10, 15, "1,2,3", "重复重复", 0, 2, CaptureMode.Selection);
Check(snapshot.SourceText == "重复", "选区读取错误");
Check(EditorSnapshotValidator.Matches(snapshot, snapshot), "相同快照必须匹配");
Check(!EditorSnapshotValidator.Matches(snapshot, snapshot with { SelectionStart = 2, SelectionEnd = 4 }), "相同文字的不同选区不得匹配");
Check(!EditorSnapshotValidator.Matches(snapshot, snapshot with { ProcessStartTimeUtcTicks = 16 }), "PID 复用不得匹配");
Check(!EditorSnapshotValidator.Matches(snapshot, snapshot with { ElementRuntimeId = "1,2,4" }), "不同控件不得匹配");
Check(!EditorSnapshotValidator.Matches(snapshot, snapshot with { DocumentText = "重复重复!" }), "文本变化不得匹配");
Check(!EditorSnapshotValidator.Matches(snapshot, snapshot with { WindowHandle = (nint)8 }), "窗口变化不得匹配");
var clipboardCaptureCalls = 0;
var uiaCaptureCalls = 0;
EditorSnapshot? ObserveClipboard(EditorSnapshot original, Func<bool> processMatches, CaptureResult uia, string? clipboardText) =>
    FocusedEditorReader.ObserveClipboardTarget(original, processMatches,
        () => { uiaCaptureCalls++; return uia; },
        () => { clipboardCaptureCalls++; return clipboardText; });
var capturedSnapshot = new CaptureResult(CaptureStatus.Captured, snapshot);
Check(ObserveClipboard(snapshot, () => true, capturedSnapshot, "重复") == snapshot &&
      uiaCaptureCalls == 1 && clipboardCaptureCalls == 0, "UIA 快照须使用 UIA 重捕获而非仅比较剪贴板选中文字");
foreach (var changed in new[]
{
    snapshot with { SelectionStart = 2, SelectionEnd = 4 },
    snapshot with { DocumentText = "重复重复!" },
    snapshot with { ElementRuntimeId = "1,2,4" },
    snapshot with { ProcessStartTimeUtcTicks = 16 }
})
    Check(ObserveClipboard(snapshot, () => true, new(CaptureStatus.Captured, changed), "重复") is null,
        "UIA 身份、全文或选区发生变化时不得写入");
Check(ObserveClipboard(snapshot, () => true, new(CaptureStatus.Unavailable), "重复") is null,
    "无法稳定重捕获 UIA 时不得降级为剪贴板文字验证");
var processChecks = 0;
Check(ObserveClipboard(snapshot, () => ++processChecks == 1, capturedSnapshot, "重复") is null && processChecks == 2,
    "观察期间进程身份变化不得写入");
Check(ObserveClipboard(snapshot, () => false, capturedSnapshot, "重复") is null,
    "进程不一致或启动时间不可得不得写入");
var clipboardSnapshot = snapshot with { Method = CaptureMethod.Clipboard, ElementRuntimeId = "clipboard", DocumentText = "重复", SelectionEnd = 2 };
Check(ObserveClipboard(clipboardSnapshot, () => true, new(CaptureStatus.Unavailable), "重复") == clipboardSnapshot &&
      ObserveClipboard(clipboardSnapshot, () => true, capturedSnapshot, "其他") is null,
    "剪贴板快照应保留现有的文字核验能力");
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
var taskCurrent = true;
backend.OnObserve = () => taskCurrent = false;
Check(EditorReplacer.Replace(whole, "译文", backend, isCurrent: () => taskCurrent).Status == ReplacementStatus.ManualApply && backend.Writes == 0,
    "最终观察期间任务失效不可写入");
backend.OnObserve = null;
Check(EditorReplacer.Replace(whole, "译文\n后续", backend).Status == ReplacementStatus.ManualApply && backend.Writes == 0,
    "多行译文不可写入单行框");
var flexible = new FakeReplacementBackend { Current = snapshot, SupportsSelection = true, SupportsMultiline = true };
Check(EditorReplacer.Replace(snapshot, "译文\n后续", flexible).Status == ReplacementStatus.Applied,
    "支持选区和多行的兼容后端应允许写入");
flexible.Current = whole;
flexible.Confirmed = true;
Check(EditorReplacer.Replace(whole, "译文\n后续", flexible).Status == ReplacementStatus.Applied,
    "多行兼容后端应允许整框写入");
Check(EditorReplacer.Replace(whole, "译文", backend).Status == ReplacementStatus.Applied && backend.Writes == 1,
    "一致时应提交且确认");
backend.Confirmed = false;
Check(EditorReplacer.Replace(whole, "译文", backend).Status == ReplacementStatus.SubmittedUnconfirmed,
    "写入未确认时不得报告成功");
var skippedConfirmations = backend.Confirmations;
var skippedResult = EditorReplacer.Replace(whole, "译文", backend, confirmAfterReplacement: false);
Check(skippedResult.Status == ReplacementStatus.SubmittedUnconfirmed &&
      skippedResult.Reason!.Contains("未检查") && backend.Confirmations == skippedConfirmations,
    "关闭确认应跳过读取并准确报告已提交但未检查");
var writesBeforeSafetyChecks = backend.Writes;
foreach (var changed in new[]
{
    whole with { WindowHandle = (nint)8 },
    whole with { DocumentText = "变化" },
    whole with { SelectionStart = 2, SelectionEnd = 4 }
})
{
    backend.Current = changed;
    Check(EditorReplacer.Replace(whole, "译文", backend, confirmAfterReplacement: false).Status == ReplacementStatus.ManualApply &&
          backend.Writes == writesBeforeSafetyChecks, "关闭确认仍需验证目标、原文及选区");
}
backend.Current = whole;
Check(EditorReplacer.Replace(whole, "译文", backend, isCurrent: () => false, confirmAfterReplacement: false).Status == ReplacementStatus.ManualApply &&
      backend.Writes == writesBeforeSafetyChecks, "关闭确认时失效任务仍不得写入");
backend.Submission = ReplacementSubmission.NotSubmitted;
var confirmsBefore = backend.Confirmations;
Check(EditorReplacer.Replace(whole, "译文", backend).Status == ReplacementStatus.ManualApply && backend.Confirmations == confirmsBefore,
    "明确未发送时应允许手动应用且不检查写入结果");
backend.Submission = ReplacementSubmission.Unknown;
Check(EditorReplacer.Replace(whole, "译文", backend).Status == ReplacementStatus.OutcomeUnknown && backend.Confirmations == confirmsBefore,
    "发送情况不确定时不得报告已提交或安全手动应用");
Check(EditorReplacer.Replace(whole, "译文", backend, confirmAfterReplacement: false).Status == ReplacementStatus.OutcomeUnknown && backend.Confirmations == confirmsBefore,
    "关闭确认时发送情况不确定仍不可报告已提交");
backend.Submission = ReplacementSubmission.NotSubmitted;
Check(EditorReplacer.Replace(whole, "译文", backend, confirmAfterReplacement: false).Status == ReplacementStatus.ManualApply && backend.Confirmations == confirmsBefore,
    "关闭确认时未发送仍需报告手动应用");
Check(ClipboardPasteSubmission.FromClipboardStatus(ClipboardPasteStatus.NotSubmitted) == ReplacementSubmission.NotSubmitted &&
      ClipboardPasteSubmission.FromClipboardStatus(ClipboardPasteStatus.Unknown) == ReplacementSubmission.Unknown &&
      ClipboardPasteSubmission.FromClipboardStatus(ClipboardPasteStatus.Submitted) == ReplacementSubmission.Submitted,
    "剪贴板粘贴状态必须保守映射");
Check(ClipboardPasteSubmission.TextMatches("第一行\r\n第二行\n", "第一行\n第二行\n"),
    "回填确认应兼容 CRLF 和 LF");
Check(!ClipboardPasteSubmission.TextMatches("第一行\n第二行", "第一行\n第二行\n"),
    "回填确认不得忽略末尾换行丢失");
backend.ThrowOnWrite = true;
Check(EditorReplacer.Replace(whole, "译文", backend).Status == ReplacementStatus.OutcomeUnknown,
    "写入抛错不能假定已发送或未发生写入");
backend.CancelOnWrite = true;
Check(EditorReplacer.Replace(whole, "译文", backend).Status == ReplacementStatus.OutcomeUnknown,
    "写入期间取消不能假定未发生写入");
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

var detector = new TripleSpaceDetector(500);
var triggers = 0;
bool Accepted() { triggers++; return true; }
bool Key(TripleSpaceDetector d, uint key, bool down, long time, bool eligible = true, bool modifiers = false,
    bool injected = false, Func<bool>? trigger = null) =>
    d.Process(key, down, time, eligible, modifiers, injected, trigger ?? Accepted);
const uint space = TripleSpaceDetector.SpaceVirtualKey;
Check(!Key(detector, space, true, 1000) && !Key(detector, space, false, 1010), "第一击必须透传");
Check(!Key(detector, space, true, 1020) && !Key(detector, space, false, 1030), "第二击必须透传");
Check(Key(detector, space, true, 1040) && Key(detector, space, true, 1050), "第三击和自动重复必须消耗");
Check(Key(detector, space, false, 1060), "被消耗的按下必须配对消耗松开");
Check(Key(detector, space, true, 1100) && Key(detector, space, false, 1110), "第四击必须消耗");
Check(!Key(detector, 0x41, true, 1120) && !Key(detector, 0x41, false, 1130), "无关键必须透传且不能解除锁定");
Check(Key(detector, space, true, 1140) && Key(detector, space, false, 1150), "第五击必须消耗");
Check(Key(detector, space, true, 1640) && Key(detector, space, false, 1650), "恰好间隔边界仍属连按");
Check(triggers == 1, "连按只能触发一次");
Check(!Key(detector, space, true, 2141) && !Key(detector, space, false, 2150), "超过间隔后必须恢复正常输入");
Check(!Key(detector, space, true, 2160) && !Key(detector, space, false, 2170), "重置后的第二击透传");
Check(Key(detector, space, true, 2180) && Key(detector, space, false, 2190) && triggers == 2, "重置后可以再次触发");

var slow = new TripleSpaceDetector(500);
Check(!Key(slow, space, true, 1000) && !Key(slow, space, false, 1010), "首次空格透传");
Check(!Key(slow, space, true, 2000) && !Key(slow, space, false, 2010), "超时后重新计数");
Check(!Key(slow, space, true, 2100) && !Key(slow, space, false, 2110), "超时后的第二击透传");
Check(!Key(slow, 0x41, true, 2120), "非空格按下重置未完成序列");
Check(!Key(slow, space, true, 2130) && !Key(slow, space, false, 2140), "重置后重新计数");

var rejected = new TripleSpaceDetector(500);
for (var i = 0; i < 6; i++)
{
    Check(!Key(rejected, space, true, 3000 + i * 20, trigger: () => false), "未接受的触发及后续空格均透传");
    Check(!Key(rejected, space, false, 3010 + i * 20), "未接受的空格松开透传");
}
var excluded = new TripleSpaceDetector(500);
for (var i = 0; i < 4; i++)
{
    Check(!Key(excluded, space, true, 4000 + i * 20, eligible: false), "终端或自身窗口空格透传");
    Check(!Key(excluded, space, false, 4010 + i * 20), "终端或自身窗口松开透传");
}
Check(!Key(excluded, space, true, 4100, modifiers: true) && !Key(excluded, space, false, 4110), "组合键空格透传");
Check(!Key(excluded, space, true, 4120, injected: true) && !Key(excluded, space, false, 4130, injected: true), "注入空格透传且不参与检测");
Check(!Key(excluded, space, true, 4140) && !Key(excluded, space, false, 4150), "排除的按键不能累积计数");

var paired = new TripleSpaceDetector(500);
for (var i = 0; i < 2; i++) { Key(paired, space, true, 5000 + i * 20); Key(paired, space, false, 5010 + i * 20); }
Check(Key(paired, space, true, 5040), "接受第三击");
Check(!Key(paired, 0x10, true, 5050, modifiers: true), "修饰键透传");
Check(Key(paired, space, false, 5060, eligible: false), "前台或修饰键变化时仍配对消耗松开");
Check(!Key(paired, space, true, 5070, eligible: false) && !Key(paired, space, false, 5080), "切换到终端后空格透传并解除锁定");

Check(TargetApplication.ClassifyProcessName("QQ") == TargetKind.Social, "QQ 应走社交兼容路径");
Check(TargetApplication.ClassifyProcessName("Weixin.exe") == TargetKind.Social, "微信应走社交兼容路径");
Check(TargetApplication.ClassifyProcessName("Discord") == TargetKind.Social, "Discord 应走社交兼容路径");
Check(TargetApplication.ClassifyProcessName("TelegramDesktop") == TargetKind.Social, "Telegram 应走社交兼容路径");
Check(TargetApplication.ClassifyProcessName("WindowsTerminal") == TargetKind.Terminal, "Windows Terminal 应识别为终端");
Check(TargetApplication.ClassifyProcessName("notepad") == TargetKind.TextEditor, "记事本应允许剪贴板兼容读取");
Check(TargetApplication.ClassifyProcessName("chrome") == TargetKind.Standard, "浏览器不属于自动剪贴板兼容目标");
Console.WriteLine("Windows 安全快照、快捷键与目标识别检查通过。");

sealed class FakeReplacementBackend : IReplacementBackend
{
    public EditorSnapshot? Current { get; set; }
    public bool SupportsSelection { get; set; }
    public bool SupportsMultiline { get; set; }
    public int Writes { get; private set; }
    public bool Confirmed { get; set; } = true;
    public bool ThrowOnWrite { get; set; }
    public bool CancelOnWrite { get; set; }
    public ReplacementSubmission Submission { get; set; } = ReplacementSubmission.Submitted;
    public int Confirmations { get; private set; }
    public Action? OnObserve { get; set; }
    public EditorSnapshot? ObserveEligibleTarget() { OnObserve?.Invoke(); return Current; }
    public ReplacementSubmission SetValue(string text)
    {
        Writes++;
        if (CancelOnWrite) throw new OperationCanceledException();
        if (ThrowOnWrite) throw new InvalidOperationException();
        return Submission;
    }
    public bool ConfirmValue(string text) { Confirmations++; return Confirmed; }
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
