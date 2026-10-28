using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using popotranslation.Views;
using PopoTranslation.Core;
using PopoTranslation.Infrastructure.Secrets;
using PopoTranslation.Infrastructure.Settings;
using PopoTranslation.Providers;
using PopoTranslation.Windows.Automation;
using PopoTranslation.Windows.Editing;
using PopoTranslation.Windows.Diagnostics;
using PopoTranslation.Windows.Hotkeys;
using PopoTranslation.Windows.Messaging;
using PopoTranslation.Windows.Tray;

namespace popotranslation.Services;

internal sealed class ApplicationController : IDisposable
{
    private const string DefaultSelectionHotkey = "Ctrl+Alt+F9";
    private const string DefaultWholeHotkey = "Ctrl+Alt+F10";
    private readonly SettingsPage _page;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _openSettings;
    private readonly Action _exit;
    private readonly string _iconPath;
    private readonly JsonSettingsStore _settingsStore;
    private readonly JsonApplicationPreferencesStore _preferencesStore;
    private readonly SemaphoreSlim _preferencesGate = new(1, 1);
    private ApplicationPreferences _preferences = new();
    private volatile bool _notificationsEnabled = true;
    private bool _preferencesUnavailable;
    private long _preferencesVersion;
    private readonly WindowsSecretStore _secrets;
    private readonly TranslationCoordinator _coordinator = new();
    private readonly object _startGate = new();
    private readonly FocusedEditorReader _editor = new();
    private NativeMessageWindow? _native;
    private GlobalHotkeyService? _hotkeys;
    private SpaceSequenceHotkeyService? _spaceHotkey;
    private TrayIconService? _tray;
    private volatile AppSettings? _settings;
    private CancellationTokenSource? _pending;
    private long _generation;
    private volatile bool _paused;
    private volatile bool _disposed;
    private volatile bool _saving;
    private bool _settingsUnavailable;
    private bool _spaceHotkeyUnavailable;
    private volatile bool _initialized;

    public bool CanHide => _tray is not null;

    public ApplicationController(SettingsPage page, DispatcherQueue dispatcher, Action openSettings, Action exit, string iconPath)
    {
        _page = page;
        _dispatcher = dispatcher;
        _openSettings = openSettings;
        _exit = exit;
        _iconPath = iconPath;
        var directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PopoTranslation");
        _settingsStore = new JsonSettingsStore(System.IO.Path.Combine(directory, "settings.json"));
        _preferencesStore = new JsonApplicationPreferencesStore(System.IO.Path.Combine(directory, "preferences.json"));
        _page.ApplicationSettingsChanged += OnApplicationSettingsChanged;
        _secrets = new WindowsSecretStore(System.IO.Path.Combine(directory, "secrets"));
        _page.SaveRequested = SaveAsync;
        _page.TestConnectionRequested = TestConnectionAsync;
        _page.SelectProfileRequested = SelectProfileAsync;
        _page.AddProfileRequested = AddProfileAsync;
        _page.DeleteProfileRequested = DeleteProfileAsync;
        _page.ApplyResultRequested = _ => Task.FromResult(new SettingsActionResult(false, "请复制译文并切换到目标输入框后手动粘贴。"));
    }

