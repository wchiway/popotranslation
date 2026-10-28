using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace popotranslation.Views;

// The key is transient input. An empty key means retain the existing credential.
public sealed record SettingsForm(
    string ProfileId,
    string Name,
    string Protocol,
    string BaseUrl,
    string ModelId,
    string ApiKey,
    bool HasSavedApiKey,
    int TimeoutSeconds,
    string? SourceLanguage,
    string TargetLanguage,
    string? Style,
    string SelectionHotkey,
    string WholeControlHotkey,
    string SecondaryCredential = "",
    bool HasSavedSecondaryCredential = false)
{
    public override string ToString() => "SettingsForm { credentials redacted }";
}

public sealed record SettingsActionResult(bool Success, string Message);
public sealed record ProfileOption(string Id, string Name);

public sealed partial class SettingsPage : Page
{
    private string _profileId = Guid.NewGuid().ToString("N");
    private bool _hasSavedApiKey;
    private bool _hasSavedSecondaryCredential;
    private string? _loadedProtocol;
    private readonly Dictionary<string, string> _protocolEndpoints = new(StringComparer.Ordinal);
    private bool _loadingSettings;
    private bool _busy;
    private bool _applyingResult;
    private bool _updatingProfiles;
    private string? _selectedProfileId;
    private Draft? _baseline;
    private ComboBoxItem? _savedTargetLanguageItem;

    private sealed record Draft(string Name, string Protocol, string BaseUrl, string ModelId, string Timeout,
        string SourceLanguage, string TargetLanguage, string Style, string SelectionHotkey, string WholeControlHotkey);

    public Func<SettingsForm, Task<SettingsActionResult>>? SaveRequested { get; set; }
    public Func<SettingsForm, Task<SettingsActionResult>>? TestConnectionRequested { get; set; }
    public event Action<string>? ResultCopied;
    public Func<string, Task<SettingsActionResult>>? ApplyResultRequested { get; set; }
    public Func<string, Task<SettingsActionResult>>? SelectProfileRequested { get; set; }
    public Func<Task<SettingsActionResult>>? AddProfileRequested { get; set; }
    public Func<string, Task<SettingsActionResult>>? DeleteProfileRequested { get; set; }

    public SettingsPage()
    {
        InitializeComponent();
        TimeoutBox.Text = "30";
    }

