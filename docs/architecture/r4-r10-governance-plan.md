# ListenSphere 后续架构治理与发布计划

## 执行约束

本路线以 R3 验收基线为起点，固定顺序为：

`R3 基线固化 → R2/R3 欠账收口 → R4 → R5 → R6 → R7 → R8 → R9 → 架构门禁 → R10 CI → Release Pipeline`

每个阶段使用独立 `codex/` 分支、独立报告和独立验收。阶段未通过时停止，
不得自动进入下一阶段。每阶段均要求 Release 构建 0 错误、0 警告，全部适用的
.NET/Android 测试通过，以及 `git diff --check` 通过。

## R3 基线固化

- 重新执行 Release build、138 项以上 .NET 测试、25 项以上 Android 测试和 UI 冒烟。
- 将 R3 展示层收口、默认麦克风选择修复及本路线提交并推送到 `main`，记录提交 SHA。
- R3 以前生成的 Windows ZIP 标记为过期，不作为后续发布基线。
- 后续阶段从该提交创建独立分支。

## R2/R3 欠账收口

- 将 `ControllerNetworkViewModel` 缩减为聚合 Shell，下设 Transport、RemoteDevices、
  AudioOutput、LocalRouting、Microphone、RemoteAudio、GroupMixer 七个展示模型。
- 展示模型只负责命令、集合和 Coordinator snapshot 投影；Coordinator 继续作为运行时
  资源唯一所有者。
- 将所有卡片 Item ViewModel 移出 `ControllerNetworkViewModel.cs`，更新内部 XAML 绑定，
  不改变用户行为、设置键或协议。
- `ControllerNetworkViewModel.cs` 控制在 30 KB 内，各展示模型尽量保持 5–25 KB。
- 将首次引导和删除设备确认提取为独立 Dialog View；`MainWindow` 只保留 Window Chrome、
  Sidebar、Dashboard ContentHost 和 Dialog Host。
- 增加绑定路径、页面加载、命令转发和关闭生命周期测试。

## R4：拆分 ControllerViewModel

- 新增 `SceneViewModel`、`DiagnosticsViewModel`、`FirstRunViewModel`、
  `LocalSessionsViewModel`。
- 新增 `ISceneService`、`ISceneSerializationService` 和可替换的 `IFileDialogService`，
  分离场景业务、JSON 文件读写和文件选择。
- 新增 `ControllerSettingsCoordinator`，统一设置加载、快照、去抖保存、最新写入门禁和
  关闭落盘。
- 本机会话枚举、聚合、音量防抖、静音和图标加载迁入 `LocalSessionsViewModel`，单卡片
  模型独立存放。
- `ControllerViewModel` 仅编排初始化、子模型生命周期和 Shell 导航，目标小于 25 KB。
- 保持场景文件格式、设置结构和用户可见命令语义。

## R5：Configuration Migration

- 新增纯函数接口 `ISettingsMigration`，包含 `SourceVersion`、`TargetVersion` 和
  `Migrate(settings)`。
- `SettingsMigrationPipeline` 固定执行：
  `deserialize → version check → migrate → normalize → validate`。
- 最低支持 v10，建立 v10→11→12→13→14→15 连续迁移链；v10→11 显式迁移语音闪避
  字段，其余步骤显式填充既有字段默认值或仅推进版本。
- v1–9 和高于 v15 的文件不覆盖、不改名、不自动保存；启动使用内存默认值并提示不兼容。
- 损坏 JSON 继续备份为 corrupt 文件并恢复默认值。
- 测试覆盖逐步迁移、跨版本直升、幂等、非法值、损坏文件、未来版本拒绝和原子保存。

## R6：Network 大文件拆分

- 保持 `ListenSphere.Network` 单一程序集和现有 namespace。
- 控制通道拆为 Server、Client、Connection、TransportPreface，现有公共入口保留为兼容门面。
- UDP 音频拆为 Sender、Receiver、FrameReassembler、JitterBuffer、SequenceTracker 和
  Contracts。
- 先机械迁移再清理职责，不修改 protobuf 字段、帧前缀、TLS、UDP 包头或错误语义。
- 使用固定向量验证 Windows/Android 字节兼容，覆盖取消、超时、乱序、重复包、分片缺失和
  资源释放。

## R7：Android 结构治理