    public async Task InitializeAsync()
    {
        try
        {
            _preferences = await _preferencesStore.LoadAsync();
        }
        catch
        {
            _preferencesUnavailable = true;
        }
        if (_disposed) return;
        ApplyPreferences(_preferences);
        if (_preferencesUnavailable)
            _page.SetStatus("无法读取应用设置；现有文件未修改，请检查后重启。", true);

        try
        {
            _native = new NativeMessageWindow();
            _hotkeys = new GlobalHotkeyService(_native);
            _hotkeys.Triggered += OnHotkey;
            try
            {
                _spaceHotkey = new SpaceSequenceHotkeyService(_native);
                _spaceHotkey.Triggered += OnTripleSpace;
            }
            catch
            {
                _spaceHotkey?.Dispose();
                _spaceHotkey = null;
                _spaceHotkeyUnavailable = true;
            }
            _tray = new TrayIconService(_native, _iconPath);
            _tray.SetEnglish(_preferences.Language == InterfaceLanguage.English);
            _tray.ActionRequested += OnTrayAction;
        }
        catch
        {
            _tray?.Dispose();
            _tray = null;
            _spaceHotkey?.Dispose();
            _spaceHotkey = null;
            _hotkeys?.Dispose();
            _hotkeys = null;
            _native?.Dispose();
            _native = null;
            _page.SetStatus("无法启动托盘或快捷键服务，请检查桌面环境后重启。", true);
            return;
        }

        try
        {
            var settings = await _settingsStore.LoadAsync();
            if (_disposed) return;
            _settings = settings;
            if (settings is null)
            {
                var id = Guid.NewGuid().ToString("N");
                _page.LoadSettings(new SettingsForm(id, "", nameof(ApiProtocol.OpenAiChatCompletions), "", "", "", false, 30, null, "简体中文", null, DefaultSelectionHotkey, DefaultWholeHotkey));
                _page.SetProfiles([new ProfileOption(id, "新服务")], id);
                _initialized = true;
                if (_spaceHotkeyUnavailable)
                    _page.SetStatus("连续三次空格快捷键不可用，请使用备用快捷键。", true);
                return;
            }
            var profile = settings.Providers.FirstOrDefault(p => p.Id == settings.ActiveProviderId)
                ?? throw new InvalidOperationException();
            var hasKey = false;
            try { hasKey = !string.IsNullOrWhiteSpace(await _secrets.ReadAsync(profile.CredentialReference)); }
            catch { /* The key can be replaced without overwriting the settings file. */ }
            if (_disposed) return;
            var hasSecondary = await HasSecondaryAsync(profile);
            LoadProfileIntoPage(settings, profile, hasKey, hasSecondary);
            _initialized = true;
            if (!TryParseHotkey(settings.SelectionHotkey ?? DefaultSelectionHotkey, out var selectionKey) ||
                !TryParseHotkey(settings.WholeControlHotkey ?? DefaultWholeHotkey, out var wholeKey) ||
                !_hotkeys.ConfigurePair(selectionKey!, wholeKey!).Success)
                _page.SetStatus("无法注册保存的快捷键；请更换快捷键并重新保存。", true);
            else
            {
                _hasRegisteredHotkeys = true;
                if (!hasKey || (RequiresSecondary(profile.Protocol) && !hasSecondary))
                    _page.SetStatus("找不到已保存的凭据，请重新输入并保存。", true);
            }
            if (_spaceHotkeyUnavailable)
                _page.SetStatus("连续三次空格快捷键不可用，请使用备用快捷键。", true);
        }
        catch
        {
            _settingsUnavailable = true;
            _page.SetStatus("无法读取设置或密钥；现有文件未修改，请检查后重启。", true);
        }
    }

    private void RefreshProfileList()
    {
        var settings = _settings;
        if (settings is null) return;
        _page.SetProfiles(settings.Providers.Select(p => new ProfileOption(p.Id, p.Name)).ToArray(),
            settings.ActiveProviderId);
    }

    private async Task<bool> HasSecondaryAsync(ProviderProfile profile)
    {
        if (profile.SecondaryCredentialReference is null) return false;
        try { return !string.IsNullOrWhiteSpace(await _secrets.ReadAsync(profile.SecondaryCredentialReference)); }
        catch { return false; }
    }

    private void LoadProfileIntoPage(AppSettings settings, ProviderProfile profile, bool hasKey, bool hasSecondary)
    {
        _page.LoadSettings(new SettingsForm(profile.Id, profile.Name, profile.Protocol.ToString(),
            profile.BaseUrl, profile.ModelId, "", hasKey, profile.TimeoutSeconds, settings.SourceLanguage,
            settings.TargetLanguage, settings.Style, settings.SelectionHotkey ?? DefaultSelectionHotkey,
            settings.WholeControlHotkey ?? DefaultWholeHotkey, "", hasSecondary));
        _page.SetProfiles(settings.Providers.Select(p => new ProfileOption(p.Id, p.Name)).ToArray(), profile.Id);
    }

