# R7 — Android 结构治理

## 实际修改

- 基线：`main` / `f03e0ec`（已验收的 R6）；独立分支：`codex/r7-android-structure`。
- 将 Compose 页面按 connection、capture、transport、diagnostics、components 分目录提取，保留原 package、文案、布局、LazyList item 顺序和点击语义。
- 将 Service 内的会话调度、采集授权、三模传输和前台通知拆分；Service 只保留兼容常量、状态入口和生命周期转发。
- MobileViewModel 继续维护选择、权限展示、偏好与 UI StateFlow；发现、探测和 USB 检测资源交给 MobileConnectionCoordinator。
- 增加会话代际隔离和录屏授权回调租约，防止旧会话的状态、通知或完成回调干扰新会话。
- 为 MobilePreferences 增加内部 SharedPreferences 注入构造，测试真实加载/保存路径；公共 Context 构造不变。

## 文件变更

路径以 `apps/ListenSphere.Mobile/app/src/main/java/io/listensphere/mobile/` 为根。

| 文件/目录 | 职责 | 本次结束规模 |
| --- | --- | --- |
| `ui/ListenSphereScreen.kt` | 页面状态订阅、顶栏、列表和子组件编排 | 8,877 B / 195 行 |
| `ui/connection/` | 无线、蓝牙、USB 连接项、主控行、配对弹窗 | 独立页面组件 |
| `ui/capture/` | 采集来源列表和卡片 | 独立页面组件 |
| `ui/transport/` | 三模选择、蓝牙编码和声道选择 | 独立页面组件 |
| `ui/diagnostics/` | 流状态、权限与后台提示 | 独立页面组件 |
| `ui/components/` | 共用卡片、标题、刷新按钮与状态点 | 无运行时资源 |
| `ui/MobileViewModel.kt` | UI 状态与用户选择 | 14,793 B / 331 行 |
| `ui/MobileConnectionCoordinator.kt` | 发现、蓝牙探测、USB 检测 | 单一发现资源所有者 |
| `service/AudioStreamingService.kt` | Android Service 生命周期/兼容入口 | 2,169 B / 60 行 |
| `service/StreamingSession.kt` | 会话 Job、取消、异常展示和终止 | 3,912 B |
| `service/AudioCaptureCoordinator.kt` | 创建采集与 MediaProjection 授权释放 | 2,552 B |
| `service/TransportSession.kt` | 传输分发及原网络连接/重连流程 | 8,915 B |
| `service/BluetoothTransportSession.kt` | RFCOMM 建连、能力和重连 | 6,857 B |
| `service/UsbTransportSession.kt` | 原生 USB 会话 | 3,590 B |
| `service/TransportAudioPump.kt` | 发送循环、控制读取与心跳 | 4,615 B |
| `service/StreamingNotificationCoordinator.kt` | 前台通知频道、启动、更新和删除 | 2,148 B |
| `service/SessionLifetime.kt`、`CapturePermissionLease.kt`、`TransportRetryPolicy.kt` | 可测试的状态发布、权限回调和重试边界 | 小型内部策略 |
| `core/model/MobilePreferences.kt` | 只增加测试注入接缝 | 格式和迁移逻辑不变 |

测试新增 `StreamingLifecycleTest.kt`（7 项）和 `MobilePreferencesStorageTest.kt`（4 项）。Windows、protobuf、协议固定向量、Manifest、Gradle 依赖与版本号均未修改。

## 架构变化

Service → StreamingSession → AudioCaptureCoordinator / TransportSession / StreamingNotificationCoordinator。

StreamingSession 持有协程作用域及当前任务，每个任务独立持有采集协调器；TransportSession 的局部 socket/client/sender 仍沿用原 try/finally 释放顺序。蓝牙、USB 和音频泵采用同 package 的内部扩展函数拆文件，不新增重复状态或协议门面。

SessionLifetime 串行化当前代际的发布与结束操作。停止/销毁先使当前代际失效，再取消任务；旧任务不能覆盖新状态、重新发通知或结束新服务。录屏授权回调只取消创建它的 Job，正常释放先注销回调，避免正常 stop 触发取消下一会话。

Compose 分目录仍保持原 namespace，UI 子组件仅接收展示状态和 ViewModel 命令；不创建 socket、录音或通知资源。

## 行为变化

不改变三种连接方式、音源、配对、编解码选择、通知文案、配置文件名/键/版本、wire contract 或后台权限策略。继续使用 START_NOT_STICKY，不自动恢复系统终止后的录音。