- `ListenSphereScreen.kt` 拆为 connection、capture、transport、diagnostics 和 components。
- `AudioStreamingService` 只保留 Service 生命周期，新增 StreamingSession、AudioCapture、
  TransportSession 和 Notification 协调器。
- `MobileViewModel` 负责 UI 状态，协调器负责协议和资源生命周期。
- 保持 Kotlin、Compose、三种传输、权限恢复、后台通知和配置格式不变。
- Android 测试不得少于 25 项，并补充 Service 重建、权限撤销、主动断连不抢连和配置迁移。

## R8：实时音频性能治理

- 为发送、接收、重组、静音补帧和混音路径建立分配基线，再逐点消除短生命周期分配。
- 仅在所有权明确时使用 `ArrayPool<byte>`、`MemoryPool<byte>` 和预分配缓冲；借用内存不得
  跨异步生命周期或缓存到下一帧。
- 增加每帧分配回归，并记录 GC、内存、线程、句柄、欠载和溢出。
- 阶段验收执行 30 分钟稳定测试；Release Candidate 执行 2 小时测试。
- 预热后内存不得线性增长，线程和句柄保持稳定，本地泄漏不得导致缓冲或丢包计数无界增长。

## R9：多设备时钟治理

- 复用 `AdaptivePlaybackController` 和 `IAudioResampler`。
- 新增 `IPlayoutClock`、`IRemoteClockEstimator`、`IAdaptiveAudioResampler`，分离时钟观察、
  漂移估计和播放修正。
- 先建立确定性虚拟时钟模拟，再接入自适应重采样；整帧丢弃仅作紧急缓冲保护。
- 覆盖 ±100 ppm、±300 ppm、时间戳回绕/重置、网络抖动和设备重连。
- 两小时模拟与实机测试要求延迟和缓冲不持续增长，不出现周期性明显爆音。

## 架构与仓库门禁

- 强制 Engine、Protocol、Network、Configuration、Windows 适配层和 Core 的依赖规则。
- 仓库卫生测试基于 Git 索引，禁止跟踪 wpftmp、bin、obj、APK、日志和 IDE 文件。
- 生成文件尺寸报告；超限在阶段报告中解释，不用机械行数替代职责审查。
- 所有门禁在 R10 CI 中强制执行。

## R10：CI

- `windows-ci.yml`：restore、Release build、全量测试、架构测试、协议测试、TRX 上传。
- `android-ci.yml`：JDK 17、SDK 34、Gradle 缓存、assembleDebug、testDebugUnitTest、测试 XML
  和 Debug APK 上传。
- PR 与 `main` push 均运行；依赖下载失败不得通过修改版本临时绕过。
- CI 仅上传构建产物，不提交 APK、日志或测试目录。

## Release Pipeline

- `v*` 标签触发 `release.yml`。
- Windows 发布自包含 x64 的 `ListenSphere-Controller-x64.zip` 和
  `ListenSphere-Sender-x64.zip`。
- Android 在安全 keystore 建立前仅发布 `ListenSphere-Mobile-debug.apk`，GitHub Release
  标记为预发布，不冒充正式签名版本。
- 同时生成 SHA-256、版本清单、测试摘要和发布说明。
- 发布前在干净 Windows VM 和 Android 真机验证安装、升级、配置保留、信任保留、启动和
  卸载。
- Android Release 签名待 keystore 安全进入 GitHub Secrets 后单独启用。

## 回归范围与边界

- UI 变更必须执行 UI Automation 和人工视觉/交互回归。
- 音频变更必须验证本机、无线、蓝牙、USB、附加输出、麦克风、EQ、Dynamics、Ducking 和
  Group Bus。
- 设置或协议变更必须包含旧版本或固定向量兼容测试。
- 每阶段报告固定包含：实际修改、文件变更、架构变化、行为变化、自动测试、人工测试、
  已知风险、阶段是否通过。
- 保持 .NET 10、WPF、Kotlin/Compose、JDK 17、SDK 34 和 Windows x64。
- 不修改 protobuf 字段号、UDP 包头、配置键语义或旧客户端兼容能力。
- 主控端代理麦克风继续暂缓；虚拟声卡、Opus、AI、iOS 和 UI 重设计不在本流程内。
- 设置迁移最低支持 v10；Android 暂不实施正式签名。
