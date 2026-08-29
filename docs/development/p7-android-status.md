# P7 Android 技术验证状态

## 1. 已实现内容

- 创建原生 Kotlin + Jetpack Compose 的 `ListenSphere Mobile` 工程，最低 Android 10（API 29），目标 API 34。
- 实现深色优先移动界面，展示手机 → 聆界 → Windows 主控端的声音流向、设备选择、配对码、声音来源、电平、帧数和 RTT。
- 使用 Android NSD 查找 `_listensphere._tcp.`，解析 Controller 的 UUID、名称、协议版本、能力和端口；保留手动 IP/端口备用入口。
- 使用 Android Keystore 创建五年期 RSA 客户端身份，TLS 1.3 首次连接时校验证书与 Protobuf 身份指纹，配对后固定 Controller 指纹。
- 复用仓库 `control.proto` 的 Java Lite 生成结果，支持 32 位大端长度前缀、Hello、Pair、StartStream 和 Heartbeat。
- 支持麦克风采集和用户授权后的 AudioPlaybackCapture；统一转换为 48 kHz Float32 PCM 双声道、10 ms 帧。
- 实现 60 字节小端音频包头、1200 字节上限、应用层分片、AES-256-GCM 和 UDP 发送。
- 使用 Android 前台服务维持采集与发送，系统音频模式声明 `mediaProjection`，麦克风模式声明 `microphone`。
- 日志与状态只记录计数、错误和电平，不保存或记录 PCM 内容。

## 2. 文件变更

- Android 工程：`apps/ListenSphere.Mobile`
- 共享协议：`protocol/protobuf/listensphere/v1/control.proto`（未修改，Android 直接遵循）
- 协议再生成脚本：`apps/ListenSphere.Mobile/scripts/regenerate-protocol.ps1`
- 阶段说明：`docs/development/p7-android-status.md`

## 3. 核心实现说明

数据流如下：

```text
麦克风 / AudioPlaybackCapture
  → AudioRecord
  → 48 kHz Float32 双声道 10 ms
  → 3840 字节 PCM 帧
  → 最多 1124 字节明文分片
  → AES-256-GCM（包头作为 AAD）
  → UDP
  → Windows Controller 抖动缓冲与混音
```

Android 的 UUID 在 Protobuf 身份和 StartStream 中需要兼容 `System.Guid.ToByteArray()` 的混合字节序；音频包头内的 SessionId 则使用 RFC 4122/网络顺序。`GuidWire` 对这两种布局进行了明确区分，并有固定向量测试。

## 4. 编译命令

```powershell
cd apps\ListenSphere.Mobile
.\gradlew.bat assembleDebug
.\gradlew.bat testDebugUnitTest
```

仓库父目录包含中文时，Android Gradle 构建通过 `android.overridePathCheck=true` 工作；本机 Gradle/JUnit 测试工作器存在中文 classpath 问题，可临时使用纯 ASCII 路径或 `subst` 盘符运行测试。

协议发生变化后重新生成 Java Lite 源码：

```powershell
.\scripts\regenerate-protocol.ps1 -ProtocPath C:\path\to\protoc.exe
```

## 5. 运行方法

1. 安装 `app/build/outputs/apk/debug/app-debug.apk`。
2. 手机与 Windows 主控端连接同一局域网。
3. 启动 ListenSphere Controller，并允许 Windows 防火墙中的专用网络访问。
4. 打开“聆界”，等待自动发现主控端。
5. 首次连接时在 Controller 生成六位码并填入手机。
6. 选择“手机麦克风”或“手机播放声音”，点击“开始发送”。

## 6. 手动测试步骤

### 麦克风

1. 授予麦克风和通知权限。
2. 完成配对并开始发送。
3. 对手机麦克风讲话，确认手机电平变化。
4. 在 Controller 的“其他设备”中确认 Android 声道上线并能听到声音。
5. 调整远程声道音量和静音，确认立即生效。

### 系统播放声音

