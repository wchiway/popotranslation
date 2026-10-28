# 架构

PopoTranslation 是单进程 Windows 桌面程序。设置窗口本身是 WinUI 3；跨应用输入、托盘和热键通过 Win32 与 UI Automation 实现。

```text
WinUI SettingsPage
        |
ApplicationController (应用生命周期、配置、快捷键、翻译流程)
        |
        +-- PopoTranslation.Core        翻译契约、任务协调、配置契约
        +-- PopoTranslation.Infrastructure JSON 设置、DPAPI 凭据
        +-- PopoTranslation.Providers   四种 AI 协议适配
        +-- PopoTranslation.Windows     热键、托盘、UIA 读取与保守回填
```

## 启动与常驻

`App` 使用本机会话级命名互斥量保证只有一个实例。第二个实例通过命名事件唤醒第一个实例并退出；第一个实例隐藏消息窗口和托盘图标持续运行。关闭设置窗口时隐藏 `AppWindow`，从托盘选择“退出”才销毁控制器、消息窗口和 UI Automation 工作线程。

## 快捷键与读取流程

1. 全局热键在专用原生消息线程触发。
2. 控制器立即记录触发时的前台窗口，并启动 UI Automation 读取；不先等待 UI 线程。
3. 捕获必须满足：预期前台窗口、进程实例、控件运行时 ID、全文、选区一致；密码、只读和不支持的模式被拒绝。
4. 取得快照后才回到 UI 线程更新状态并读取密钥。
5. 翻译任务由 `TranslationCoordinator` 管理；新任务使旧任务失效，结果必须属于当前任务才能进入回填。

## 回填

- 整框自动回填只尝试标准 Win32 单行 Edit：前台焦点、进程实例、控件、原文、选区及可写状态全部一致，且使用非输入法英语布局；写入后读回一致才报告已应用。
- 任何检查失败、非标准控件、多行译文、选区或无法确认的情况都返回手动应用，结果保留在设置页供复制。
- 跨进程检查和写入不是原子事务，UIA 调用也可能阻塞；因此不承诺任意应用的撤销栈、输入法组合或原子替换。
- 用户手动点击“复制结果”后自行切换目标粘贴；不会自动发送消息。

## AI 协议

`ITranslationProvider` 接收原文、目标语言、源语言和风格。工厂根据配置创建 OpenAI Chat Completions、OpenAI Responses、Anthropic Messages 或 Gemini generateContent 适配器。生产传输禁用自动重定向、Cookie 和自动重试，统一使用无限 HTTP 超时，由任务取消令牌控制总期限。每个适配器只提取完整最终译文，拒绝思考块、工具调用、截断、拒绝和空响应。

## 配置与凭据

普通设置写入用户本地配置目录的版本化 JSON；密钥使用当前 Windows 用户的 DPAPI 保护，配置文件只保存凭据引用。设置保存采用临时文件替换并保留备份；损坏的现有配置不会被静默覆盖。应用不会记录原文或译文，错误消息只保留安全的错误码和通用说明。

## 线程

- WinUI 线程：界面和设置回调。
- 原生消息线程（STA）：热键、托盘和隐藏窗口。
- UI Automation 线程（MTA）：读取和回填。
- 线程池：网络请求和通知派发。

控制器避免在 UI 线程同步等待 UI Automation 或托盘原生回调。回填最终门禁可能因目标应用阻塞而等待，这是当前已知边界；不会声称取消令牌能终止已开始的同步 COM 调用。
