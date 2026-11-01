# 插件端发布流程

仓库所有者要求：每次完成并提交经过验证的 DalamudMCP 插件端更新后，必须递增版本号、创建 GitHub Release，并同步发布到其 Dalamud 插件仓库。该范围已获得持续授权，无需再次询问是否发布。

源码仓库：`NiGuangOwO/DalamudMCP`。插件分发仓库：`NiGuangOwO/DalamudPlugins`，本机通常位于 `C:\Github\DalamudPlugins`。

## 发布步骤

1. 检查两个仓库的工作区、远程分支、已有标签和 Release，以及分发仓库当前的 `AssemblyVersion`。保留无关的本地修改，避免覆盖其他插件。版本和网络配置必须现场核实。
2. 在 `src/DalamudMCP/DalamudMCP.csproj` 中递增 `Version`，新版本必须高于当前源码和分发仓库中的版本。常规兼容更新递增四段版本的最后一段；有更大版本规划时遵循所有者要求。版本号应在发布构建前更新，可与功能一并提交，或在功能提交后追加发布提交。
3. 构建 Debug 和 Release，运行与改动相关的检查。插件注册或加载相关改动运行 `tests/PluginLoadTest`；协议相关改动运行 `tests/ProtocolSmokeTest`。保存验证范围和结果，不把离线检查描述成游戏内验证。游戏已经关闭时，不为发布重新启动游戏。
4. 检查 `src/DalamudMCP/bin/x64/Release/DalamudMCP/latest.zip` 及同目录 `DalamudMCP.json`。确认压缩包内程序集、清单和外部清单的版本一致，作者为 `NiGuangOwO`、InternalName 为 `DalamudMCP`，依赖与资源完整。记录 ZIP 的 SHA-256。
5. 提交并推送源码与版本号；为确切的发布提交创建 `v<Version>` 标签并推送。创建同名的正式 GitHub Release，附上本次生成的 `latest.zip` 和 `DalamudMCP.json`，使用简体中文和英文双语说明行为变更、验证结果和实际限制。不得移动或覆盖已经发布的同名标签；重试时先核对现有 Release 的提交及资源。
6. 将同一构建的 `latest.zip`、`DalamudMCP.json` 和需要更新的发布资源同步到分发仓库 `plugins/DalamudMCP/`。保留其他插件，提交并推送分发更新。不要重新构建另一份 ZIP 来替换已经上传的 Release 资源。
7. 等待分发仓库 `.github/workflows/regenerate.yml` 成功生成 `pluginmaster.json`。核对 DalamudMCP 的版本、作者、安装及更新下载链接，再下载对应包并比较 SHA-256。若 raw GitHub 缓存尚未刷新，使用分发提交固定的索引和包验证准确内容，并说明缓存情况。
8. 检查两仓库远程引用与本地提交一致，报告版本、源码提交、Release 链接、分发提交、索引及包校验结果。上传成功或工作流启动不等于发布已验证完成。

## Release 说明语言

后续每次 GitHub Release 的正文均提供简体中文和英文两部分，默认先中文、后英文，分别使用 `## 简体中文` 和 `## English`。两种语言应完整对应本次变更、验证结果和实际限制；不能只翻译标题或用一句英文摘要替代完整英文说明。发布前核对两部分的版本、工具名称、测试数量、链接和限制是否一致。

## 范围和失败处理

此流程适用于实际随插件分发的代码、工具、服务行为、依赖、图像和插件元数据更新。仅修改文档、发布流程说明、发布记账文件，或仅修改独立小游戏开发插件时，不递归触发 DalamudMCP 二进制发布。

认证、构建、测试或远程校验失败时，保留已经完成的工作并报告具体阻塞；不得报告未完成的发布成功，也不得为解决认证问题输出凭据。遇到真实权限不足或不在授权范围内的操作时，再明确说明需要的用户操作。
