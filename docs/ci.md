# GitHub Actions 测试与发布

`Build and test` 在推送、拉取请求和手动触发时，使用 Windows 运行器构建解决方案并执行 Core、Infrastructure、Providers 测试和 Windows 检查。拉取请求不需要发布权限。

`Build and release` 在推送新建的 `v*` 版本标签时自动运行。标签须是三段版本号，例如 `v1.2.3` 创建正式 Release，`v1.2.3-rc.1` 创建 Pre-release。标签指向的提交须位于远程 `main` 的历史中。工作流对同一提交进行测试和构建，使用已推送的标签创建 Release；已有同名 Release 不会被覆盖。安装器、应用 ZIP 和 SHA-256 校验文件会作为发布附件。预发布的标签和产物名包含完整后缀，安装器中显示的版本为三段数字基础版本。

先将需要发布的提交和工作流推送到 `main`，再创建并推送标签，例如：

```powershell
git tag v1.2.3
git push origin v1.2.3

# 预发布示例
git tag v1.2.4-rc.1
git push origin v1.2.4-rc.1
```

也可在 Actions 页面手动触发构建，该方式只生成 Actions 构建产物，不会创建 Release。

发布前请在 GitHub 仓库的 Actions Secrets 中配置 `DEEPSEEK_API_KEY`。工作流调用 DeepSeek Responses API 生成中文发布描述，并将提交哈希、标题和正文发送给 DeepSeek；请勿在提交信息中包含凭据或私人信息。模型结果需要人工审阅，调用失败、不完整或超出大小限制时发布会中止。仅构建产物而不发布时无需该密钥。

版本区间从目标提交的 Git first-parent 链中最近的 `v` 前缀 SemVer 标签开始，包含正式版和预发布版标签（即使该标签没有关联 Release）；同一提交上存在多个候选标签时按标签名称字典序取最大值。提交列表包含区间内合并引入的分支提交。若没有前驱标签，则使用当前提交全部可达历史；前驱与目标在同一提交时，说明无新增提交。GitHub Actions 会重新检查标签范围，但无法与其他发布者的操作实现完全原子化。发布失败后已推送的标签仍会保留，且可能留下草稿，需在 GitHub 检查。

运行工作流需要先将这些文件推送到 GitHub，并在仓库启用 Actions；如仓库或组织限制 `GITHUB_TOKEN` 权限，需要允许该工作流创建 Release。生成的应用和安装包未签名，自动化通过不代表已完成干净机器安装、升级、卸载及目标应用兼容性验证；正式分发前仍应执行 [发布检查清单](release-checklist.md)。

推送符合规则的新版本标签会自动发布；仅推送分支不会创建 Release。请为 `v*` 标签配置保护规则，限制创建、更新和删除权限：标签可能在发布检查后被外部操作改变，工作流检查不能完全消除竞态。构建参数和依赖说明见 [安装包构建](installation.md)。
