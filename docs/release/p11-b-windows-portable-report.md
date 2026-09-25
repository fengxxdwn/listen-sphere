# P11-B Windows Portable 阶段报告

## 版本与分支

- Product version：`0.6.0-beta.1`
- 分支：`codex/p11-b-windows-portable`
- P11-A / `main` 基线：`2ced6cfe3f7e37e457b0d0c5f343f21ca2cf8c56`
- P11-B 实现提交：`2f4c6158cb14138a76d90c08ba39191228a69647`

## 实际修改

- 新增 `scripts/Publish-ListenSphereWindows.ps1` 作为唯一的本地 Windows
  Portable 打包入口。
- 脚本严格从 `eng/ListenSphere.Version.props` 读取唯一且非空的
  `ListenSphereProductVersion`，不提供默认版本回退。
- 脚本清理并重建被 Git 忽略的 `artifacts/publish` 和
  `artifacts/packages`，随后 restore、Release build、全量测试、publish、
  Metadata/self-contained 检查、ZIP 和 SHA256。
- 新增 Architecture governance test，约束版本来源、发布参数、命名规则和
  `artifacts/` Git 卫生。
- PDB 保留在 ZIP 中，便于 Beta 诊断。

未修改产品代码、用户配置路径、音频、网络、协议或 UI；未创建安装器、
签名、发布工作流、GitHub Release 或 tag。

## 构建命令

完整验收从仓库根目录执行：

```powershell
./scripts/Publish-ListenSphereWindows.ps1
```

脚本内部执行与以下命令等价的流程：

```powershell
dotnet restore ListenSphere.sln
dotnet build ListenSphere.sln -c Release --no-restore -m:1 -warnaserror
dotnet test ListenSphere.sln -c Release --no-build --no-restore
```

Release build：0 警告、0 错误。

## 测试结果

- 全量 .NET：213/213，通过；失败 0，跳过 0。
- Architecture：12/12。
- Core：105/105。
- Protocol：14/14。
- Windows Technical：82/82。
- `git diff --check`：通过。
- Governance test：脚本存在、产品版本未硬编码、publish 参数与 ZIP 命名
  正确、`artifacts/` 已被忽略且不可进入 Git 索引。

## Publish 参数

Controller 与 Sender 均使用：

```text
Configuration=Release
RuntimeIdentifier=win-x64
SelfContained=true
PublishSingleFile=false
PublishTrimmed=false
```

未启用 ReadyToRun、NativeAOT 或第三方压缩依赖。发布目录分别包含
`hostfxr.dll`、`hostpolicy.dll`、`coreclr.dll`、
`PresentationFramework.dll` 和应用 runtimeconfig，满足 self-contained 文件
结构检查。

发布 EXE Metadata 已核对：

| 应用 | FileDescription | ProductVersion | FileVersion |
|---|---|---|---|
| Controller | 聆界 / ListenSphere Controller | `0.6.0-beta.1` | `0.6.0.0` |
| Sender | 聆界发送端 / ListenSphere Sender | `0.6.0-beta.1` | `0.6.0.0` |

## Packages

### Controller

- 文件：`ListenSphere-Controller-win-x64-0.6.0-beta.1.zip`
- 大小：77,112,335 bytes
- 文件数：497
- PDB：13
- SHA256：`1c0b88f79c7ddeafec7bb532ed42f98491e737bdd5da4d0fd605a1bfbb789e39`

### Sender

- 文件：`ListenSphere-Sender-win-x64-0.6.0-beta.1.zip`
- 大小：76,778,752 bytes
- 文件数：492
- PDB：11
- SHA256：`c30fd1f879b99bea2dcbfff65a9b2f6e424f366e3aece771cb149a9b2881966e`

`SHA256SUMS.txt` 仅包含小写 SHA256、两个空格和相对文件名，不包含绝对
路径。两个 ZIP 的 EXE 均直接位于 ZIP 根目录；框架本地化子目录保留，没有
多余的 Controller/Sender 包装层。ZIP 不包含 settings、信任存储、日志、
TestResults、APK、keystore 或用户秘密。

## Portable 实际启动验证

### Publish 目录

- Controller：主窗口出现，标题为“聆界 · ListenSphere Controller”，进程
  响应正常，通过窗口关闭正常退出，退出码 0，未强制终止。
- Sender：主窗口出现，标题为“聆界 · ListenSphere Sender”，进程响应
  正常，通过窗口关闭正常退出，退出码 0，未强制终止。

### 全新 ZIP 解压目录

两个 ZIP 分别解压到新建临时目录后再次执行相同验证：Controller 与 Sender
均显示主窗口、响应正常、正常退出且退出码为 0；没有缺失 native DLL 或
runtime 文件错误。验证完成后临时目录已删除。

现有程序仍使用 `%LocalAppData%\ListenSphere\Controller` 和
`%LocalAppData%\ListenSphere\Sender`；P11-B 没有引入相对目录设置模式。

## Git hygiene

- `artifacts/publish/`、`artifacts/packages/`、两个 ZIP 和
  `SHA256SUMS.txt` 均由现有 `artifacts/` 规则忽略。
- `git ls-files artifacts` 为空。
- 提交只包含脚本、文档和治理测试，不包含生成产物。

## 已知风险

- 启动验证在装有 .NET SDK/Runtime 的开发机上执行。self-contained 运行时
  文件结构和 ZIP 解压启动均已验证，但“未安装对应 .NET Desktop Runtime
  的干净机器”验证仍属于 P11-F / Release Candidate，不能冒充已完成。
- 本次只验证启动、窗口响应和正常退出，不替代 P11-F 的两小时实机音频、
  网络重连和多设备回归。
- Beta 包保留 PDB，体积较大但有利于诊断。

## 是否通过

P11-B 验收项通过：Release build、213 项 .NET 测试、两个 self-contained
publish、Metadata、ZIP 根目录、SHA256、publish/解压目录实际启动、用户配置
路径不变和 Git 卫生均已验证。干净机器验证按阶段计划保留至 P11-F。

阶段在此停止，不进入 P11-C Windows Installer。
