# 聆界 / ListenSphere

聆界（ListenSphere）是一款面向多电脑、多设备用户的局域网音频中枢。

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
