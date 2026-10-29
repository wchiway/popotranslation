# Windows EXE 安装包构建

查证日期：2026-10-29。本配置使用 Inno Setup 7.1.0 生成 x64 自包含应用的 setup EXE；现有 WiX MSI 项目仍保留，其真实发布目录构建曾因 Windows Installer 校验失败，不能作为可分发的替代品。

首次安装默认路径为系统的 32 位 Program Files 目录下的 `PopoTranslation`，通常是 `C:\Program Files (x86)\PopoTranslation`，实际盘符由系统决定。需要管理员权限，安装器仅允许 x64 Windows，其他架构未验证。开始菜单提供应用入口；安装完成后不会自动启动。安装器使用固定 AppId，后续使用相同 AppId 的 Inno 安装包默认复用之前选择的安装目录；用户仍可选择其他目录，变更目录后的旧文件需要自行检查。Inno 安装包不自动识别或迁移此前 WiX MSI 安装记录，不应将其视为 MSI 的自动升级。

安装器启动时检查当前会话中的应用单实例互斥锁，发现应用运行时提示用户退出或取消；不会强制关闭应用。卸载或安装前请手动退出所有会话中的应用，避免在安装过程中重新启动应用。这个检查不能保证检测到其他 Windows 会话，也不能消除检查后的启动竞争；管理员提权后能否识别原用户会话的实例尚未实测。

用户数据不由安装器管理：首次使用时默认位于 `%USERPROFILE%\.PopoTranslation`；若 `%LOCALAPPDATA%\PopoTranslation` 已存在，则继续使用旧目录，即使新目录也存在。安装、卸载不会主动迁移或删除这两处数据。不要将个人数据放进应用安装目录。

## 构建

需要 Windows、.NET 10 SDK、可构建 WinUI 的 Visual Studio MSBuild、Windows SDK、NuGet 依赖和 Inno Setup 7.1.0 编译器。官方 [Inno Setup 下载页](https://jrsoftware.org/isdl.php)提供安装器及签名验证说明。编译器自身可按官方文档使用 `/PORTABLE=1 /CURRENTUSER /NOICONS /TASKS=` 部署到独立目录；商业使用请查看官方许可。

在仓库根目录使用 Visual Studio MSBuild 发布到全新、独立的目录，按本机情况修改路径。MSBuild 还会使用项目既有的中间输出目录，因此请先确认这些目录可用于构建。

```powershell
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw '找不到 Visual Studio MSBuild' }
$buildRoot = Join-Path $env:TEMP 'PopoTranslation-build'
$publish = Join-Path $buildRoot 'publish'
if (Test-Path -LiteralPath $publish) { throw '发布目录已存在，请选择新目录' }
& $msbuild .\popotranslation.csproj -restore -target:Publish -property:Configuration=Release -property:Platform=x64 -property:WindowsAppSDKSelfContained=true "-property:PublishDir=$publish\"
if ($LASTEXITCODE -ne 0) { throw '发布失败' }
```

现有发布配置指定 `win-x64`、.NET 自包含和非单文件，上面的命令额外启用 Windows App SDK 自包含。发布目录不能是用户数据目录，须事先人工核查内容：脚本递归打包目录，排除常见设置、数据库、日志、证书、环境文件及调试符号，但不能识别所有私密文件或多余文件。务必确认目录不包含凭据、个人数据、其他非分发文件及重解析点。

使用新建的空输出目录，避免覆盖现有安装包。安装包命名为 `PopoTranslation.{version}.Setup.x64.exe`，例如 `PopoTranslation.1.0.0.Setup.x64.exe`。版本号应随发行递增；每次打包前检查输出文件不存在。以下例子使用独立编译器位置，按实际路径修改。

```powershell
$iscc = '<Inno Setup 安装目录>\ISCC.exe' # 替换为本机编译器路径
$output = Join-Path $buildRoot 'setup-output'
$version = '1.0.0'
if (Test-Path -LiteralPath $output) { throw '输出目录已存在，请选择新目录' }
New-Item -ItemType Directory -Path $output | Out-Null
& $iscc /NI "/DPublishDir=$publish" "/DAppVersion=$version" "/O$output" .\installer\PopoTranslation.iss
if ($LASTEXITCODE -ne 0) { throw '安装包构建失败' }
```

仅构建成功不代表可正式分发。应用和安装包尚未完成 Authenticode 签名；尚未在干净机器上实测安装、运行、升级、卸载和重启。自包含配置也不能证明所有系统前提均已满足，尤其需要在目标系统验证 Visual C++ 运行库及实际 Windows API 依赖；安装器不会检查或安装额外前置组件。

参考资料：

- [Inno Setup 下载和签名验证](https://jrsoftware.org/isdl.php)、[目录常量](https://jrsoftware.org/ishelp/topic_consts.htm)、[编译器参数](https://jrsoftware.org/ishelp/topic_compilercmdline.htm)、[便携安装脚本](https://github.com/jrsoftware/issrc/blob/is-7_1_0/isportable.iss)。
- [Windows App SDK 自包含部署](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)。
