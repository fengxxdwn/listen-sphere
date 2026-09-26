# 聆界 / ListenSphere

聆界（ListenSphere）是一款面向多电脑、多设备用户的局域网音频中枢。

当前产品版本：`0.6.0-beta.1`（P11 Beta）。

当前仓库已完成：

- P0：需求、架构、协议草案和可编译骨架。
- P1：Windows 输出设备枚举、Loopback Capture、统一格式、Peak/RMS、测试音和调试 WAV。
- P2：Windows Audio Session 枚举、实时状态、独立音量与静音控制。
- P3：mDNS/DNS-SD 自动发现、TLS 1.3 控制通道、六位验证码配对、证书固定信任、心跳与断线检测。
- P4：TLS 下发临时音频会话、AES-256-GCM 加密 UDP、应用层分片/重组、30 ms 抖动缓冲、丢帧静音和 WASAPI 播放。
- P5（代码完成，待人工验收）：中文主界面、输出选择、端点主音量、本机/远程声道、场景保存恢复和首次使用引导。
- P6（代码完成，待人工验收）：Controller/Sender 输出设备热插拔恢复、多发送端定时混音、发现服务重试、配置损坏备份恢复、丢包/RTT/缓冲/漂移统计，以及持久化隐私诊断日志与诊断包导出。
- P9（无线可靠性代码完成，待真机验收）：ListenSphere Mobile 已支持 mDNS、TLS 1.3 配对、加密 UDP、NSD 安全刷新、有界自动重连、Controller 主动断开反馈，以及 Float32/PCM16 采集回退。
- R8（已验收）：完成实时音频热路径的缓冲所有权与分配治理、自动分配门禁，以及 30 分钟合成加密回环稳定性测试。
- R9（已验收）：完成多设备时钟治理；每个远程源具有独立的基于时间戳的时钟漂移估计、有界自适应重采样和每流自适应 PCM 缓冲，并修复 Controller 10 ms 调度器长期运行时的时间漂移。
- R10（已验收）：Windows 与 Android CI 已覆盖 Release 构建、.NET 全量/架构/协议测试、Android 构建与单元测试，以及测试结果和 Debug APK 上传。

R9 的时钟治理用于限制长期运行中的缓冲与时钟漂移，不代表已经实现设备间采样级精确同步（sample-accurate synchronization）、共同绝对播放时刻（common absolute playout time），也不代表能够自动校准不同声卡的固有延迟。

## 项目名称

| 项目 | 名称 |
|---|---|
| 产品 | 聆界 |
| 英文产品名 | ListenSphere |
| GitHub 仓库 | listen-sphere |
| Windows 主控端 | ListenSphere Controller |
| Windows 发送端 | ListenSphere Sender |
| Android 客户端 | ListenSphere Mobile |
| 通信协议 | ListenSphere Protocol |

## 构建

需要 Windows x64 和 .NET 10 SDK 10.0.302 或更新的 .NET 10 feature band。

```powershell
dotnet restore ListenSphere.sln
dotnet build ListenSphere.sln -c Release --no-restore
dotnet test ListenSphere.sln -c Release --no-build
```

Android 客户端需要 JDK 17+、Android SDK 34 和 Gradle 8.10.2：

```powershell
cd apps\ListenSphere.Mobile
.\gradlew.bat assembleDebug
```

Android 10（API 29）或更高版本才支持本项目使用的 AudioPlaybackCapture。系统播放声音仍受 Android 授权、应用捕获策略和受保护内容限制。

当前自动门禁统计：

- .NET tests：215/215。
- Android test files：17。
- Android unit tests：36/36。

## 下载

P11-C 已具备本地生成 Windows Installer 的能力；正式用户下载仍待 Packaging CI / Release。仓库根目录中的历史测试包不应视为正式发布。

## Windows 安装

可使用 Inno Setup 6 和 `scripts/Build-ListenSphereInstaller.ps1` 在本地生成安装器，目标安装位置为 `%ProgramFiles%\ListenSphere`。Controller 默认安装，Sender 可选；普通安装、升级与卸载都将保留 `%LocalAppData%\ListenSphere` 下的设置、场景、配对信任和日志。当前 Beta 安装器未签名，可能触发 Windows SmartScreen。

## Windows 便携版

P11-B 已具备本地生成自包含 Controller 与 Sender `win-x64` 便携包的能力，不要求用户单独安装 .NET Runtime；正式用户下载仍待 Packaging CI / Release。

## Android 安装

P11-D 已具备本地生成 Debug 和 Release APK 的能力。未配置 release keystore 时，Release APK 会明确标记为 `unsigned`；只有通过正式 keystore 签名验证后才使用正式文件名。正式用户下载仍待 Packaging CI / Release，任何永久 keystore 都不会提交到本仓库。

## 系统要求

- Windows x64；源码构建需要 `.NET SDK 10.0.302`（以 `global.json` 为准）。
- Android 10（API 29）或更高版本；源码构建需要 JDK 17 与 Android SDK 34。
- 设备位于允许局域网设备互访与 mDNS 的网络中。

## 第一次连接

先启动 Controller，再启动 Sender 或 Android 客户端；选择发现到的 Controller，并按界面提示完成六位验证码配对。Windows Defender Firewall 首次提示时，仅在可信网络上允许 Controller 通信。

## Beta 注意事项

- P11 尚未完成安装器签名、自动更新或应用商店发布。
- 安装前保留重要设置备份；卸载默认不会删除用户数据。
- 网络隔离、客户端隔离或受保护的 Android 音频内容可能阻止连接或捕获。

设计与验收资料：

- `docs/architecture/p0-architecture.md`
- `docs/protocol/listensphere-protocol-v1.md`
- `docs/development/p1-plan.md`
- `docs/development/p2-status.md`
- `docs/development/p3-status.md`
- `docs/development/p4-status.md`
- `docs/development/p5-status.md`
- `docs/development/p6-status.md`
- `docs/development/p7-android-status.md`
- `docs/release/versioning.md`
- `docs/release/packaging.md`
- `docs/release/android-signing.md`
- `docs/release/release-checklist.md`