    private async Task<SettingsActionResult> SelectProfileAsync(string profileId)
    {
        if (_disposed) return new(false, "应用正在退出。");
        if (!_initialized) return new(false, "设置仍在加载，请稍后重试。");
        if (_saving) return new(false, "其他设置操作仍在进行。");
        var settings = _settings;
        var profile = settings?.Providers.FirstOrDefault(p => p.Id == profileId);
        if (settings is null || profile is null) return new(false, "找不到该服务配置。");
        if (settings.ActiveProviderId == profileId) return new(true, "当前已是该服务。");

        _saving = true;
        Invalidate();
        try
        {
            var next = settings with { ActiveProviderId = profileId };
            await _settingsStore.SaveAsync(next);
            if (_disposed) return new(false, "应用正在退出。");
            _settings = next;
            var hasKey = false;
            try { hasKey = !string.IsNullOrWhiteSpace(await _secrets.ReadAsync(profile.CredentialReference)); }
            catch { }
            var hasSecondary = await HasSecondaryAsync(profile);
            LoadProfileIntoPage(next, profile, hasKey, hasSecondary);
            var message = hasKey && (!RequiresSecondary(profile.Protocol) || hasSecondary)
                ? "已切换服务。" : "已切换服务；未找到凭据，请重新输入并保存。";
            if (IsMachine(profile.Protocol))
                message += "全局源语言和目标语言可能不受该服务支持，请检查并在需要时调整后保存。";
            return new(true, message);
        }
        catch { return new(false, "切换服务失败，原设置仍然有效。"); }
        finally { _saving = false; }
    }

    private Task<SettingsActionResult> AddProfileAsync()
    {
        if (_disposed) return Task.FromResult(new SettingsActionResult(false, "应用正在退出。"));
        if (!_initialized) return Task.FromResult(new SettingsActionResult(false, "设置仍在加载，请稍后重试。"));
        if (_saving) return Task.FromResult(new SettingsActionResult(false, "其他设置操作仍在进行。"));
        var settings = _settings;
        var active = settings?.Providers.FirstOrDefault(p => p.Id == settings.ActiveProviderId);
        var id = Guid.NewGuid().ToString("N");
        var form = new SettingsForm(id, "新服务", active?.Protocol.ToString() ?? nameof(ApiProtocol.OpenAiChatCompletions),
            "", "", "", false, active?.TimeoutSeconds ?? 30, settings?.SourceLanguage,
            settings?.TargetLanguage ?? "简体中文", settings?.Style,
            settings?.SelectionHotkey ?? DefaultSelectionHotkey,
            settings?.WholeControlHotkey ?? DefaultWholeHotkey);
        _page.LoadSettings(form);
        var options = (settings?.Providers.Select(p => new ProfileOption(p.Id, p.Name)) ?? [])
            .Append(new ProfileOption(id, "新服务")).ToArray();
        _page.SetProfiles(options, id);
        return Task.FromResult(new SettingsActionResult(true, "已添加新服务，请填写并保存。"));
    }

    private async Task<SettingsActionResult> DeleteProfileAsync(string profileId)
    {
        if (_disposed) return new(false, "应用正在退出。");
        if (!_initialized) return new(false, "设置仍在加载，请稍后重试。");
        if (_saving) return new(false, "其他设置操作仍在进行。");
        var settings = _settings;
        var profile = settings?.Providers.FirstOrDefault(p => p.Id == profileId);
        if (settings is null || profile is null) return new(false, "该服务尚未保存，无需删除。");
        var remaining = settings.Providers.Where(p => p.Id != profileId).ToArray();
        if (remaining.Length == 0) return new(false, "至少保留一个服务配置。");

        _saving = true;
        Invalidate();
        try
        {
            var next = settings with
            {
                Providers = remaining,
                ActiveProviderId = settings.ActiveProviderId == profileId ? remaining[0].Id : settings.ActiveProviderId
            };
            await _settingsStore.SaveAsync(next);
            if (_disposed) return new(false, "应用正在退出。");
            _settings = next;
            try { await _secrets.DeleteAsync(profile.CredentialReference); } catch { }
            if (profile.SecondaryCredentialReference is not null)
                try { await _secrets.DeleteAsync(profile.SecondaryCredentialReference); } catch { }
            var nextActive = remaining.FirstOrDefault(p => p.Id == next.ActiveProviderId) ?? remaining[0];
            var hasKey = false;
            try { hasKey = !string.IsNullOrWhiteSpace(await _secrets.ReadAsync(nextActive.CredentialReference)); }
            catch { }
            LoadProfileIntoPage(next, nextActive, hasKey, await HasSecondaryAsync(nextActive));
            return new(true, "已删除服务配置。");
        }
        catch { return new(false, "删除服务失败，原设置仍然有效。"); }
        finally { _saving = false; }
    }

