# GitHub Actions 测试与发布

`Build and test` 在推送、拉取请求和手动触发时，使用 Windows 运行器构建解决方案并执行 Core、Infrastructure、Providers 测试和 Windows 检查。拉取请求不需要发布权限。

`Release` 从 Actions 页面手动运行，填写不带 `v` 的三段版本号（如 `1.2.3` 或 `1.2.3-rc.1`），选择 `release` 或 `prerelease`。正式版不允许预发布后缀，预发布版必须带后缀。默认只生成 Actions 构建产物；勾选 `publish` 后，工作流才会创建对应的 GitHub Release 和 `v` 前缀标签。发布仅允许从 `main` 触发，已有同名标签或 Release 时不会覆盖。发布前会运行同一提交的测试，成功后生成 EXE 安装包、应用发布目录的 ZIP 压缩包及其 SHA-256 校验文件。预发布的标签与产物名包含完整后缀，安装器中显示的版本为三段数字基础版本。

发布前请在 GitHub 仓库的 Actions Secrets 中配置 `DEEPSEEK_API_KEY`。工作流调用 DeepSeek Responses API 生成中文发布描述，并将提交哈希、标题和正文发送给 DeepSeek；请勿在提交信息中包含凭据或私人信息。模型结果需要人工审阅，调用失败、不完整或超出大小限制时发布会中止。仅构建产物而不发布时无需该密钥。

版本区间从目标提交的 Git first-parent 链中最近的 `v` 前缀 SemVer 标签开始，包含正式版和预发布版标签（即使该标签没有关联 Release）；同一提交上存在多个候选标签时按标签名称字典序取最大值。提交列表包含区间内合并引入的分支提交。若没有前驱标签，则使用当前提交全部可达历史；前驱与目标在同一提交时，说明无新增提交。GitHub Actions 会重新检查标签范围，但无法与其他发布者的操作实现完全原子化。发布失败可能留下已创建的标签或草稿，需在 GitHub 检查。

运行工作流需要先将这些文件推送到 GitHub，并在仓库启用 Actions；如仓库或组织限制 `GITHUB_TOKEN` 权限，需要允许该工作流创建 Release。生成的应用和安装包未签名，自动化通过不代表已完成干净机器安装、升级、卸载及目标应用兼容性验证；正式分发前仍应执行 [发布检查清单](release-checklist.md)。

GitHub Actions 工作流由手动触发的人员控制发布；仅提交或推送配置不会创建 Release。构建参数和依赖说明见 [安装包构建](installation.md)。
