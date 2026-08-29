# P12 原生 USB 连接状态

## 目标

P12 将“有线”默认承载从 USB 网络共享（RNDIS/NCM）改为 Android Open
Accessory（AOA）+ Windows WinUSB。原生模式不会创建网络适配器、默认网关或
路由，因此不会让 Windows 改用手机网络。

## 已实现

- `ListenSphere.Windows.Usb` Windows 平台适配模块。
- Windows USB 设备轮询、Android AOA 1/2 协议探测与 Accessory 模式切换请求。
- AOA VID/PID 识别和 WinUSB Bulk IN/OUT 管道。
- 固定 24 字节 `LSUB` USB 帧头，支持控制、音频、状态和保活帧。
- ListenSphere Protocol 设备证书验证、六位验证码配对和可信设备合并。
- 48 kHz、PCM16、双声道、10 ms 音频传输和 Controller 混音入口。
- Android `UsbManager` Accessory 探测、权限申请、插线唤起和前台采集服务。
- 手机端保留“USB 网络兼容模式”，仅在原生 USB 不可用时手动启用。
- Controller 中原生 USB 设备按“有线设备”显示，可单独调音、静音和断连。
- 主控主动断开无线或蓝牙会话时先发送协议级 `Disconnect`；Android 将其识别为
  用户操作并停止发送，不再进入网络故障自动重连。意外掉线仍保留退避重连。

## 安全与边界

- AOA 激活探测只对已知 Android USB 厂商 VID 执行，禁止向任意 USB 设备发送
  Vendor Control Request。
- USB 物理连接不替代设备身份验证；首次连接仍需验证码，后续仍验证证书指纹。
- PCM 数据不写入日志；错误日志只包含阶段、设备状态和异常类型。
- 原生 USB 使用可靠 Bulk 通道，不使用 TCP、UDP、IP 地址、网络共享或 ADB。

## 当前实机前提与待验证项

- Windows 必须能为 Android Accessory 接口绑定 `WinUSB.sys`。开发环境可使用
  Android/Google USB 驱动；正式发布需要签名的驱动安装包或经验证的自动绑定方案。
- 部分 OEM 不暴露可由桌面 WinUSB 打开的初始 Android 接口，导致主控端无法发送
  AOA 切换请求。这类设备暂时使用 USB 网络兼容模式，并标记为驱动兼容性问题。
- 需要在 Xiaomi 2410CRP4CC 上验证：AOA 切换、系统授权、首次配对、PCM16 连续播放、
  20 次插拔、锁屏、Wi-Fi 保持连接及 10 分钟稳定性。
- AOA/WinUSB 原生链路未完成实机验证前，不标记为生产可用。

## 验收重点

1. 开启原生 USB 时，Windows 不新增 RNDIS/NCM 网卡且默认路由不变化。
2. 插线后 Android 显示 ListenSphere Controller，并由用户完成 USB 权限授权。
3. Controller 将设备归类为“有线设备”，网络和蓝牙可信记录不消失。
4. 音频帧持续增长，双声道方向正确，无周期性卡顿。
5. 断开 USB 后双方在两秒内离线，重新插线可自动恢复可信连接。
