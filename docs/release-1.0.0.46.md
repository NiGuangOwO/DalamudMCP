## 简体中文

- 新增基于项目版本号的自动发布：推送到 `main` 后执行 Debug、Release 构建、协议与 stdio 桥接检查及发布回归；新版本通过后创建 GitHub Release，一次上传安装包、清单、Skill、桥接程序及 SHA-256 来源记录。
- 安装器的安装与更新链接固定到对应版本的 Release。自动校验远程安装包与清单后，将清单、版本、发布时间和包哈希同步到 `NiGuangOwO/DalamudPlugins`，在同一提交生成 `pluginmaster.json`，并下载固定提交索引和对应安装包核验。分发仓库不再重复上传新 ZIP。
- 同步具备推送冲突重试、幂等检查、防降级和同版本换包拒绝；保留其他插件条目。增加分发仓库每小时四次的定时校对，以及不重新构建的 `sync_only` 手动修复入口。索引校验失败时发布流程报告失败。
- 验证：13 项发布回归和 6 项索引回归通过，包含真实本地 Git 远程的并发冲突与重复同步；使用既有 1.0.0.45 Release 进行了下载、哈希和索引迁移预检，14 个其他插件条目保持一致。新版本的构建、协议和桥接结果以本次 Actions 记录为准。
- 范围与限制：本次更新发布基础设施和插件版本号，插件功能不变，未重新执行游戏内验证。用户需要刷新插件安装器；GitHub raw/CDN 缓存、网络故障和定时任务延迟可能推迟更新显示，无法保证所有客户端即时收到更新。跨仓库发布依赖专用部署密钥及 GitHub 服务可用性。桥接程序需要 .NET 10，Skill 同步仍依赖客户端执行能力与权限。

## English

- Add version-driven automatic releases. Pushes to `main` run Debug/Release builds, protocol and stdio bridge checks, and publication regressions. A new version creates a GitHub Release with one upload of the plugin package, manifest, skill, bridge, and SHA-256 provenance.
- Pin installation and update URLs to the corresponding Release version. After downloading and verifying the remote package and manifest, synchronize the manifest, version, publication time, and package hash to `NiGuangOwO/DalamudPlugins`, regenerate `pluginmaster.json` in the same commit, and verify the pinned remote index and package. New ZIPs are no longer duplicated in the distribution repository.
- Add competing-push retries, idempotence, downgrade prevention, and rejection of same-version package replacement while preserving other plugins. Reconcile the distribution four times per hour and provide a `sync_only` manual repair path without rebuilding. Failed index verification fails the release workflow.
- Validation: all 13 publication regressions and 6 index regressions passed, including competing pushes against a real local Git remote and repeated synchronization. Preflight migration using the existing 1.0.0.45 Release verified downloads, hashes, and the index while preserving all 14 other plugin entries. Build, protocol, and bridge results for this version are recorded in its Actions run.
- Scope and limitations: this release changes publication infrastructure and the plugin version only; plugin functionality is unchanged and in-game behavior was not revalidated. Users must refresh the Plugin Installer. GitHub raw/CDN caches, network failures, and scheduled-run delays can postpone update visibility; immediate delivery to every client is not guaranteed. Cross-repository publication depends on the dedicated deploy key and GitHub availability. The bridge requires .NET 10; skill synchronization still depends on client execution capabilities and permissions.
