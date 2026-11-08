# 自动构建、发布与插件索引同步

仓库所有者持续授权对已完成并验证的 DalamudMCP 更新进行版本递增与发布，无需再次询问。源码仓库为 `NiGuangOwO/DalamudMCP`，分发索引仓库为 `NiGuangOwO/DalamudPlugins`。新安装包只上传到源码仓库的 GitHub Release；分发仓库仅同步 JSON 清单和来源记录，不再重复提交新 ZIP。

## 正常发布

1. 检查本地与远程状态，保留无关改动。在 `src/DalamudMCP/DalamudMCP.csproj` 递增四段 `Version`，高于已发布的稳定版本。
2. 准备 `docs/release-<Version>.md`，先 `## 简体中文`、后 `## English`。两种语言完整对应行为变化、实际验证结果和限制。不得把离线检查写成现场验证，或把 Skill 推荐修订写成客户端已安装版本。
3. 完成相关本地验证，提交到 `main` 并推送。正式分发使用 Actions 本次生成的包，不上传另一个不同构建的安装包。游戏关闭时，不为发布启动游戏。
4. 跟进 [.github/workflows/release.yml](../.github/workflows/release.yml) 的所有任务。`plan` 检查版本和已发布状态；`build` 调用构建工作流，构建 Debug/Release、运行协议、stdio 桥接和发布回归，打包 Skill 和需要 .NET 10 的桥接程序；`publish` 校验产物并创建 Release。
5. 新版本必须具备 `latest.zip`、`DalamudMCP.json`、`dalamud-development-skill.zip`、`dalamud-development.manifest.json`、`dalamud-development.payload.json`、`dalamud-mcp-bridge.zip` 和 `release-provenance.json`。校验包内/外清单、身份、版本、依赖清单、Skill 哈希及 GitHub 资产 SHA-256，发布后重新下载。CI Artifacts 只用于任务间传递，不是安装器下载地址。
6. `sync` 使用只绑定分发仓库的部署密钥，先下载并验证正式 Release，再更新 `plugins/DalamudMCP/DalamudMCP.json`、`plugins/DalamudMCP/release.json` 与 `pluginmaster.json`。安装/更新 URL 固定为对应版本的 Release 资产。一次提交包含清单与已生成索引，不依赖下一次工作流再生成 JSON。
7. `sync` 推送后读取远程 main 的确切提交，从该提交下载 `pluginmaster.json` 核对身份、版本、文案、下载链接和发布时间，并重新校验安装包 SHA-256。只有整个流程成功，才能报告发布完成。记录源码提交、标签、Release、分发提交、运行链接及验证结果。

已发布标签和资源不得删除、移动或覆盖。相同版本的安装包哈希改变会被拒绝；新构建必须使用新版本。其他插件的条目、资源和未变更的时间戳必须保留。推送冲突会从最新分支重新生成索引并重试，不使用强制推送。

## 配置与修复

- 源码仓库 Actions Secret `PLUGIN_REPO_DEPLOY_KEY` 是专用 SSH 私钥，对应公钥只给 `NiGuangOwO/DalamudPlugins` 写权限。不要改用个人令牌或在日志、工作流文件中保存私钥。
- 发布流程串行执行且不中止正在运行的发布。构建或上传失败先诊断，再重跑失败任务以复用同一份产物。草稿中的不同哈希资源也不会被静默替换。
- Release 已发布但索引同步失败时，在源码仓库的 **Release and plugin repository → Run workflow** 选中 `sync_only`，仅同步当前版本，不重新构建或重复上传。
- 分发仓库的 **Regenerate PluginMaster** 支持手动执行，并在每小时第 7、22、37、52 分钟校对最新正式 Release。该流程只需分发仓库自己的 `GITHUB_TOKEN`，可在跨仓库部署密钥失效时补偿同步。定时触发可能延迟，不能拿“已安排校对”当成“已完成同步”。
- raw GitHub/CDN 索引可能暂时缓存旧版本。先核对固定提交的索引与包；用户仍需刷新插件安装器。无法保证离线用户、所有网络或所有客户端即时看到更新。
- 自动校验失败会使工作流失败，不隐藏异常、不自动回滚到旧版本，也不宣称发布成功。真实权限或凭据问题才需要用户协助。

## 回归与发布范围

```powershell
python -m unittest discover -s tests/ReleasePipelineTest -p "test_*.py"
```

分发仓库同时运行 `python -m unittest discover -s tests -p "test_*.py"`。覆盖外部 Release 清单、固定版本链接、旧 ZIP 回退、时间戳稳定、其他插件保留、幂等及并发冲突重试。

插件代码变更仍需相关离线测试；注册/加载变化运行 `tests/PluginLoadTest`，协议变化运行 `tests/ProtocolSmokeTest`。Skill 变化时先运行 `python build/update-guidance-baselines.py` 保留历史原版指纹，再验证 C# 和 Python Skill 同步测试及实际运行构建的内容修订。自动发布不替代需要的游戏内验证。

纯文档、发布记账和工作流维护不必单独递增插件版本。未修改版本时，不重复创建 Release；本次基础设施接入使用 1.0.0.46 做完整构建、发布和索引同步验收。