有意强化的内部行为：旧会话结束不能污染新会话；正常释放后迟到的投屏权限回调无效。主动断连/取消不重试，网络错误重试间隔仍为 1、2、4、8、12、15 秒。

未增加代理麦克风、虚拟声卡、Opus、AI 或通话音频捕获能力。

## 自动测试

- Windows：`dotnet build ListenSphere.sln -c Release --no-restore -m:1`，0 警告、0 错误，7.58 秒。
- .NET：`dotnet test ListenSphere.sln -c Release --no-build --no-restore --logger trx --results-directory TestResults/R7`，183/183；Architecture 8、Core 79、Protocol 14、Windows Technical 82。构建及测试共 13.34 秒。
- Android：JDK 17 / SDK 34 / Gradle 8.10.2，经临时 L: 英文盘符执行 `assembleDebug testDebugUnitTest --no-daemon --offline --rerun-tasks -Pkotlin.incremental=false`；42 个任务全部重新执行，41 秒，36/36，失败/错误 0。
- 新增测试覆盖代际替换、停止后的迟到状态、重建后未主动开始时无活动会话、权限回调只取消所有者、正常释放抑制迟到回调、主动断连不重试、网络与权限错误区分，以及配置 v0/v1→v2、完整字段往返、未来版本加载保护、非法枚举回退。
- 生命周期单元测试针对生产策略对象，不等同于 Android 框架 Service/MediaProjection 的完整仪器测试；此边界没有用测试替身结果冒充真机验证。
- 既有协议固定向量、音频编码和三模协议测试全部通过。
- Debug APK：55,572,924 B；SHA-256：`6F5337B3F09D237304DBA0BFE76B267085B8BA58763DAE074CBC75BDF7D4244A`。保留在 Gradle 输出目录，不提交。
- `git diff --check` 通过；索引卫生扫描未发现 bin、obj、APK、日志、IDE 文件或 wpftmp。
- 原始日志、TRX、UI XML、截图与机械迁移脚本保留于被忽略的 `TestResults/R7/`；临时 L: 已清理。

### 真机 UI 自动冒烟

已授权平板 Xiaomi `2410CRP4CC` 上执行：

1. `adb install -r` 成功，未卸载、清数据、重置权限或删除信任。
2. 最终 APK 冷启动成功（1,155 ms），无线/蓝牙/有线页面切换成功；UI hierarchy 中对应主控列表、配对设备列表和原生 USB 说明可见。
3. 原最近成功连接记录保留；测试后恢复原无线模式。
4. 未发送状态下 force-stop 后重开成功（1,029 ms），UI 为“尚未发送/未连接”，dumpsys 无 AudioStreamingService，未自动开始录音。
5. 本次检查的 AndroidRuntime 错误日志无输出；已查看重开后的截图，未见卡片/文本布局异常。

上述不代表三模音频已完成实机回归：电脑主控未启动，USB 页面仍显示尚未检测到主控端。

## 人工测试

用户已反馈“测试通过”，R7 人工验收通过。以下为提交验收时的检查清单；用户未提供逐项记录，不将其表述为自动化逐项验证结果：

- 无线、蓝牙、USB 的连接、配对、实际声音发送、停止及重新开始。
- 主控主动断连后平板不抢连；快速停止/开始时无旧状态或旧通知残留。
- 录屏授权撤销、麦克风权限恢复、锁屏后台通知及系统终止后的手动恢复。
- 本机/附加输出、麦克风、EQ、Dynamics、Ducking、Group Bus 的端到端音频回归。

本次未模拟系统通话限制，未宣称能够绕过 Android 受保护音频策略。

## 已知风险

- Android 仍输出既有 overridePathCheck 实验选项及 SDK XML 版本警告；无 Kotlin 编译警告或错误。
- Windows 大小写不敏感路径上进行 Kotlin 文件名大小写调整后，增量缓存曾误报重复符号；最终禁用增量并重新执行全部任务验证通过，未改依赖版本、未提交本地缓存。
- JVM 单元测试与 UI 冒烟不能替代真实 MediaProjection 权限撤销、厂商后台回收和长时音频测试。
- 阻塞音频/传输操作沿用现有底层实现；实时性能、长期资源稳定性仍属于 R8，不在 R7 顺带改写。
- 仍为现有 Debug 签名 APK，不作为正式签名发布。

## 阶段是否通过

**实现、自动门禁及用户人工验收通过。** 按阶段流程将 R7 分支合并至 main；不进入 R8。本次验收归档仅修改报告，不修改源码或重新生成 APK。
