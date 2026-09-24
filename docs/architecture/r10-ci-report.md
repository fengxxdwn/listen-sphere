# R10 CI 阶段报告

日期：2026-09-24。分支：`codex/r10-ci`。基线：本地 `main` 的 R9 验收提交 `83ed612`。

## 实际修改

- 新增 Windows 和 Android GitHub Actions 工作流。两者在 Pull Request、`main` push 以及手动触发时运行。
- Windows：按照 `global.json` 安装 .NET 10 SDK，restore、Release 构建、全量测试、独立架构与协议门禁、Git 差异空白检查、文件尺寸报告、TRX 上传。
- Android：JDK 17、Android SDK 34 / Build Tools 34、Gradle 缓存和 wrapper 校验；构建 Debug APK，执行单元测试，分别上传测试 XML 与 APK。
- 增加项目引用边界与 Git 索引卫生测试；生成不阻断构建的源文件尺寸报告。CI 只上传产物，不提交 APK、TRX 或构建目录。

## 文件变更

- `.github/workflows/windows-ci.yml`：Windows 工作流。
- `.github/workflows/android-ci.yml`：Android 工作流。
- `tests/ListenSphere.Architecture.Tests/GovernanceTests.cs`：依赖边界、应用测试依赖和 Git 索引卫生测试。
- `scripts/Write-ListenSphereFileSizeReport.ps1`：扫描 `src`、`apps`、`tests` 中的 C#、XAML、Kotlin 文件，生成 Markdown 尺寸报告。
- 本报告。未修改产品业务代码、协议、配置或 UI，也未创建 Release Pipeline。

## 架构变化

Core 的直接项目引用按批准的依赖图执行精确约束：Engine → Audio.Abstractions；Network → Device、Protocol；其余核心项目不增加 ListenSphere 项目引用。Windows 适配项目只可直接引用核心项目，应用项目不得引用测试项目。原有程序集层面的 Windows/UI 禁止引用检查继续保留。

Git 索引检查禁止跟踪 `bin`、`obj`、IDE/Gradle 临时目录、`TestResults`、APK、日志、用户设置文件及 `*_wpftmp.csproj`。检查 Git **索引**，不根据本地缓存是否存在作判断。

## 行为变化

用户端运行行为无变化。R10 仅增加代码提交与合并时的自动门禁。文件尺寸超过 50 KiB 只进入报告，由阶段评审判断职责边界，不按行数机械拒绝构建。

## 自动测试

- `dotnet restore ListenSphere.sln`：通过。
- `dotnet build ListenSphere.sln -c Release --no-restore -m:1 -warnaserror`：0 警告、0 错误。
- 全量 .NET：212/212（Architecture 11、Core 105、Protocol 14、Windows Technical 82）。独立架构门禁 11/11、独立协议门禁 14/14。
- Android：JDK17 / SDK34 / Gradle8.10.2。`assembleDebug testDebugUnitTest --no-daemon --offline --rerun-tasks` 完整重跑；XML 36/36，失败/错误 0。Debug APK 已生成。
- 两份 YAML 均可解析，含预期的 `pull_request`、`push`、`workflow_dispatch` 事件与构建/上传步骤；没有本地安装 `actionlint`，GitHub 运行环境的实际执行仍待验证。
- `git diff --check` 通过；文件尺寸报告扫描 272 个文件，仅 `ControllerNetworkRuntime.cs`（91,461 B）超过 50 KiB。该文件是既有 R2/R3 展示层运行时协调代码，属于已知待审查大文件；R10 不通过改变其职责或拆分业务代码来消除报告项。

第一次本地 Release 构建遇到旧 Controller 进程 `3396` 锁定 DLL；核实它确实为本仓库 Release 可执行文件后关闭，重新构建通过。此问题不涉及干净的 CI runner。

## 人工测试

本阶段无用户界面或音频行为改动，因此没有另行启动程序做视觉/听感回归。当前本地编译、测试、XML、APK、报告已验证；GitHub Actions 真实运行尚未验证。按阶段流程保留在独立分支，待远端 PR 的 Windows/Android 检查均成功后再请用户验收并合并。

## 已知风险

- GitHub 远端此前发生连接超时。若分支推送失败，工作流不会在 GitHub 上运行；本地成功不等于 CI 已通过。
- GitHub 托管 runner 的具体 SDK 缓存和依赖下载情况只能由真实运行验证；依赖下载失败时保留错误，不改依赖版本绕过。
- GitHub 仓库可能需要启用 Actions 与满足私有仓库运行配额，尚未读取远端设置。
- 91,461 B 的既有大文件保留为架构审查项，不作为 R10 临时拆分目标。

## 阶段是否通过

R10 实现与本地门禁通过；远端 Windows/Android 工作流尚未实跑，阶段验收待完成。不自动合并 `main`，不进入 Release Pipeline。

参考：GitHub [setup-dotnet](https://github.com/actions/setup-dotnet)、[setup-java](https://github.com/actions/setup-java)、[upload-artifact](https://github.com/actions/upload-artifact)，[Gradle setup-gradle](https://github.com/gradle/actions/tree/main/setup-gradle)，[Android setup-android](https://github.com/android-actions/setup-android)。
