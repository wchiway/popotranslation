# PopoTranslation

PopoTranslation 是一个计划使用 C# 和 WinUI 3 构建的 Windows 桌面 AI 翻译工具。用户在其他应用输入文字后，按下全局快捷键，将内容翻译成预设语言，并在确认原编辑位置未变化后回填。

当前处于项目初始化阶段，尚未创建应用代码，也未完成构建、AI 联调或应用兼容性测试。

## 计划功能

- 系统托盘常驻，可配置全局快捷键、目标语言和翻译风格。
- 翻译选中文字；为经过验证的输入框提供整框翻译。
- 支持 OpenAI Chat Completions、OpenAI Responses、Anthropic Messages、Google Gemini 原生 generateContent 四种独立协议。
- 配置 AI 服务地址、模型和凭据；按协议及模型能力提供高级设置。
- 翻译失败、目标变化或用户继续输入时保留原文，并提供译文预览和手动应用入口。

## 技术方向

| 领域 | 计划选型 |
| --- | --- |
| 平台 | Windows 11 x64，.NET 10 LTS |
| UI | WinUI 3 / Windows App SDK |
| 输入框交互 | Windows UI Automation、Win32 输入模拟、按需剪贴板辅助 |
| AI | OpenAI、Anthropic、Google.GenAI 官方 .NET SDK，独立协议适配 |
| 配置 | JSON 普通设置与 Windows 用户范围凭据保护 |
| 部署 | 首先验证 Unpackaged 桌面部署，再进行干净环境验收 |

具体依赖版本和 SDK 调用方式在实施时根据最新官方文档及实际构建环境查证、锁定。

## 使用边界

跨应用兼容性取决于控件暴露的辅助功能、权限和编辑行为，需要逐个应用验证。工具不自动发送消息；密码框和无法确认的编辑位置不自动取文或回填。第一版以纯文本为范围，富文本样式和任意应用的撤销行为需另行适配。

AI 翻译会将用户主动触发的文本发送到配置的服务地址。API Key 和个人配置应仅保存在本机，不进入仓库。