    public void LoadSettings(SettingsForm settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!string.IsNullOrWhiteSpace(settings.ProfileId)) _profileId = settings.ProfileId;
        _selectedProfileId = _profileId;
        _loadingSettings = true;
        _loadedProtocol = settings.Protocol;
        _protocolEndpoints.Clear();
        _protocolEndpoints[settings.Protocol] = settings.BaseUrl;
        _hasSavedApiKey = settings.HasSavedApiKey;
        _hasSavedSecondaryCredential = settings.HasSavedSecondaryCredential;
        NameBox.Text = settings.Name;
        ProtocolBox.SelectedIndex = -1;
        foreach (ComboBoxItem item in ProtocolBox.Items)
        {
            if (item.Tag as string == settings.Protocol)
            {
                ProtocolBox.SelectedItem = item;
                break;
            }
        }
        BaseUrlBox.Text = settings.BaseUrl;
        ModelBox.Text = settings.ModelId;
        ApiKeyBox.Password = string.Empty;
        SecondaryKeyBox.Password = string.Empty;
        UpdateKeyHint();
        TimeoutBox.Text = settings.TimeoutSeconds.ToString();
        SourceLanguageBox.Text = settings.SourceLanguage ?? string.Empty;
        SelectMachineSourceLanguage(settings.SourceLanguage);
        SelectTargetLanguage(settings.TargetLanguage);
        StyleBox.Text = settings.Style ?? string.Empty;
        SelectionHotkeyBox.Text = settings.SelectionHotkey;
        WholeHotkeyBox.Text = settings.WholeControlHotkey;
        _loadingSettings = false;
        UpdateProtocolFields();
        _baseline = CaptureForm();
    }

    public void SetProfiles(IReadOnlyList<ProfileOption> profiles, string? activeProfileId)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _updatingProfiles = true;
        try
        {
            ProfilesBox.Items.Clear();
            foreach (var profile in profiles)
                ProfilesBox.Items.Add(new ComboBoxItem { Content = profile.Name, Tag = profile.Id });
            _selectedProfileId = activeProfileId;
            SelectProfileItem(activeProfileId);
        }
        finally { _updatingProfiles = false; }
    }

    private void SelectProfileItem(string? id)
    {
        ProfilesBox.SelectedItem = ProfilesBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, id, StringComparison.Ordinal));
    }

    private void SelectTargetLanguage(string language)
    {
        if (_savedTargetLanguageItem is not null)
        {
            TargetLanguageBox.Items.Remove(_savedTargetLanguageItem);
            _savedTargetLanguageItem = null;
        }

        var selected = TargetLanguageBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, language, StringComparison.Ordinal));
        if (selected is null && !string.IsNullOrWhiteSpace(language))
        {
            // Keep a previously saved free-form language available without changing its value.
            selected = _savedTargetLanguageItem = new ComboBoxItem { Content = $"{language}（已保存）", Tag = language };
            TargetLanguageBox.Items.Add(selected);
        }
        TargetLanguageBox.SelectedItem = selected;
    }

    private string SelectedProtocol => (ProtocolBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
    private static bool IsMachine(string protocol) => protocol is "AlibabaTranslate" or "BaiduTranslate" or "YoudaoTranslate" or "DeepLTranslate";
    private static bool NeedsSecondary(string protocol) => protocol is "AlibabaTranslate" or "BaiduTranslate" or "YoudaoTranslate";
    private bool CanUseSavedCredential => SelectedProtocol == _loadedProtocol;
    private string SelectedSourceLanguage => IsMachine(SelectedProtocol)
        ? (MachineSourceLanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty
        : SourceLanguageBox.Text;

    private void SelectMachineSourceLanguage(string? language)
    {
        var value = language ?? string.Empty;
        var item = MachineSourceLanguageBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(x => string.Equals(x.Tag as string, value, StringComparison.Ordinal));
        if (item is null && value.Length != 0)
        {
            item = new ComboBoxItem { Content = $"{value}（已保存）", Tag = value };
            MachineSourceLanguageBox.Items.Add(item);
        }
        MachineSourceLanguageBox.SelectedItem = item;
    }

    private void Protocol_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || ApiKeyBox is null || SecondaryKeyBox is null || MachineSourceLanguageBox is null || BaseUrlBox is null) return;
        // Keep endpoints per protocol; never carry transient credentials across protocols.
        var previousProtocol = (e.RemovedItems.OfType<ComboBoxItem>().FirstOrDefault()?.Tag as string) ?? string.Empty;
        if (previousProtocol.Length != 0) _protocolEndpoints[previousProtocol] = BaseUrlBox.Text;
        ApiKeyBox.Password = string.Empty;
        SecondaryKeyBox.Password = string.Empty;
        BaseUrlBox.Text = _protocolEndpoints.GetValueOrDefault(SelectedProtocol, string.Empty);
        if (IsMachine(SelectedProtocol) && !IsMachine(previousProtocol)) SelectMachineSourceLanguage(SourceLanguageBox.Text);
        else if (!IsMachine(SelectedProtocol) && IsMachine(previousProtocol))
            SourceLanguageBox.Text = (MachineSourceLanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? SourceLanguageBox.Text;
        UpdateProtocolFields();
    }

    private void UpdateProtocolFields()
    {
        var protocol = SelectedProtocol;
        var machine = IsMachine(protocol);
        var secondary = NeedsSecondary(protocol);
        ModelBox.Visibility = machine ? Visibility.Collapsed : Visibility.Visible;
        StyleBox.Visibility = machine ? Visibility.Collapsed : Visibility.Visible;
        MachineStyleHint.Visibility = machine ? Visibility.Visible : Visibility.Collapsed;
        MachineStyleHint.Text = protocol == "AlibabaTranslate"
            ? "机器翻译不使用翻译风格设置；阿里云暂不支持繁體中文。"
            : "机器翻译不使用翻译风格设置。";
        SourceLanguageBox.Visibility = machine ? Visibility.Collapsed : Visibility.Visible;
        MachineSourceLanguageBox.Visibility = machine ? Visibility.Visible : Visibility.Collapsed;
        SecondaryKeyBox.Visibility = secondary ? Visibility.Visible : Visibility.Collapsed;
        SecondaryKeyHint.Visibility = secondary ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyBox.Header = protocol switch
        {
            "AlibabaTranslate" => "AccessKey ID",
            "BaiduTranslate" => "APP ID",
            "YoudaoTranslate" => "应用 ID",
            _ => "API Key"
        };
        SecondaryKeyBox.Header = protocol == "AlibabaTranslate" ? "AccessKey Secret" : "应用密钥";
        BaseUrlBox.PlaceholderText = protocol == "DeepLTranslate" ? "留空使用 DeepL 免费版地址；付费版请输入官方地址" : "留空使用官方默认地址";
        UpdateKeyHint();
    }

    private string SelectedTargetLanguage => (TargetLanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

    private Draft CaptureForm() => new(
        NameBox.Text, (ProtocolBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty,
        BaseUrlBox.Text, ModelBox.Text, TimeoutBox.Text, SelectedSourceLanguage,
        SelectedTargetLanguage, StyleBox.Text, SelectionHotkeyBox.Text, WholeHotkeyBox.Text);

    private bool HasUnsavedChanges()
    {
        if (_baseline is null) return true;
        return !string.IsNullOrEmpty(ApiKeyBox.Password) || !string.IsNullOrEmpty(SecondaryKeyBox.Password) || CaptureForm() != _baseline;
    }

    public void SetStatus(string message, bool isError = false)
    {
        StatusBar.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        StatusBar.Title = isError ? "需要注意" : "状态";
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    public void SetResult(string? text)
    {
        ResultBox.Text = text ?? string.Empty;
        CopyButton.IsEnabled = !string.IsNullOrEmpty(text);
        ApplyButton.IsEnabled = !_applyingResult && !string.IsNullOrEmpty(text);
    }

    public void SetApplyAvailable(bool available)
    {
        ApplyButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.IsEnabled = available && !_applyingResult && !string.IsNullOrEmpty(ResultBox.Text);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(SaveRequested, true);
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        await RunActionAsync(TestConnectionRequested, false);
    }

    private async Task RunActionAsync(Func<SettingsForm, Task<SettingsActionResult>>? action, bool saving)
    {
        if (_busy) return;
        if (action is null)
        {
            SetStatus("此功能尚未连接。", true);
            return;
        }
        if (!TryGetForm(out var form, out var error))
        {
            SetStatus(error, true);
            return;
        }

        SetBusy(true);
        SetStatus(saving ? "正在保存设置…" : "正在测试连接…");
        try
        {
            var result = await action(form!);
            if (saving && result.Success)
            {
                _loadedProtocol = form!.Protocol;
                _hasSavedApiKey = true;
                _hasSavedSecondaryCredential = NeedsSecondary(form.Protocol);
                ApiKeyBox.Password = string.Empty;
                SecondaryKeyBox.Password = string.Empty;
                UpdateKeyHint();
                _baseline = CaptureForm();
            }
            SetStatus(result.Message, !result.Success);
        }
        catch (Exception)
        {
            // Do not surface exception text, which may contain credentials or request data.
            SetStatus(saving ? "保存失败，请检查设置后重试。" : "连接测试失败，请检查设置后重试。", true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        ProfilesBox.IsEnabled = !busy;
        AddProfileButton.IsEnabled = !busy;
        DeleteProfileButton.IsEnabled = !busy;
        SourceLanguageBox.IsEnabled = !busy;
        MachineSourceLanguageBox.IsEnabled = !busy;
        SecondaryKeyBox.IsEnabled = !busy;
        TargetLanguageBox.IsEnabled = !busy;
        StyleBox.IsEnabled = !busy;
        SelectionHotkeyBox.IsEnabled = !busy;
        WholeHotkeyBox.IsEnabled = !busy;
        NameBox.IsEnabled = !busy;
        ProtocolBox.IsEnabled = !busy;
        BaseUrlBox.IsEnabled = !busy;
        ModelBox.IsEnabled = !busy;
        ApiKeyBox.IsEnabled = !busy;
        TimeoutBox.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy;
        TestButton.IsEnabled = !busy;
        BusyIndicator.IsActive = busy;
    }

    private async void Profiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingProfiles || _busy) return;
        var id = (ProfilesBox.SelectedItem as ComboBoxItem)?.Tag as string;
        if (id is null || id == _selectedProfileId) return;
        var previousId = _selectedProfileId;
        SetBusy(true);
        try
        {
            if (!await ConfirmDiscardAsync() || SelectProfileRequested is null)
            {
                if (SelectProfileRequested is null) SetStatus("服务切换尚未连接。", true);
                return;
            }
            var result = await SelectProfileRequested(id);
            if (result.Success) _selectedProfileId = id;
            SetStatus(result.Message, !result.Success);
        }
        catch (Exception) { SetStatus("无法切换服务，请重试。", true); }
        finally
        {
            _updatingProfiles = true;
            try { SelectProfileItem(_selectedProfileId ?? previousId); }
            finally { _updatingProfiles = false; SetBusy(false); }
        }
    }

    private async void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            if (!await ConfirmDiscardAsync()) return;
            if (AddProfileRequested is null) { SetStatus("新增服务尚未连接。", true); return; }
            var result = await AddProfileRequested();
            SetStatus(result.Message, !result.Success);
        }
        catch (Exception) { SetStatus("无法新增服务，请重试。", true); }
        finally { SetBusy(false); }
    }

    private async void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _selectedProfileId is null) return;
        SetBusy(true);
        try
        {
            if (!await ConfirmDiscardAsync()) return;
            if (DeleteProfileRequested is null) { SetStatus("删除服务尚未连接。", true); return; }
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "删除服务？",
                Content = "此操作将删除当前服务配置及其保存的密钥。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var result = await DeleteProfileRequested(_selectedProfileId);
            SetStatus(result.Message, !result.Success);
        }
        catch (Exception) { SetStatus("无法删除服务，请重试。", true); }
        finally { SetBusy(false); }
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!HasUnsavedChanges()) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "放弃未保存的更改？",
            Content = "切换或修改服务列表会放弃当前页面未保存的设置和密钥输入。",
            PrimaryButtonText = "放弃更改",
            CloseButtonText = "返回编辑",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private bool TryGetForm(out SettingsForm? form, out string error)
    {
        form = null;
        error = string.Empty;
        var name = NameBox.Text.Trim();
        var baseUrl = BaseUrlBox.Text.Trim();
        var model = ModelBox.Text.Trim();
        var target = SelectedTargetLanguage;
        if (SelectedProtocol == "AlibabaTranslate" && (target == "繁體中文" || SelectedSourceLanguage == "繁體中文"))
        {
            error = "阿里云机器翻译暂不支持所选的繁體中文；请选择其他语言或服务。";
            return false;
        }
        if (name.Length == 0 || target.Length == 0 || ProtocolBox.SelectedItem is not ComboBoxItem selected || selected.Tag is not string protocol ||
            (!IsMachine(protocol) && model.Length == 0))
        {
            error = "请填写服务名称、目标语言，选择服务类型；AI 服务还需填写模型。";
            return false;
        }
        if (baseUrl.Length != 0 && (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)))
        {
            error = "自定义服务地址必须使用 HTTPS；本机 localhost 可使用 HTTP，且不能包含凭据、查询参数或片段。";
            return false;
        }
        if (!int.TryParse(TimeoutBox.Text, out var timeout) || timeout < 1 || timeout > 600)
        {
            error = "请求超时须为 1 到 600 秒之间的整数。";
            return false;
        }
        if (!TryNormalizeHotkey(SelectionHotkeyBox.Text, out var selection) ||
            !TryNormalizeHotkey(WholeHotkeyBox.Text, out var whole))
        {
            error = "快捷键须包含 Ctrl、Alt 或 Shift 和一个字母、数字或 F1–F24 功能键；F12 及 Win 组合不可用。";
            return false;
        }
        if (selection == whole)
        {
            error = "两个翻译快捷键不能相同。";
            return false;
        }
        if ((!CanUseSavedCredential || !_hasSavedApiKey) && string.IsNullOrWhiteSpace(ApiKeyBox.Password))
        {
            error = "请输入服务凭据。";
            return false;
        }
        if (NeedsSecondary(protocol) && (!CanUseSavedCredential || !_hasSavedSecondaryCredential) && string.IsNullOrWhiteSpace(SecondaryKeyBox.Password))
        {
            error = "请输入应用密钥。";
            return false;
        }
        form = new SettingsForm(_profileId, name, protocol, baseUrl, IsMachine(protocol) ? "" : model, ApiKeyBox.Password,
            CanUseSavedCredential && _hasSavedApiKey, timeout, Optional(SelectedSourceLanguage), target,
            Optional(StyleBox.Text), selection, whole,
            NeedsSecondary(protocol) ? SecondaryKeyBox.Password : "", CanUseSavedCredential && _hasSavedSecondaryCredential);
        return true;
    }

    private static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryNormalizeHotkey(string text, out string normalized)
    {
        normalized = string.Empty;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        var modifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!new[] { "Ctrl", "Alt", "Shift", "Win" }.Contains(parts[i], StringComparer.OrdinalIgnoreCase) ||
                !modifiers.Add(parts[i])) return false;
        }
        var key = parts[^1].ToUpperInvariant();
        var validKey = key.Length == 1 && ((key[0] >= 'A' && key[0] <= 'Z') || (key[0] >= '0' && key[0] <= '9'));
        validKey |= key.Length >= 2 && key[0] == 'F' && int.TryParse(key[1..], out var number) && number is >= 1 and <= 24 && key == $"F{number}";
        if (!validKey || key == "F12" || modifiers.Contains("Win")) return false;
        normalized = string.Join("+", new[] { "Ctrl", "Alt", "Shift", "Win" }.Where(modifiers.Contains).Append(key));
        return true;
    }

    private void UpdateKeyHint()
    {
        ApiKeyHint.Text = CanUseSavedCredential && _hasSavedApiKey ? "已保存凭据；留空可继续使用，输入新值可替换。" : "凭据会在保存时交由应用安全存储。";
        SecondaryKeyHint.Text = CanUseSavedCredential && _hasSavedSecondaryCredential ? "已保存密钥；留空可继续使用，输入新值可替换。" : "密钥会在保存时交由应用安全存储。";
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var text = ResultBox.Text;
        var action = ApplyResultRequested;
        if (_applyingResult || string.IsNullOrEmpty(text)) return;
        if (action is null)
        {
            SetStatus("暂不支持应用译文，请复制结果后手动粘贴。", true);
            return;
        }
        _applyingResult = true;
        ApplyButton.IsEnabled = false;
        try
        {
            var result = await action(text);
            SetStatus(result.Message, !result.Success);
        }
        catch (Exception)
        {
            SetStatus("无法应用译文，请复制结果后手动粘贴。", true);
        }
        finally
        {
            _applyingResult = false;
            ApplyButton.IsEnabled = !string.IsNullOrEmpty(ResultBox.Text);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(ResultBox.Text)) return;
        try
        {
            var data = new DataPackage();
            data.SetText(ResultBox.Text);
            Clipboard.SetContent(data);
            SetStatus("结果已复制到剪贴板。");
            ResultCopied?.Invoke(ResultBox.Text);
        }
        catch (Exception)
        {
            SetStatus("无法复制结果，请手动选择并复制。", true);
        }
    }
}
