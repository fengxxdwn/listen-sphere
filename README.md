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

当前已支持多个 Sender 的有界队列混音，但采用 Controller 本地 10 ms 播放时钟；跨机器时钟同步、严格采样时间戳对齐及高质量异步重采样仍未完成。

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

## 下载

P11-A 仅完成产品 Metadata 与打包架构准备，尚未发布面向用户的安装包。后续产物将统一从 GitHub Actions 下载，不应将仓库根目录中的历史测试包视为正式发布。

## Windows 安装

正式安装器将在 P11-C 提供，目标安装位置为 `%ProgramFiles%\ListenSphere`。普通安装、升级与卸载都将保留 `%LocalAppData%\ListenSphere` 下的设置、场景、配对信任和日志。当前 Beta 安装器计划为未签名版本，可能触发 Windows SmartScreen。

## Windows 便携版

P11-B 将提供自包含的 Controller 与 Sender `win-x64` ZIP，不要求用户单独安装 .NET Runtime。当前尚无 P11 便携包。

## Android 安装

当前阶段可从源码构建 Debug APK。Android 可能要求用户允许安装来自所用文件管理器或浏览器的未知应用。正式签名 APK 尚未提供，任何永久 keystore 都不会提交到本仓库。

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
