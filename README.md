# PopoTranslation

PopoTranslation 是一个使用 C#、.NET 10 和 WinUI 3 构建的 Windows 桌面 AI 翻译工具。程序常驻托盘，**连续按三次空格**后会读取当前输入框，调用配置的 AI 服务翻译，并尝试回填；无法安全回填时保留译文供手动复制。

## 已实现

- WinUI 3 双栏设置窗口、托盘常驻、单实例运行。
- 默认识别方式：连续三次空格；同时保留可配置的选区/整框备用快捷键。
- 多行和富文本文本的读取：优先使用 UI Automation；QQ、微信、Discord、Telegram 等应用在无法暴露可编辑文本控件时使用剪贴板兼容路径。
- 多行/富文本回填：标准可写 ValuePattern 直接设置；其他社交软件通过剪贴板粘贴；终端只读取选中的文本，不自动改写。
- 多个 AI 服务配置的新增、切换和删除；密钥使用当前 Windows 用户的 DPAPI 保护。
- 四种独立协议：OpenAI Chat Completions、OpenAI Responses、Anthropic Messages、Google Gemini 原生 generateContent。
- 手动连接测试；连接测试不会在保存设置时自动执行。
- 过期任务、取消、超时、拒绝、截断和思考块过滤。
- 单实例运行：再次启动会唤醒已有实例。

默认连续三次空格会翻译整个输入框；第三次空格会被拦截，前两次空格会从翻译源文本中去除。终端中不启用三次空格，请先选中文本并使用选区备用快捷键。

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

在 Visual Studio 2026 中打开 `popotranslation.slnx`，选择 `x64`，启动 `popotranslation`。打开托盘菜单中的设置，至少填写一个服务配置、模型、API Key 和目标语言并保存。主触发方式是连续三次空格；备用快捷键默认为 `Ctrl+Alt+F9`（选区）和 `Ctrl+Alt+F10`（整个输入框），可在设置中修改。备用组合被占用时保存仍会成功，三次空格仍可使用。

## 限制

自动回填依赖目标控件或剪贴板兼容路径，跨进程检查与写入不是原子操作；输入法组合中的内容、部分自绘控件、受保护窗口和终端输入不会被自动替换。QQ、微信、Discord、Telegram 的实际客户端版本、权限和输入法状态需要逐个实测。剪贴板兼容模式会短暂占用系统剪贴板，并尝试恢复原内容；若系统或目标应用拒绝恢复，剪贴板可能保留译文。详细边界见 [兼容性说明](docs/compatibility.md)。

工具不会自动发送消息，不记录原文或译文，也不自动执行原文中的指令。API Key 和个人配置仅保存在本机，不进入仓库。

## 开发

构建、测试和已知限制见 [docs/development.md](docs/development.md)；模块关系见 [docs/architecture.md](docs/architecture.md)。
