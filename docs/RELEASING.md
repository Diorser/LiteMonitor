# 构建与发布自动化

工作流为 `.github/workflows/delivery.yml`。不需要 PAT、额外 Secret、人工修改版本、打 Tag 或上传附件；发布只使用当前仓库的 `GITHUB_TOKEN`。

## 触发与权限

- 发往 `master` 的 PR 和合并队列执行自动化测试。涉及程序、运行资源、项目、测试或构建脚本时，还会在 Windows 2022 上编译、执行所有回归程序、打包并验证。普通 README、截图和维护文档变更只运行轻量检查，`CI` 检查仍会给出结果。
- `master` 的 push 自动检查与上次正式 Release 的运行源码指纹。指纹未变则不发布；有变化才构建并发布。使用完整 Git 历史比较，不依赖单次 push 的文件列表，因此失败后再次合并也不会遗漏变更。
- `ci/**` 分支支持推送后预验证，便于在创建 PR 前核实实际 Windows Actions。已有对应 PR 时优先由 PR 验证，避免重复 Windows 构建。
- 每天 UTC 03:43 的轻量检查补偿暂时的服务故障，无待发布变更时跳过 Windows 构建。也支持手动重跑和 `workflow_dispatch`。Fork 的计划任务可能需要仓库管理员先启用 Actions；GitHub 也会在公共仓库长时间无活动后停用计划任务，正常 push 触发不依赖计划任务。
- PR、计划和编译任务只有 `contents: read`，checkout 不保留 Git 凭证。仅 `master` 的发布任务获得 `contents: write`。不使用 `pull_request_target`，不会执行带发布权限的 PR 代码。
- `master` 串行处理且不取消正在发布的任务。PR 旧运行可取消。发布前再次确认 `master`，跳过过期构建；后续运行会包含尚未发布的变化。

建议把稳定的 `CI` 设为分支保护所需检查。仓库级 Actions 禁用、组织策略、首次外部贡献者工作流审批、Tag 规则和分支保护属于 GitHub 管理权限范围，工作流不会绕过它们。自动合并只在仓库允许且保护规则均满足时才可使用；本实现不自动更改保护或替用户合并 PR。

## 版本与重试

`Directory.Build.props` 的 `1.3.8` 是本地构建基线。正式发布取基线、有效的三段数字 Tag 及已有 Release 的最高版本，默认递增补丁；本仓库当前无历史 Tag/Release，第一次为 `v1.3.9`。预发行 Tag 不参与正式版本。补丁达到 Windows 文件版本上限时向次版本进位。

同一版本通过 MSBuild 属性传给主程序与 Updater 的 Version、AssemblyVersion、FileVersion 和 InformationalVersion。AssemblyVersion/FileVersion 的第四段为 0。Git Tag、Release 标题、ZIP、更新清单与文件版本一致，不产生版本号回写提交。

发布使用草稿作为可恢复事务：先保存提交、版本和源码指纹，再上传并验证三个附件，最后创建指向该提交的 Tag 并公开 Release。附件校验使用 GitHub 返回的 SHA256；服务端未返回 digest 时下载附件重新计算。网络错误自动重试同一事务，已有草稿及相同附件可复用，草稿中的残缺附件可重传。公开的版本不覆盖、不移动 Tag，不接受不同提交占用同一版本。编译或任一回归失败都无法进入发布任务。

发生持续错误时 Actions 明确失败，草稿保留用于重试。修复原因后重新运行即可；临时故障也可由下一次每日检查恢复。同一源码成功发布后再次运行不会递增版本。中途废弃的其他提交草稿仍保留为草稿，其版本继续保留，后续版本可有间隔。

## 构建与产物

使用 .NET 8 最新稳定 SDK，`global.json` 限定在 8.0 系列并接受服务更新；NuGet 依赖及内容哈希由各项目 `packages.lock.json` 锁定，CI 以 locked mode 还原并缓存 NuGet。GitHub 官方 Actions 固定到完整提交 SHA。源码编译启用确定性构建、CI 路径映射；ZIP 时间戳固定为源码提交日期。SDK、运行时服务补丁或 runner 镜像变化仍可能改变二进制，因此跨环境不承诺 ZIP 逐字节相同。