1. 选择“手机播放声音”。
2. 接受 Android 的录屏/播放捕获授权提示。
3. 播放允许被捕获的音乐或视频应用。
4. 确认 Controller 能接收声音。
5. 再使用禁止捕获或受 DRM 保护的应用，确认应用无声音但 ListenSphere 不崩溃。

### 稳定性

- 重复开始/停止 20 次。
- 锁屏后观察厂商系统是否终止前台服务。
- Wi-Fi 断开 10 秒后恢复；当前版本应显示错误，自动重连尚未完成。
- 连续发送 30 分钟，记录 CPU、内存、RTT、断流与明显爆音。

## 7. 自动测试结果

- Kotlin Debug 编译：通过。
- Android JVM 协议测试：3/3 通过。
- 覆盖内容：System.Guid 字节布局、控制帧大端长度、固定 60 字节音频包头、4 片分片、AES-GCM AAD 和解密。
- Android Lint：未完成；本机 AGP 8.4.0 在 `lintAnalyzeDebug` 长时间无进展且未生成报告，已作为工具链问题保留。
- Windows 原有解决方案测试需在本阶段结束验收时再次运行，确保 Android 目录没有影响 .NET 架构。

## 8. 已知问题

- AudioPlaybackCapture 只能捕获允许被捕获的应用；DRM、FLAG_SECURE、应用自身策略及厂商限制可能产生静音。
- 部分手机不支持 48 kHz Float32 AudioRecord，后续需要加入 PCM16 回退和设备能力探测。
- 当前网络异常会停止前台流并显示错误，尚未实现指数退避自动重连。
- P7 验证链路仍使用未压缩 PCM，约 3.1 Mbit/s 音频净载荷，尚未引入 Opus。
- 当前逐片 AES-GCM 和 PCM 转换仍有短生命周期对象，真机性能验证后需要引入缓冲池。
- Android/JUnit 在中文工程路径下存在 classpath 兼容问题；APK 构建不受影响。
- `0.1.0-p7` 的 Android Keystore 身份仅授权 RSA PKCS#1，导致 TLS 1.3 客户端认证失败；`0.1.1-p7` 已启用 RSA-PSS 并使用 v2 密钥别名自动迁移。
- 个别厂商的 Keystore/Conscrypt 在允许 RSA-PSS 后仍返回 RSA internal error；`0.1.2-p7` 改用 ECDSA P-256 和 v3 密钥别名，绕开厂商 RSA 私钥实现。
- Windows Schannel 的 TLS 1.3 客户端证书后握手认证与部分 Android Conscrypt 实现不兼容，双方会分别报告 EOF/I/O error。`0.1.3-p7` 保持 TLS 1.3，由主控端 TLS 证书认证并固定主控身份；客户端改在加密通道内发送证书、32 字节随机数和绑定主控指纹的身份签名，主控端验证公钥签名后再进行配对/固定，避免依赖 TLS 后握手客户端证书流程。
- `0.1.4-p8` 将 Android NSD 刷新改为等待停止回调后使用新 Listener 重启，避免重复注册导致闪退；新增无线网络/蓝牙/有线模式入口，其中仅无线网络标记为可用。Controller 远程声道新增“断开但保留信任”，并为两端加入状态、电平、选择和入场动画。

## 9. 尚未实现内容

- 从 Windows 向 Android 建立反向音频流并使用 AudioTrack 播放。现有 Protocol v1 的 StartStream 只授权 Sender 向 Controller 发送，需要先设计反向流角色、端点和密钥协商。
- 自动重连、后台电池优化引导、厂商白名单说明。
- PCM16 回退、动态重采样、时钟漂移补偿和 Opus。
- Android 远程控制 Windows 声道和主音量。
- 发布签名、正式 Release APK、应用商店配置。

## 10. 阶段验收结论

P7 的代码与自动测试部分已完成；由于尚未在实体 Android 设备上完成发现、配对、麦克风、AudioPlaybackCapture 和 30 分钟稳定性验证，本阶段状态为“代码完成，待真机验收”，不能标记为完整通过。
