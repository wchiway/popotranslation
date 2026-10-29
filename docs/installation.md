# Windows 安装包构建

查证日期：2026-10-29。

当前配置仍处于验证中：使用实际 x64 自包含发布目录构建时，Windows Installer 校验报告 ICE03（部分资源文件的语言 ID 无效，两个 DLL 的语言列表超出字段长度），构建失败，不能用于分发。下面的命令用于继续排查，并非已验证成功的操作。

安装项目独立于主解决方案，使用 NuGet 还原 WiX SDK 6.0.2，不需要全局安装 `wix`。它配置为使用 x86 MSI 和 32 位纯文件组件打包 x64 应用，并限制在 64 位 Windows 上安装。默认目录为系统的 `ProgramFilesFolder`（在 64 位 Windows 上通常是 `C:\Program Files (x86)\PopoTranslation`，具体盘符由系统决定），需要管理员权限。应用只发布 x64，其他架构未验证。MSI 不包含开始菜单快捷方式或自动启动配置；安装后的启动入口需后续实现并验证。

## 构建

需要 Windows、.NET 10 SDK、可构建 WinUI 的 Visual Studio MSBuild、Windows SDK 和 NuGet 网络访问。先在仓库根目录使用本机 Visual Studio 的 MSBuild 发布，路径根据实际安装位置调整：

```powershell
$msbuild = 'D:\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
& $msbuild .\popotranslation.csproj -restore -target:Publish -property:Configuration=Release -property:Platform=x64 -property:WindowsAppSDKSelfContained=true
```

现有 `win-x64.pubxml` 指定 `win-x64`、.NET 自包含和非单文件；上面额外启用 Windows App SDK 自包含。默认发布目录为 `bin\Release\net10.0-windows10.0.19041.0\win-x64\publish`。发布前使用干净的输出目录，打包前检查其中没有凭据、个人设置、日志和不应分发的文件：安装项目递归收集整个目录。项目对 settings.json、preferences.json、数据库、日志、密钥文件和 logs、secrets 目录设置排除与检查，但不能识别所有敏感文件，人工检查仍然必要。构建不会自动发布或清理目录；不要覆盖已有输出目录。

```powershell
dotnet build .\installer\PopoTranslation.wixproj -c Release -p:InstallerVersion=1.0.0
```

成功构建后的默认 MSI 位于 `installer\bin\Release`，也可用 `-p:PublishDir=<绝对路径>` 指定已准备的 x64 发布目录。版本应在每次发布时递增，采用 Windows Installer 支持的版本格式，避免重复版本覆盖或降级。升级使用固定 UpgradeCode，安装包不管理用户数据。不要把此 MSI 当作已经验证过的正式发布物：签名、安装、升级、卸载、重启和干净 Windows 环境运行都需另行验证。

首次使用的用户数据目录为 `%USERPROFILE%\.PopoTranslation`；若 `%LOCALAPPDATA%\PopoTranslation` 已存在，则继续使用旧目录，即使新目录也存在。卸载不删除这两处数据。退出正在运行的应用后再进行安装或升级，避免占用文件。

.NET 和 Windows App SDK 分别设置为自包含，不能据此推断所有目标机器依赖均已满足；尤其需要在干净机器核实 Windows App Runtime、Visual C++ 运行库及所用 API 的实际依赖。此配置不安装或检查其他前置组件。

参考资料：

- [WiX SDK 项目](https://github.com/wixtoolset/wix/blob/main/src/wix/WixToolset.Sdk/README.md)、[Files 元素](https://docs.firegiant.com/wix/schema/wxs/files/)、[MSBuild 属性](https://docs.firegiant.com/wix/tools/msbuild/)。
- [Windows App SDK 自包含部署](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)。
