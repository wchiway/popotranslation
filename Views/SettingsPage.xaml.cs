using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Windows.ApplicationModel.DataTransfer;
using PopoTranslation.Core;

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
    bool HasSavedSecondaryCredential = false,
    string CustomHeadersJson = "",
    string CustomBodyJson = "")
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
    private string? _loadedBaseUrl;
    private readonly Dictionary<string, string> _protocolEndpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Headers, string Body)> _protocolAdvanced = new(StringComparer.Ordinal);
    private bool _loadingSettings;
    private bool _busy;
    private bool _applyingResult;
    private bool _updatingProfiles;
    private string? _selectedProfileId;
    private Draft? _baseline;
    private ComboBoxItem? _savedTargetLanguageItem;
    private ComboBoxItem? _savedQwenModelItem;
    private bool _updatingApplicationSettings = true;
    private string _language = "zh-CN";
    private string _statusMessage = "保存设置后，使用快捷键开始翻译。";
    private bool _statusWasSet;
    private readonly List<(Action<string> Set, string Original)> _localizedProperties = [];

    public event Action<string, string, bool>? ApplicationSettingsChanged;

    // Values are stable persistence identifiers, independent of the displayed language.
    public void ApplyApplicationSettings(string language, string theme, bool notificationsEnabled) =>
        ApplyApplicationSettingsCore(language, theme, notificationsEnabled, true);

    private void ApplyApplicationSettingsCore(string language, string theme, bool notificationsEnabled, bool enableControls)
    {
        _updatingApplicationSettings = true;
        try
        {
            _language = language == "en-US" ? "en-US" : "zh-CN";
            AppLanguageBox.SelectedIndex = _language == "en-US" ? 1 : 0;
            AppThemeBox.SelectedIndex = theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
            NotificationsToggle.IsOn = notificationsEnabled;
            AppLanguageBox.IsEnabled = enableControls;
            AppThemeBox.IsEnabled = enableControls;
            NotificationsToggle.IsEnabled = enableControls;
            RequestedTheme = theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
            RefreshLanguage();
        }
        finally { _updatingApplicationSettings = false; }
    }

    public string Localize(string chinese)
    {
        if (_language != "en-US") return chinese;
        if (UiLocalization.English.TryGetValue(chinese, out var translated)) return translated;
        const string prefix = "UIA 将 ";
        const string protectedSuffix = " 标记为受保护；请确认焦点在聊天输入框，而不是密码或支付输入框。";
        const string unknownPrefix = "UIA 无法确认 ";
        const string unknownSuffix = " 是否受保护；请把光标放进聊天输入框后重试。";
        if (chinese.StartsWith(prefix, StringComparison.Ordinal) && chinese.EndsWith(protectedSuffix, StringComparison.Ordinal))
            return $"UIA marks {chinese[prefix.Length..^protectedSuffix.Length]} as protected. Ensure focus is in a chat input box, not a credential or payment field.";
        if (chinese.StartsWith(unknownPrefix, StringComparison.Ordinal) && chinese.EndsWith(unknownSuffix, StringComparison.Ordinal))
            return $"UIA cannot determine whether {chinese[unknownPrefix.Length..^unknownSuffix.Length]} is protected. Put the cursor in a chat input box and try again.";
        return chinese;
    }

    private void AppSettings_SelectionChanged(object sender, SelectionChangedEventArgs e) => OnApplicationSettingsChanged();
    private void AppSettings_Toggled(object sender, RoutedEventArgs e) => OnApplicationSettingsChanged();

    private void OnApplicationSettingsChanged()
    {
        if (_updatingApplicationSettings || AppLanguageBox?.SelectedItem is not ComboBoxItem language || AppThemeBox?.SelectedItem is not ComboBoxItem theme || NotificationsToggle is null) return;
        var languageValue = (string)language.Tag;
        var themeValue = (string)theme.Tag;
        ApplyApplicationSettings(languageValue, themeValue, NotificationsToggle.IsOn);
        ApplicationSettingsChanged?.Invoke(languageValue, themeValue, NotificationsToggle.IsOn);
    }

    private void CaptureLocalizedProperties(object? node)
    {
        if (node is null) return;
        void Add(string? value, Action<string> setter)
        {
            if (!string.IsNullOrEmpty(value) && UiLocalization.English.ContainsKey(value)) _localizedProperties.Add((setter, value));
        }
        if (node is DependencyObject dependency)
            Add(AutomationProperties.GetName(dependency), value => AutomationProperties.SetName(dependency, value));
        switch (node)
        {
            case NavigationView navigation:
                foreach (var item in navigation.MenuItems) CaptureLocalizedProperties(item);
                CaptureLocalizedProperties(navigation.Content);
                break;
            case TextBlock text: Add(text.Text, value => text.Text = value); break;
            case TextBox box:
                Add(box.Header as string, value => box.Header = value);
                Add(box.PlaceholderText, value => box.PlaceholderText = value);
                break;
            case PasswordBox box:
                Add(box.Header as string, value => box.Header = value);
                Add(box.PlaceholderText, value => box.PlaceholderText = value);
                break;
            case ComboBox combo:
                Add(combo.Header as string, value => combo.Header = value);
                foreach (var item in combo.Items) CaptureLocalizedProperties(item);
                break;
            case ToggleSwitch toggle:
                Add(toggle.Header as string, value => toggle.Header = value);
                Add(toggle.OnContent as string, value => toggle.OnContent = value);
                Add(toggle.OffContent as string, value => toggle.OffContent = value);
                break;
            case Expander expander:
                Add(expander.Header as string, value => expander.Header = value);
                CaptureLocalizedProperties(expander.Content);
                break;
            case InfoBar: break; // Status is refreshed from its original message separately.
            case ScrollViewer scroll: CaptureLocalizedProperties(scroll.Content); break;
            case ContentControl content:
                if (content.Content is string label) Add(label, value => content.Content = value);
                else CaptureLocalizedProperties(content.Content);
                break;
            case Panel panel:
                foreach (var child in panel.Children) CaptureLocalizedProperties(child);
                break;
            case Border border: CaptureLocalizedProperties(border.Child); break;
        }
    }

    private void RefreshLanguage()
    {
        foreach (var (set, original) in _localizedProperties) set(Localize(original));
        StatusBar.Title = Localize(_statusWasSet ? (StatusBar.Severity == InfoBarSeverity.Error ? "需要注意" : "状态") : "准备就绪");
        StatusBar.Message = Localize(_statusMessage);
        if (!_loadingSettings) UpdateProtocolFields();
    }

    private sealed record Draft(string Name, string Protocol, string BaseUrl, string ModelId, string Timeout,
        string SourceLanguage, string TargetLanguage, string Style, string SelectionHotkey, string WholeControlHotkey,
        string Advanced)
    {
        public override string ToString() => "Draft { sensitive values redacted }";
    }

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
        CaptureLocalizedProperties(Content);
        Navigation.SelectionChanged += Navigation_SelectionChanged;
        Navigation.SelectedItem = ServiceNavigation;
        ShowPage("service");
        ApplyApplicationSettingsCore("zh-CN", "System", true, false);
        TimeoutBox.Text = "30";
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string page)
            ShowPage(page);
    }

    private void ShowPage(string page)
    {
        ServiceCard.Visibility = page == "service" ? Visibility.Visible : Visibility.Collapsed;
        TranslationPage.Visibility = page == "translation" ? Visibility.Visible : Visibility.Collapsed;
        ResultPage.Visibility = page == "result" ? Visibility.Visible : Visibility.Collapsed;
        AppPage.Visibility = page == "app" ? Visibility.Visible : Visibility.Collapsed;
        ServiceActions.Visibility = page is "service" or "translation" ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.ChangeView(null, 0, null);
    }

    public void ShowResultPage() => Navigation.SelectedItem = ResultNavigation;

    public void LoadSettings(SettingsForm settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!string.IsNullOrWhiteSpace(settings.ProfileId)) _profileId = settings.ProfileId;
        _selectedProfileId = _profileId;
        _loadingSettings = true;
        _loadedProtocol = settings.Protocol;
        _loadedBaseUrl = settings.BaseUrl;
        _protocolEndpoints.Clear();
        _protocolEndpoints[settings.Protocol] = settings.BaseUrl;
        _protocolAdvanced.Clear();
        if (IsAdvancedProtocol(settings.Protocol))
            _protocolAdvanced[settings.Protocol] = (settings.CustomHeadersJson ?? string.Empty, settings.CustomBodyJson ?? string.Empty);
        CustomHeadersBox.Text = IsAdvancedProtocol(settings.Protocol) ? settings.CustomHeadersJson ?? string.Empty : string.Empty;
        CustomBodyBox.Text = IsAdvancedProtocol(settings.Protocol) ? settings.CustomBodyJson ?? string.Empty : string.Empty;
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
        SelectQwenModel(settings.Protocol == "QwenMt" ? settings.ModelId : string.Empty);
        SelectQwenRegion(settings.BaseUrl);
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
        _baseline = CaptureForm(currentOnly: true);
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
    private static bool IsMachine(string protocol) => protocol is "AlibabaTranslate" or "BaiduTranslate" or "YoudaoTranslate" or "DeepLTranslate" or "QwenMt";
    private static bool IsAdvancedProtocol(string protocol) => protocol is "OpenAiChatCompletions" or "OpenAiResponses" or "AnthropicMessages" or "GeminiGenerateContent";
    private static bool NeedsModel(string protocol) => !IsMachine(protocol) || protocol == "QwenMt";
    private string SelectedModel => SelectedProtocol == "QwenMt"
        ? (QwenModelBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty
        : ModelBox.Text;
    private static bool NeedsSecondary(string protocol) => protocol is "AlibabaTranslate" or "BaiduTranslate" or "YoudaoTranslate";
    private bool CanUseSavedCredential => SelectedProtocol == _loadedProtocol &&
        (SelectedProtocol != "QwenMt" || ProviderCredentialPolicy.CanReuse(ApiProtocol.QwenMt, _loadedBaseUrl, BaseUrlBox.Text));
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

    private void SelectQwenModel(string model)
    {
        if (_savedQwenModelItem is not null)
        {
            QwenModelBox.Items.Remove(_savedQwenModelItem);
            _savedQwenModelItem = null;
        }
        var item = QwenModelBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(x => string.Equals(x.Tag as string, model, StringComparison.Ordinal));
        if (item is null && !string.IsNullOrWhiteSpace(model))
        {
            item = _savedQwenModelItem = new ComboBoxItem { Content = $"{model}（已保存）", Tag = model };
            QwenModelBox.Items.Add(item);
        }
        QwenModelBox.SelectedItem = item ?? QwenModelBox.Items[0];
    }

    private void SelectQwenRegion(string baseUrl)
    {
        var item = QwenRegionBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(x => string.Equals(x.Tag as string, baseUrl, StringComparison.OrdinalIgnoreCase));
        QwenRegionBox.SelectedItem = item ?? QwenRegionBox.Items[2];
    }

    private void BaseUrl_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingSettings || SelectedProtocol != "QwenMt" || QwenRegionBox is null) return;
        if (ApiKeyBox is not null) ApiKeyBox.Password = string.Empty;
        SelectQwenRegion(BaseUrlBox.Text);
        UpdateKeyHint();
    }

    private void QwenRegion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || SelectedProtocol != "QwenMt" || BaseUrlBox is null) return;
        var url = (QwenRegionBox.SelectedItem as ComboBoxItem)?.Tag as string;
        if (url is not null && url != "custom") BaseUrlBox.Text = url;
    }

    private void Protocol_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings || ApiKeyBox is null || SecondaryKeyBox is null || MachineSourceLanguageBox is null || BaseUrlBox is null || CustomHeadersBox is null || CustomBodyBox is null) return;
        // Keep endpoints per protocol; never carry transient credentials across protocols.
        var previousProtocol = (e.RemovedItems.OfType<ComboBoxItem>().FirstOrDefault()?.Tag as string) ?? string.Empty;
        if (previousProtocol.Length != 0) _protocolEndpoints[previousProtocol] = BaseUrlBox.Text;
        if (IsAdvancedProtocol(previousProtocol))
            _protocolAdvanced[previousProtocol] = (CustomHeadersBox.Text, CustomBodyBox.Text);
        var advanced = _protocolAdvanced.GetValueOrDefault(SelectedProtocol, (string.Empty, string.Empty));
        CustomHeadersBox.Text = advanced.Item1;
        CustomBodyBox.Text = advanced.Item2;
        ApiKeyBox.Password = string.Empty;
        SecondaryKeyBox.Password = string.Empty;
        BaseUrlBox.Text = _protocolEndpoints.GetValueOrDefault(SelectedProtocol, string.Empty);
        if (SelectedProtocol == "QwenMt")
        {
            if (QwenModelBox.SelectedItem is null) SelectQwenModel(string.Empty);
            if (string.IsNullOrWhiteSpace(BaseUrlBox.Text))
                BaseUrlBox.Text = (QwenRegionBox.Items[0] as ComboBoxItem)?.Tag as string ?? string.Empty;
            SelectQwenRegion(BaseUrlBox.Text);
        }
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
        var qwen = protocol == "QwenMt";
        AdvancedSettings.Visibility = IsAdvancedProtocol(protocol) ? Visibility.Visible : Visibility.Collapsed;
        ModelBox.Visibility = machine ? Visibility.Collapsed : Visibility.Visible;
        QwenModelBox.Visibility = qwen ? Visibility.Visible : Visibility.Collapsed;
        QwenRegionBox.Visibility = qwen ? Visibility.Visible : Visibility.Collapsed;
        QwenRegionHint.Visibility = qwen ? Visibility.Visible : Visibility.Collapsed;
        StyleBox.Visibility = machine ? Visibility.Collapsed : Visibility.Visible;
        MachineStyleHint.Visibility = machine ? Visibility.Visible : Visibility.Collapsed;
        MachineStyleHint.Text = Localize(protocol == "AlibabaTranslate"
            ? "机器翻译不使用翻译风格设置；阿里云暂不支持繁體中文。"
            : "机器翻译不使用翻译风格设置。");
        SourceLanguageBox.Visibility = machine ? Visibility.Collapsed : Visibility.Visible;
        MachineSourceLanguageBox.Visibility = machine ? Visibility.Visible : Visibility.Collapsed;
        SecondaryKeyBox.Visibility = secondary ? Visibility.Visible : Visibility.Collapsed;
        SecondaryKeyHint.Visibility = secondary ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyBox.Header = Localize(protocol switch
        {
            "AlibabaTranslate" => "AccessKey ID",
            "BaiduTranslate" => "APP ID",
            "YoudaoTranslate" => "应用 ID",
            _ => "API Key"
        });
        SecondaryKeyBox.Header = Localize(protocol == "AlibabaTranslate" ? "AccessKey Secret" : "应用密钥");
        BaseUrlBox.PlaceholderText = Localize(protocol == "DeepLTranslate" ? "留空使用 DeepL 免费版地址；付费版请输入官方地址" : "留空使用官方默认地址");
        UpdateKeyHint();
    }

    private string SelectedTargetLanguage => (TargetLanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;

    private Draft CaptureForm(bool currentOnly = false)
    {
        var advanced = currentOnly
            ? new Dictionary<string, (string Headers, string Body)>(StringComparer.Ordinal)
            : new Dictionary<string, (string Headers, string Body)>(_protocolAdvanced, StringComparer.Ordinal);
        if (IsAdvancedProtocol(SelectedProtocol))
            advanced[SelectedProtocol] = (CustomHeadersBox.Text, CustomBodyBox.Text);
        var snapshot = JsonSerializer.Serialize(advanced.Where(pair => pair.Value.Headers.Length != 0 || pair.Value.Body.Length != 0)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new[] { pair.Key, pair.Value.Headers, pair.Value.Body }));
        return new Draft(NameBox.Text, SelectedProtocol, BaseUrlBox.Text, SelectedModel, TimeoutBox.Text, SelectedSourceLanguage,
            SelectedTargetLanguage, StyleBox.Text, SelectionHotkeyBox.Text, WholeHotkeyBox.Text, snapshot);
    }

    private bool HasUnsavedChanges()
    {
        if (_baseline is null) return true;
        return !string.IsNullOrEmpty(ApiKeyBox.Password) || !string.IsNullOrEmpty(SecondaryKeyBox.Password) || CaptureForm() != _baseline;
    }

    public void SetStatus(string message, bool isError = false)
    {
        _statusWasSet = true;
        _statusMessage = message;
        StatusBar.Severity = isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        StatusBar.Title = Localize(isError ? "需要注意" : "状态");
        StatusBar.Message = Localize(message);
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
            Navigation.SelectedItem = error switch
            {
                "阿里云机器翻译暂不支持所选的繁體中文；请选择其他语言或服务。" or
                "快捷键须包含 Ctrl、Alt 或 Shift 和一个字母、数字或 F1–F24 功能键；F12 及 Win 组合不可用。" or
                "两个翻译快捷键不能相同。" => TranslationNavigation,
                "请填写服务名称、目标语言，选择服务类型；AI 服务还需填写模型。" when string.IsNullOrEmpty(SelectedTargetLanguage) => TranslationNavigation,
                _ => ServiceNavigation
            };
            if (error is "自定义请求头必须为空或有效的 JSON 对象。" or "自定义请求体必须为空或有效的 JSON 对象。")
                AdvancedSettings.IsExpanded = true;
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
                _loadedBaseUrl = form.BaseUrl;
                _hasSavedApiKey = true;
                _hasSavedSecondaryCredential = NeedsSecondary(form.Protocol);
                ApiKeyBox.Password = string.Empty;
                SecondaryKeyBox.Password = string.Empty;
                UpdateKeyHint();
                _baseline = CaptureForm(currentOnly: true);
            }
            if (!result.Success && result.Message == "高级请求设置无效，请检查 JSON 和受保护的请求字段。")
            {
                Navigation.SelectedItem = ServiceNavigation;
                AdvancedSettings.IsExpanded = true;
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
        QwenModelBox.IsEnabled = !busy;
        QwenRegionBox.IsEnabled = !busy;
        ApiKeyBox.IsEnabled = !busy;
        TimeoutBox.IsEnabled = !busy;
        AdvancedSettings.IsEnabled = !busy;
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
                Title = Localize("删除服务？"),
                Content = Localize("此操作将删除当前服务配置及其保存的密钥。"),
                PrimaryButtonText = Localize("删除"),
                CloseButtonText = Localize("取消"),
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
            Title = Localize("放弃未保存的更改？"),
            Content = Localize("切换或修改服务列表会放弃当前页面未保存的设置和密钥输入。"),
            PrimaryButtonText = Localize("放弃更改"),
            CloseButtonText = Localize("返回编辑"),
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
        var model = SelectedModel.Trim();
        var target = SelectedTargetLanguage;
        if (SelectedProtocol == "AlibabaTranslate" && (target == "繁體中文" || SelectedSourceLanguage == "繁體中文"))
        {
            error = "阿里云机器翻译暂不支持所选的繁體中文；请选择其他语言或服务。";
            return false;
        }
        if (name.Length == 0 || target.Length == 0 || ProtocolBox.SelectedItem is not ComboBoxItem selected || selected.Tag is not string protocol ||
            (NeedsModel(protocol) && model.Length == 0))
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
        if (IsAdvancedProtocol(protocol) && !IsJsonObjectOrEmpty(CustomHeadersBox.Text))
        {
            error = "自定义请求头必须为空或有效的 JSON 对象。";
            return false;
        }
        if (IsAdvancedProtocol(protocol) && !IsJsonObjectOrEmpty(CustomBodyBox.Text))
        {
            error = "自定义请求体必须为空或有效的 JSON 对象。";
            return false;
        }
        form = new SettingsForm(_profileId, name, protocol, baseUrl, NeedsModel(protocol) ? model : "", ApiKeyBox.Password,
            CanUseSavedCredential && _hasSavedApiKey, timeout, Optional(SelectedSourceLanguage), target,
            Optional(StyleBox.Text), selection, whole,
            NeedsSecondary(protocol) ? SecondaryKeyBox.Password : "", CanUseSavedCredential && _hasSavedSecondaryCredential,
            IsAdvancedProtocol(protocol) && !string.IsNullOrWhiteSpace(CustomHeadersBox.Text) ? CustomHeadersBox.Text : "",
            IsAdvancedProtocol(protocol) && !string.IsNullOrWhiteSpace(CustomBodyBox.Text) ? CustomBodyBox.Text : "");
        return true;
    }

    private static bool IsJsonObjectOrEmpty(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
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
        ApiKeyHint.Text = Localize(CanUseSavedCredential && _hasSavedApiKey ? "已保存凭据；留空可继续使用，输入新值可替换。"
            : SelectedProtocol == "QwenMt" && _loadedProtocol == "QwenMt" && _hasSavedApiKey
                ? "服务地址已更改，请输入对应地域的 API Key。"
                : "凭据会在保存时交由应用安全存储。");
        SecondaryKeyHint.Text = Localize(CanUseSavedCredential && _hasSavedSecondaryCredential ? "已保存密钥；留空可继续使用，输入新值可替换。" : "密钥会在保存时交由应用安全存储。");
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