正式主程序为 `win-x64`、Release、self-contained、多文件发布，不裁剪 WinForms 或反射依赖。Updater 从解决方案中的源码单独发布为包含运行时的单文件 EXE，放在 `resources/LiteMonitor.Updater.exe`，不使用仓库旧预编译文件。FPS、驱动安装器与驱动包位于既有代码使用的 `resources` 根目录；图像由 `.resx` 嵌入。

正式 Release 附件：

- `LiteMonitor-vX.Y.Z-win-x64.zip`：根目录主程序、.NET 及第三方 DLL、`resources/themes`、`resources/lang`、`resources/plugins`、硬件组件、Updater、使用说明与 `release-files.json`。
- 同名 `.zip.sha256`：标准 SHA256 校验文件。
- `version.json`：本次发布的版本、日志、日期、ZIP 地址及 SHA256。

构建会检查所有必需文件、资源与源码的一致性、主程序及更新器版本、自带运行时、文件哈希清单；实际解压 ZIP 后再验证一遍。专用 `--smoke-test` 入口启动 WinForms 消息循环、加载主题/语言并检查 JSON 资源，不读取用户配置、不联网、不加载驱动或启动 FPS。它不等同于真实硬件监测和驱动安装验收。CI 直接启动正式 EXE 与 Updater；普通非提权本地验证可用 `-SmokeViaDotNet` 运行同一 DLL 的入口，不能据此声称已验证 EXE 的 UAC 启动。

现有 `test` 目录是可执行回归程序，工作流逐个 `dotnet run` 并检查退出码；`dotnet test` 没有测试适配器时的空成功不能替代它们。发布事务的 Node 测试使用内存 GitHub 模型，不会创建真实 Tag 或 Release。构建计划保留 7 天，ZIP 构建产物保留 14 天；正式 Release 附件长期保留。

## 更新兼容与用户数据

构建时将当前仓库名写入程序集元数据，默认本仓库为 `ChenBit/LiteMonitor`。应用仅从该仓库 `releases/latest/download/version.json` 获取正式更新，按清单下载 ZIP，并在替换 Updater 前校验 SHA256。无需维护 master 上的版本 JSON，不会因先更新清单而指向尚未发布的 ZIP，也不会回退下载原作者的其他构建。已有旧版本仍使用旧更新源，需先安装一次本仓库新包才能切换到此更新渠道。

Updater 使用 `release-files.json` 验证包内文件并识别内置资源。更新时不删除额外的用户文件，不覆盖用户配置、历史记录和正在执行的更新器。主题、语言、插件已有文件只有与上次发布清单中的原始哈希一致才自动替换，用户改动与自建文件会保留。首次从没有清单的旧安装更新时保留所有已有主题、语言和插件，避免误判用户改动；新增内置资源仍会补充。如果希望用新版内置模板替换自己修改的模板，可备份后从 ZIP 手动取用。

完整包附带 PawnIO 安装器，沿用原有的管理员授权与安装对话框；不会由 CI 或冒烟入口安装驱动。安装器缺失时才从当前仓库对应 Tag 恢复组件。

## 本地维护

Windows、.NET 8 SDK、Node.js 22+、PowerShell 7 环境下运行：

```powershell
node --test automation/release.test.mjs
node automation/release.mjs plan
./automation/build.ps1
```

修改 NuGet 引用后由维护者运行 `./automation/build.ps1 -UpdateLocks` 并提交更新的锁文件；普通发布不需要此开关或任何版本编辑。`artifacts/` 是可删除的构建输出，不提交到 Git。

.NET 8 的支持周期和 GitHub 托管 runner、官方 Actions 的平台策略会变化，系统不能替代未来的框架迁移和平台维护。定期检查 .NET 支持状态，并在框架升级时一起运行完整回归与打包链路。
