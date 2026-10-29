# GitHub Actions 测试与发布

`Build and test` 在推送、拉取请求和手动触发时，使用 Windows 运行器构建解决方案并执行 Core、Infrastructure、Providers 测试和 Windows 检查。拉取请求不需要发布权限。

`Release` 从 Actions 页面手动运行，填写不带 `v` 的三段版本号（如 `1.2.3` 或 `1.2.3-rc.1`），选择 `release` 或 `prerelease`。正式版不允许预发布后缀，预发布版必须带后缀。默认只生成 Actions 构建产物；勾选 `publish` 后，工作流才会创建对应的 GitHub Release 和 `v` 前缀标签。发布仅允许从 `main` 触发，已有同名标签或 Release 时不会覆盖。发布前会运行同一提交的测试，成功后生成 EXE 安装包、应用发布目录的 ZIP 压缩包及其 SHA-256 校验文件。预发布的标签与产物名包含完整后缀，安装器中显示的版本为三段数字基础版本。

运行工作流需要先将这些文件推送到 GitHub，并在仓库启用 Actions；如仓库或组织限制 `GITHUB_TOKEN` 权限，需要允许该工作流创建 Release。生成的应用和安装包未签名，自动化通过不代表已完成干净机器安装、升级、卸载及目标应用兼容性验证；正式分发前仍应执行 [发布检查清单](release-checklist.md)。

GitHub Actions 工作流由手动触发的人员控制发布；仅提交或推送配置不会创建 Release。构建参数和依赖说明见 [安装包构建](installation.md)。
