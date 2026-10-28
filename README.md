# PopoTranslation

PopoTranslation 是一个使用 C#、.NET 10 和 WinUI 3 构建的 Windows 桌面 AI 翻译工具。程序常驻托盘，在其他应用中按下全局快捷键后读取当前输入框或选区，调用配置的 AI 服务翻译，并在能够验证目标未变化时尝试原位回填；无法安全回填时保留译文供手动复制。

## 已实现

- WinUI 3 设置窗口、托盘常驻、可配置的全局快捷键。
- 多个 AI 服务配置的新增、切换和删除；密钥使用当前 Windows 用户的 DPAPI 保护。
- 四种独立协议：OpenAI Chat Completions、OpenAI Responses、Anthropic Messages、Google Gemini 原生 generateContent。
- 手动连接测试；连接测试不会在保存设置时自动执行。
- 翻译选中文字或整个输入框；显示翻译状态，并在无法回填时提供结果复制。
- 过期任务、取消、超时、拒绝、截断和思考块过滤。
- 单实例运行：再次启动会唤醒已有实例。

## 技术栈

| 领域 | 选型 |
| --- | --- |
| 平台 | Windows 11 x64，.NET 10 LTS |
| UI | WinUI 3 / Windows App SDK |
| 输入框交互 | Windows UI Automation、Win32 |
| AI | OpenAI、Anthropic、Google.GenAI 官方 .NET SDK，独立协议适配 |
| 配置 | JSON 普通设置与 DPAPI 用户凭据保护 |
| 部署 | 目前以 Unpackaged 桌面开发/运行流程为主 |

依赖版本锁定在项目中；构建前请阅读 [开发与构建](docs/development.md)。

## 运行

在 Visual Studio 2026 中打开 `popotranslation.slnx`，选择 `x64`，启动 `popotranslation`。打开托盘菜单中的设置，至少填写一个服务配置、模型、API Key、目标语言和快捷键并保存。默认快捷键为 `Ctrl+Alt+F9`（选区）和 `Ctrl+Alt+F10`（整个输入框），可在设置中修改；若组合已被其他应用占用，保存时会提示。

## 限制

自动回填仅保守支持标准 Win32 单行、可写、非密码 `Edit` 控件；其他控件、多行富文本、输入法组合中的内容、选区替换和无法确认的窗口会转为手动复制。工具的跨应用能力取决于目标应用的 UI Automation 支持、权限和编辑行为，不能承诺覆盖所有应用。详细边界见 [兼容性说明](docs/compatibility.md)。

工具不会自动发送消息，不记录原文或译文，也不自动执行原文中的指令。API Key 和个人配置仅保存在本机，不进入仓库。

## 开发

构建、测试和已知限制见 [docs/development.md](docs/development.md)；模块关系见 [docs/architecture.md](docs/architecture.md)。
