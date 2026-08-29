# P11 有线连接状态

> P12 已将 AOA/WinUSB 设为默认有线承载。本文件记录的 RNDIS/NCM 方案仅作为
> “USB 网络兼容模式”保留。

## 当前实现

- Android 版本为 `0.3.0-p11`（versionCode 20）。
- Android 有线模式使用 USB 网络共享（RNDIS/NCM）承载 ListenSphere Protocol。
- 控制通道继续使用 TLS 1.3 + Protobuf，音频继续使用 AEAD UDP 数据报。
- 有线模式不依赖 ADB；ADB 仅用于开发安装和日志采集。
- 客户端在 TLS ClientHello 前发送 5 字节 `LSTH + transport code` 提示。该提示只用于 UI 分类，设备身份和信任仍由 TLS 内签名握手验证。
- 未发送提示的旧客户端保持兼容并按 Wireless 分类。
- 同一设备的 Wireless、Bluetooth、Wired 记录通过 ObservedTransports 合并，不互相覆盖。

## 测试前提

1. 使用支持数据传输的 USB 线连接 Android 与 Windows。
2. Android 开启“USB 网络共享”。
3. 在 Windows `ipconfig` 中找到新增 USB/RNDIS/NCM 网卡的 IPv4 地址。
4. Android 有线页面填写该 IPv4 和 Controller 当前 TCP 端口。
5. 首次连接输入一次性验证码；后续使用证书固定自动信任。

## 暂不包含

- Android Open Accessory/WinUSB 原生 bulk 通道。
- 自动识别 Windows USB 网卡地址。
- USB 断开后的跨网卡无缝迁移。