    private async Task<SettingsActionResult> SaveAsync(SettingsForm form)
    {
        if (_settingsUnavailable) return new(false, "现有设置无法读取，为避免覆盖，请先恢复设置文件后重启。");
        if (!_initialized) return new(false, "设置仍在加载，请稍后重试。");
        if (_hotkeys is null) return new(false, "快捷键服务不可用，请重启应用。");
        if (!TryBuildProfile(form, out var profile, out var error) ||
            !TryParseHotkey(form.SelectionHotkey, out var selection) || !TryParseHotkey(form.WholeControlHotkey, out var whole))
            return new(false, error ?? "快捷键格式无效。");
        _saving = true;
        Invalidate();
        string? newReference = null;
        string? newSecondaryReference = null;
        var oldSelection = _settings?.SelectionHotkey ?? DefaultSelectionHotkey;
        var oldWhole = _settings?.WholeControlHotkey ?? DefaultWholeHotkey;
        var hadOldHotkeys = _hasRegisteredHotkeys;
        try
        {
            var previous = _settings?.Providers.FirstOrDefault(p => p.Id == form.ProfileId);
            var sameProtocol = previous?.Protocol == profile!.Protocol;
            var key = form.ApiKey;
            var secondary = form.SecondaryCredential;
            if (string.IsNullOrWhiteSpace(key) && sameProtocol &&
                ProviderCredentialPolicy.CanReuse(profile.Protocol, previous!.BaseUrl, profile.BaseUrl))
                key = await _secrets.ReadAsync(previous.CredentialReference) ?? "";
            if (RequiresSecondary(profile.Protocol) && string.IsNullOrWhiteSpace(secondary) && sameProtocol && previous!.SecondaryCredentialReference is not null)
                secondary = await _secrets.ReadAsync(previous.SecondaryCredentialReference) ?? "";
            if (string.IsNullOrWhiteSpace(key) || (RequiresSecondary(profile.Protocol) && string.IsNullOrWhiteSpace(secondary)))
                return new(false, "请输入完整的服务凭据。");
            if (!string.IsNullOrWhiteSpace(form.ApiKey))
            {
                newReference = Guid.NewGuid().ToString("N");
                profile = profile with { CredentialReference = newReference };
            }
            else profile = profile with { CredentialReference = previous!.CredentialReference };
            if (RequiresSecondary(profile.Protocol))
            {
                if (!string.IsNullOrWhiteSpace(form.SecondaryCredential))
                {
                    newSecondaryReference = Guid.NewGuid().ToString("N");
                    profile = profile with { SecondaryCredentialReference = newSecondaryReference };
                }
                else profile = profile with { SecondaryCredentialReference = previous!.SecondaryCredentialReference };
            }
            _ = await Task.Run(() => TranslationProviderFactory.Create(profile, key, secondary));
            if (newReference is not null) await _secrets.WriteAsync(newReference, key);
            if (newSecondaryReference is not null) await _secrets.WriteAsync(newSecondaryReference, secondary);
            if (_disposed) return new(false, "应用正在退出。");
            var registration = _hotkeys.ConfigurePair(selection!, whole!);
            _hasRegisteredHotkeys = registration.Success;
            var profiles = (_settings?.Providers ?? Array.Empty<ProviderProfile>()).Where(p => p.Id != profile!.Id).Append(profile!).ToArray();
            var next = new AppSettings(AppSettings.CurrentSchemaVersion, profiles, profile!.Id, form.TargetLanguage, form.SourceLanguage, form.Style, form.SelectionHotkey, form.WholeControlHotkey);
            try { await _settingsStore.SaveAsync(next); }
            catch
            {
                if (!_disposed && hadOldHotkeys && TryParseHotkey(oldSelection, out var oldSelectionKey) && TryParseHotkey(oldWhole, out var oldWholeKey))
                    _hasRegisteredHotkeys = _hotkeys.ConfigurePair(oldSelectionKey!, oldWholeKey!).Success;
                else if (!_disposed) { _hotkeys.Clear(); _hasRegisteredHotkeys = false; }
                return new(false, _hasRegisteredHotkeys || !hadOldHotkeys
                    ? "设置保存失败；原设置仍然有效。" : "设置保存失败，原快捷键也无法恢复；请重新保存设置。");
            }
            _settings = next;
            RefreshProfileList();
            if (previous is not null)
            {
                if (previous.CredentialReference != profile.CredentialReference)
                    try { await _secrets.DeleteAsync(previous.CredentialReference); } catch { }
                if (previous.SecondaryCredentialReference is not null && previous.SecondaryCredentialReference != profile.SecondaryCredentialReference)
                    try { await _secrets.DeleteAsync(previous.SecondaryCredentialReference); } catch { }
            }
            newReference = null;
            newSecondaryReference = null;
            return new(true, registration.Success
                ? "设置已保存。连接测试需要手动触发。"
                : "设置已保存；备用快捷键被占用，但仍可使用连续三次空格。");
        }
        catch { return new(false, "保存失败，请检查服务地址、密钥和快捷键。"); }
        finally
        {
            if (newReference is not null)
            {
                try { await _secrets.DeleteAsync(newReference); } catch { }
            }
            if (newSecondaryReference is not null)
            {
                try { await _secrets.DeleteAsync(newSecondaryReference); } catch { }
            }
            _saving = false;
        }
    }

