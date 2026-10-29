# 开发与构建

更新日期：2026-10-28。

## 前置环境

- Windows 11 x64。
- .NET SDK 10（本项目使用 `net10.0-windows10.0.19041.0`）。
- Visual Studio 2026（或包含 Windows App SDK / WinUI 组件的兼容构建工具），用于 WinUI XAML 构建。
- 项目根目录的 `NuGet.Config` 固定使用 `https://api.nuget.org/v3/index.json`，不依赖机器上的 Visual Studio 离线源。
- 项目使用 Windows App SDK `2.6.4` 和 Windows SDK BuildTools `10.0.26100.4654`，版本写在根项目文件中；升级前应查证兼容性。
- 根项目设置 `WindowsPackageType=None`，直接运行框架依赖的 `.exe` 时需要匹配的 Windows App Runtime。自包含部署的说明见 [安装包构建](installation.md)。

## 构建

使用 Visual Studio 的 MSBuild 构建 WinUI 项目：

```powershell
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw '找不到 Visual Studio MSBuild' }
& $msbuild popotranslation.slnx -restore -target:Build -property:Configuration=Debug -property:Platform=x64
```

如果 .NET SDK 的 NuGet 缓存所在磁盘空间不足，可以只为当前进程指定缓存目录：

```powershell
$cacheRoot = Join-Path $env:TEMP 'PopoTranslation-NuGet'
$env:NUGET_PACKAGES = Join-Path $cacheRoot 'packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $cacheRoot 'http'
$env:NUGET_SCRATCH = Join-Path $cacheRoot 'scratch'
```

这三个变量只影响当前 PowerShell 进程，不会修改系统环境变量。多个并发构建必须共用同一组缓存和临时目录。

不要直接执行普通的 `dotnet build` 作为 WinUI 的完整验证；WinUI XAML 构建和 MSIX/Windows App SDK targets 可能需要 Visual Studio 提供的 MSBuild。核心类库和测试可以使用 `dotnet test`。

## 运行

在 Visual Studio 中打开 `popotranslation.slnx`，选择 `x64`，启动 `popotranslation` 项目。程序启动后从托盘打开设置，填写服务类型、模型、API Key、目标语言，保存后在输入框中连续按三次空格。可在设置页通过按键录制备用选区和整框快捷键。

首次运行前应确保：

- 已保存至少一个服务配置和密钥；没有密钥时不会发起翻译。
- 备用快捷键被占用不会阻止保存；连续三次空格仍可使用。
- 输入框是支持的焦点控件。密码框、只读控件、无法确认身份的控件会被拒绝。
- QQ、微信、Discord、Telegram 使用剪贴板兼容路径时会短暂占用剪贴板；剪贴板含图片/文件时会保存并尝试恢复。
- 终端中三次空格被禁用；请选中文本后使用选区备用快捷键。

## 测试

```powershell
dotnet test tests/PopoTranslation.Core.Tests/PopoTranslation.Core.Tests.csproj --configuration Debug
dotnet test tests/PopoTranslation.Infrastructure.Tests/PopoTranslation.Infrastructure.Tests.csproj --configuration Debug
dotnet test tests/PopoTranslation.Providers.Tests/PopoTranslation.Providers.Tests.csproj --configuration Debug
dotnet run --project tests/PopoTranslation.Windows.Tests/PopoTranslation.Windows.Tests.csproj --configuration Debug
```

Providers 测试使用本机模拟 HTTP 传输，不会发送真实 AI 请求。Windows 测试是零 NuGet 依赖的可执行检查，不是 `dotnet test` 项目。自动化测试不能代替真实应用、真实输入法和真实服务连接的验收。

## 当前限制

- 自动回填仅保守支持标准 Win32 单行、可写、非密码 `Edit` 控件；其他控件转手动复制。
- 选区可以读取，但选区替换仍转手动；多行和富文本控件通常只能读取，不能自动写回。
- 输入法组合状态无法通用确认；中文输入法、管理员窗口、浏览器、Electron、微信/QQ 等目标需要逐个实测。
- 托盘通知可能被系统抑制；结果可从设置页的结果区复制。
- 修改配置与密钥不是跨文件事务；保存失败时不会覆盖旧设置，但极端情况下可能需要重新输入密钥。
- 没有真实 AI 调用和干净机器发布的自动验收；发布前需按 `docs/release-checklist.md` 人工验证。
