# PopoTranslation

PopoTranslation 是一个使用 C#、.NET 10 和 WinUI 3 构建的 Windows 桌面 AI 翻译工具。程序常驻托盘，**连续按三次空格**后会读取当前输入框，调用配置的 AI 服务翻译，并尝试回填；无法安全回填时保留译文供手动复制。

## 已实现

- WinUI 3 双栏设置窗口、托盘常驻、单实例运行。
- 默认识别方式：连续三次空格；同时保留可配置的选区/整框备用快捷键。
- 多行和富文本文本的读取：优先使用 UI Automation；QQ、微信、Discord、Telegram 和记事本等应用在无法暴露可编辑文本控件时使用剪贴板兼容路径。
- 多行/富文本回填：标准可写 ValuePattern 直接设置；其他社交软件通过剪贴板粘贴；终端只读取选中的文本，不自动改写。
- 多个翻译服务配置的新增、切换和删除；密钥使用当前 Windows 用户的 DPAPI 保护。
- 四种 AI 协议：OpenAI Chat Completions、OpenAI Responses、Anthropic Messages、Google Gemini 原生 generateContent。
- 机器翻译服务：阿里云、百度翻译、有道智云、DeepL，以及百炼 Qwen-MT 专用翻译接口；账号凭据分别使用 DPAPI 保护。
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

机器翻译设置中选择对应服务，填写阿里云 AccessKey ID/Secret、百度 APP ID/密钥、有道应用 ID/密钥或 DeepL API Key。DeepL 默认使用免费版地址，付费版需填写官方 API 地址。机器翻译不使用 AI 风格设置；当前只映射部分常用语言，阿里云暂不支持繁体中文。服务商可能限制请求长度或改变换行；不完整的多行结果不会自动回填。真实服务的可用性与费用以各厂商为准。

百炼 Qwen-MT 请选择“百炼 Qwen-MT”，填写对应地域的百炼 API Key，并选择 `qwen-mt-flash`、`qwen-mt-plus` 或 `qwen-mt-lite`。默认使用北京地域，也可选择新加坡或填写业务空间的兼容模式地址；API Key 必须与地域匹配。更多参数及语种限制见 [官方文档](https://docs.bailian.console.aliyun.com/zh/model-studio/machine-translation)。

## 诊断日志

程序将输入捕获和剪贴板操作的诊断事件保存在 `%LOCALAPPDATA%\PopoTranslation\logs`。日志包含目标进程名、窗口句柄、时间、操作结果和错误类型，不包含输入文字、译文、剪贴板内容或 API Key。每个进程使用独立文件，单文件最多约 2 MiB；旧日志不会自动删除，可按需手动清理。日志写入失败不会影响翻译。

## 限制

自动回填依赖目标控件或剪贴板兼容路径，跨进程检查与写入不是原子操作；输入法组合中的内容、部分自绘控件、受保护窗口和终端输入不会被自动替换。QQ、微信、Discord、Telegram 的实际客户端版本、权限和输入法状态需要逐个实测。剪贴板兼容模式会短暂占用系统剪贴板，并尝试恢复原内容；若系统或目标应用拒绝恢复，剪贴板可能保留译文。详细边界见 [兼容性说明](docs/compatibility.md)。

程序写入的临时标记和译文会请求 Windows 不将其加入剪贴板历史或同步到其他设备。通过目标应用复制的原文不受该标记控制，仍可能出现在系统或第三方剪贴板历史中；既有历史记录不会自动删除。

工具不会自动发送消息，不记录原文或译文，也不自动执行原文中的指令。API Key 和个人配置仅保存在本机，不进入仓库。

## 开发

构建、测试和已知限制见 [docs/development.md](docs/development.md)；模块关系见 [docs/architecture.md](docs/architecture.md)。