    private volatile bool _hasRegisteredHotkeys;

    private async Task<SettingsActionResult> TestConnectionAsync(SettingsForm form)
    {
        if (!_initialized) return new(false, "设置仍在加载，请稍后重试。");
        if (!TryBuildProfile(form, out var profile, out var error)) return new(false, error!);
        try
        {
            var key = form.ApiKey;
            var secondary = form.SecondaryCredential;
            var previous = _settings?.Providers.FirstOrDefault(p => p.Id == form.ProfileId && p.Protocol == profile!.Protocol);
            if (string.IsNullOrWhiteSpace(key) && previous is not null &&
                ProviderCredentialPolicy.CanReuse(profile!.Protocol, previous.BaseUrl, profile.BaseUrl))
                key = await _secrets.ReadAsync(previous.CredentialReference) ?? "";
            if (RequiresSecondary(profile!.Protocol) && string.IsNullOrWhiteSpace(secondary) && previous?.SecondaryCredentialReference is not null)
                secondary = await _secrets.ReadAsync(previous.SecondaryCredentialReference) ?? "";
            if (string.IsNullOrWhiteSpace(key) || (RequiresSecondary(profile.Protocol) && string.IsNullOrWhiteSpace(secondary)))
                return new(false, "请输入完整的服务凭据。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(profile!.TimeoutSeconds));
            var result = await Task.Run(async () =>
            {
                var provider = TranslationProviderFactory.Create(profile, key, secondary);
                return await provider.TranslateAsync(new TranslationRequest("你好", "English", "简体中文", null), timeout.Token);
            }).WaitAsync(timeout.Token);
            return result.IsSuccessful ? new(true, "连接测试成功。") : new(false, ResultMessage(result));
        }
        catch (OperationCanceledException) { return new(false, "连接测试超时，请检查服务配置。"); }
        catch { return new(false, "连接测试失败，请检查服务配置和密钥。"); }
    }

    private void OnHotkey(HotkeyAction action)
    {
        var foreground = FocusedEditorReader.GetForegroundWindowAtTrigger();
        var generation = Interlocked.Increment(ref _generation);
        DiagnosticLog.Write($"event=hotkey action={action} generation={generation} foreground={foreground}");
        // Start capturing immediately on the native callback, before the UI dispatcher runs.
        // Waiting to dispatch first allows focus to move to another window or control.
        _ = CaptureThenTranslateAsync(action, foreground, generation);
    }

    private bool OnTripleSpace()
    {
        var settings = _settings;
        if (_disposed || _paused || _saving || !_initialized || settings?.Providers
            .FirstOrDefault(p => p.Id == settings.ActiveProviderId) is null)
            return false;
        OnHotkey(HotkeyAction.TripleSpace);
        return true;
    }

    private async Task CaptureThenTranslateAsync(HotkeyAction action, nint foreground, long generation)
    {
        if (_disposed || _paused || _saving || !IsCurrent(generation)) return;
        var captureStarted = Environment.TickCount64;
        CaptureResult capture;
        try
        {
            using var captureTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            capture = await _editor.CaptureAsync(
                action == HotkeyAction.Selection ? CaptureMode.Selection : CaptureMode.WholeControl,
                foreground, captureTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { DiagnosticLog.Write($"event=capture-cancelled generation={generation}"); return; }
        catch (Exception error)
        {
            DiagnosticLog.Write($"event=capture-exception generation={generation} type={error.GetType().Name} hresult={error.HResult:X8}");
            capture = new CaptureResult(CaptureStatus.Unavailable);
        }
        DiagnosticLog.Write($"event=capture-finished generation={generation} status={capture.Status} method={capture.Snapshot?.Method.ToString() ?? "none"} elapsedMs={Environment.TickCount64 - captureStarted}");

        if (!IsCurrent(generation)) return;
        _dispatcher.TryEnqueue(() => _ = TranslateAsync(action, capture, generation));
    }

    private async Task TranslateAsync(HotkeyAction action, CaptureResult capture, long generation)
    {
        if (_disposed || _paused || _saving || !IsCurrent(generation)) return;
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = new CancellationTokenSource();
        var token = _pending.Token;
        var settings = _settings;
        var profile = settings?.Providers.FirstOrDefault(p => p.Id == settings.ActiveProviderId);
        if (!_initialized || settings is null || profile is null)
        {
            Notify("请先打开设置并保存有效的服务配置。", true);
            return;
        }
        _page.SetResult(null);
        _page.SetApplyAvailable(false);
        try
        {
            if (!IsCurrent(generation, token)) return;
            if (capture.Snapshot is null)
            {
                Notify(CaptureMessage(capture), true);
                return;
            }
            var key = await _secrets.ReadAsync(profile.CredentialReference, token);
            var secondary = profile.SecondaryCredentialReference is null ? null : await _secrets.ReadAsync(profile.SecondaryCredentialReference, token);
            if (!IsCurrent(generation, token)) return;
            if (string.IsNullOrWhiteSpace(key) || (RequiresSecondary(profile.Protocol) && string.IsNullOrWhiteSpace(secondary)))
            { Notify("找不到服务凭据，请在设置中重新保存。", true); return; }
            _page.SetStatus("正在翻译…");
            var sourceText = capture.Snapshot.SourceText;
            if (action == HotkeyAction.TripleSpace) sourceText = sourceText.TrimEnd(' ');
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                Notify("输入框为空，没有可翻译的内容。", true);
                return;
            }
            var request = new TranslationRequest(TranslationLineBreaks.ToWindows(sourceText), settings.TargetLanguage, settings.SourceLanguage,
                IsMachine(profile.Protocol) ? null : settings.Style);
            var translationStarted = Environment.TickCount64;
            var operation = await Task.Run(() =>
            {
                lock (_startGate)
                {
                    token.ThrowIfCancellationRequested();
                    if (!IsCurrent(generation, token)) throw new OperationCanceledException(token);
                    return _coordinator.Start(TranslationProviderFactory.Create(profile, key, secondary), request, profile, token);
                }
            }, token);
            var result = await operation.Completion;
            DiagnosticLog.Write($"event=translation-complete generation={generation} status={result.Status} elapsedMs={Environment.TickCount64 - translationStarted}");
            if (!IsCurrent(generation, token)) return;
            if (!result.IsSuccessful || result.Text is null) { Notify(ResultMessage(result), true); return; }
            var translatedText = TranslationLineBreaks.ToWindows(result.Text);
            var replacementStarted = Environment.TickCount64;
            Task<ReplacementResult>? replacementTask = null;
            replacementTask = _editor.ReplaceAsync(capture.Snapshot, translatedText, action =>
            {
                if (!IsCurrent(generation, token)) return new ReplacementResult(ReplacementStatus.ManualApply);
                return _coordinator.TryExecuteIfCurrent(operation, () =>
                    IsCurrent(generation, token) ? action() : new ReplacementResult(ReplacementStatus.ManualApply), out var value)
                    ? value : new ReplacementResult(ReplacementStatus.ManualApply);
            }, token, () => IsCurrent(generation, token), () => _dispatcher.TryEnqueue(() =>
            {
                if (IsCurrent(generation, token) && replacementTask is { IsCompleted: false })
                    _page.SetStatus("正在确认回填…");
            }));
            var replacement = await replacementTask;
            DiagnosticLog.Write($"event=replacement-complete generation={generation} status={replacement.Status} elapsedMs={Environment.TickCount64 - replacementStarted}");
            if (!IsCurrent(generation, token)) return;
            if (replacement.Status == ReplacementStatus.Applied)
                Notify("译文已回填。");
            else
            {
                _page.SetResult(translatedText);
                Notify(replacement.Status switch
                {
                    ReplacementStatus.SubmittedUnconfirmed => "已提交回填但无法确认，请检查目标输入框；译文可在设置中复制。",
                    ReplacementStatus.OutcomeUnknown => "回填结果不确定，请先检查目标输入框；译文可在设置中复制。",
                    _ => "未能自动回填，译文可在设置中复制。"
                }, true);
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(generation, token)) Notify("读取或翻译超时，原文未自动替换。", true);
        }
        catch
        {
            if (IsCurrent(generation, token)) Notify("翻译失败，请检查输入框和服务设置。", true);
        }
    }

    private bool IsCurrent(long generation) =>
        !_disposed && generation == Volatile.Read(ref _generation);

    private bool IsCurrent(long generation, CancellationToken token) =>
        !_disposed && !token.IsCancellationRequested && generation == Volatile.Read(ref _generation);

    private void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _pending?.Cancel();
    }

    private void OnTrayAction(TrayAction action) => _dispatcher.TryEnqueue(() =>
    {
        if (_disposed) return;
        switch (action)
        {
            case TrayAction.OpenSettings:
            case TrayAction.OpenResult: _openSettings(); break;
            case TrayAction.TogglePause:
                _paused = !_paused;
                if (_paused) Invalidate();
                _tray?.SetPaused(_paused);
                _page.SetStatus(_paused ? "翻译已暂停。" : "翻译已恢复。");
                break;
            case TrayAction.Exit: _exit(); break;
        }
    });

    private void ApplyPreferences(ApplicationPreferences preferences)
    {
        _notificationsEnabled = preferences.NotificationsEnabled;
        _page.ApplyApplicationSettings(
            preferences.Language == InterfaceLanguage.English ? "en-US" : "zh-CN",
            preferences.Theme.ToString(), preferences.NotificationsEnabled);
        _tray?.SetEnglish(preferences.Language == InterfaceLanguage.English);
    }

    private async void OnApplicationSettingsChanged(string language, string theme, bool notificationsEnabled)
    {
        var version = ++_preferencesVersion;
        if (_preferencesUnavailable)
        {
            ApplyPreferences(_preferences);
            _page.SetStatus("无法读取应用设置；现有文件未修改，请检查后重启。", true);
            return;
        }
        var next = new ApplicationPreferences(
            language == "en-US" ? InterfaceLanguage.English : InterfaceLanguage.SimplifiedChinese,
            Enum.TryParse<ApplicationTheme>(theme, out var parsed) && Enum.IsDefined(parsed) ? parsed : ApplicationTheme.System,
            notificationsEnabled);
        _notificationsEnabled = notificationsEnabled;
        _tray?.SetEnglish(next.Language == InterfaceLanguage.English);
        await _preferencesGate.WaitAsync();
        try
        {
            await _preferencesStore.SaveAsync(next);
            _preferences = next;
        }
        catch
        {
            if (version == _preferencesVersion && !_disposed)
            {
                ApplyPreferences(_preferences);
                _page.SetStatus("无法保存应用设置，已恢复上次设置。", true);
            }
        }
        finally { _preferencesGate.Release(); }
    }

    private void Notify(string message, bool error = false)
    {
        _dispatcher.TryEnqueue(() =>
        {
            _page.SetStatus(message, error);
            var localized = _page.Localize(message);
            var tray = _tray;
            if (tray is null || !_notificationsEnabled) return;
            // The native thread can be blocked inside TrackPopupMenu.
            _ = Task.Run(() =>
            {
                try { if (_notificationsEnabled && !_disposed) tray.ShowNotification("PopoTranslation", localized); }
                catch { /* Notifications are best effort. */ }
            });
        });
    }

    private static string CaptureMessage(CaptureResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Reason)) return result.Reason;
        return result.Status switch
        {
            CaptureStatus.Protected => "受保护的输入框不能读取。",
            CaptureStatus.ReadOnly => "当前控件不可编辑。",
            CaptureStatus.Empty => "当前选区或输入框为空。",
            CaptureStatus.Busy => "输入框读取服务正忙，请稍后重试。",
            CaptureStatus.NoFocusedEditor => "找不到当前输入框。",
            _ => "无法安全读取当前输入框；终端请先选中文本并使用选区快捷键。"
        };
    }

    private static string ResultMessage(TranslationResult result) => result.Status switch
    {
        TranslationStatus.Failed when result.ErrorCode is "unsupported_language" => "该翻译服务不支持所选语言，请更换语言或服务。",
        TranslationStatus.Failed when result.ErrorCode is "line_break_mismatch" or "incomplete" => "译文结构不完整，未回填。",
        TranslationStatus.TimedOut => "翻译超时，请检查服务配置。",
        TranslationStatus.Truncated => "译文被截断，未回填。",
        TranslationStatus.Refused => "服务拒绝了翻译请求，未回填。",
        TranslationStatus.Empty => "服务未返回有效译文。",
        TranslationStatus.Cancelled => "翻译已取消。",
        _ => "翻译请求失败，请检查服务配置和密钥。"
    };

    private static bool IsMachine(ApiProtocol protocol) => protocol is
        ApiProtocol.AlibabaTranslate or ApiProtocol.BaiduTranslate or ApiProtocol.YoudaoTranslate or ApiProtocol.DeepLTranslate or ApiProtocol.QwenMt;

    private static bool RequiresSecondary(ApiProtocol protocol) => protocol is
        ApiProtocol.AlibabaTranslate or ApiProtocol.BaiduTranslate or ApiProtocol.YoudaoTranslate;

    private static bool TryBuildProfile(SettingsForm form, out ProviderProfile? profile, out string? error)
    {
        profile = null;
        error = null;
        if (!Enum.TryParse<ApiProtocol>(form.Protocol, false, out var protocol) || !Enum.IsDefined(protocol))
        { error = "服务类型无效。"; return false; }
        var baseUrl = string.IsNullOrWhiteSpace(form.BaseUrl) ? protocol switch
        {
            ApiProtocol.OpenAiChatCompletions or ApiProtocol.OpenAiResponses => "https://api.openai.com/v1",
            ApiProtocol.AnthropicMessages => "https://api.anthropic.com",
            ApiProtocol.GeminiGenerateContent => "https://generativelanguage.googleapis.com",
            ApiProtocol.AlibabaTranslate => "https://mt.cn-hangzhou.aliyuncs.com",
            ApiProtocol.BaiduTranslate => "https://fanyi-api.baidu.com",
            ApiProtocol.YoudaoTranslate => "https://openapi.youdao.com",
            ApiProtocol.DeepLTranslate => "https://api-free.deepl.com",
            // Alibaba Cloud Model Studio Base URL, verified 2026-10-28:
            // https://docs.bailian.console.aliyun.com/zh/model-studio/base-url
            ApiProtocol.QwenMt => ProviderCredentialPolicy.QwenDefaultBaseUrl,
            _ => ""
        } : form.BaseUrl;
        profile = new ProviderProfile(form.ProfileId, form.Name, protocol, baseUrl, form.ModelId, "pending", form.TimeoutSeconds);
        return true;
    }

    private static bool TryParseHotkey(string text, out Hotkey? hotkey)
    {
        hotkey = null;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        HotkeyModifiers modifiers = 0;
        foreach (var part in parts[..^1])
        {
            var modifier = part.ToUpperInvariant() switch
            {
                "CTRL" => HotkeyModifiers.Control, "ALT" => HotkeyModifiers.Alt,
                "SHIFT" => HotkeyModifiers.Shift, _ => (HotkeyModifiers)0
            };
            if (modifier == 0 || (modifiers & modifier) != 0) return false;
            modifiers |= modifier;
        }
        var key = parts[^1].ToUpperInvariant();
        uint vk;
        if (key.Length == 1 && (key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')) vk = key[0];
        else if (key.StartsWith('F') && int.TryParse(key[1..], out var number) && number is >= 1 and <= 24 && number != 12 && key == $"F{number}") vk = (uint)(0x70 + number - 1);
        else return false;
        hotkey = new Hotkey(modifiers, vk);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _page.ApplicationSettingsChanged -= OnApplicationSettingsChanged;
        Invalidate();
        _pending?.Dispose();
        _spaceHotkey?.Dispose();
        _spaceHotkey = null;
        _tray?.Dispose();
        _hotkeys?.Dispose();
        _native?.Dispose();
        _editor.Dispose();
    }
}
