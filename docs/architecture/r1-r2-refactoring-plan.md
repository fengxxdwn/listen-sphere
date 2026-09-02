# ListenSphere R1/R2 精确重构计划

状态（2026-09-01）：R1 已完成；R2 第 7/7 批自动化门禁与最终人工验收均已
通过，可以开始 R3 表现层治理。详细结果见
`r1-baseline-report.md` 和
`r2-progress-report.md`。

## R1：协议与文档校准

### 目标与边界

- 让 `docs/protocol/listensphere-protocol-v1.md` 与 Windows、Android 的真实握手实现一致。
- 冻结现有 protobuf 字段号、控制帧前缀、UDP 包头和身份签名字节，不改变 wire protocol。
- 不要求 TLS client certificate；客户端身份由 Hello 中的公钥材料、nonce、Controller fingerprint 绑定签名和 `DeviceProof.Validate` 证明。

### 执行顺序

1. 冻结协议基线
   - 对 `control.proto` 的 message、field number、reserved 范围生成可审查清单。
   - 记录 `ProtocolConstants`、控制帧长度前缀、TLS 版本与 UDP 固定头测试向量。
   - 对照 Windows `ControlChannel`、`DeviceProof` 与 Android control client，画出发现、TLS、Hello、配对、信任、重连状态图。
2. 只更新协议文档
   - 明确服务器证书由客户端验证/固定，Controller 不在 TLS 层强制客户端证书。
   - 明确 Hello identity proof 的输入、验证顺序、验证码仅首次信任使用及后续指纹固定。
   - 区分 TLS 会话身份、ListenSphere 设备身份和 Trust Store 状态。
3. 增加兼容性测试
   - 无 TLS client certificate 的客户端可以完成 TLS 1.3 并进入 Hello。
   - 正确签名且绑定当前 Controller fingerprint/nonce 的 Hello 通过；错误 fingerprint、nonce、签名或设备 ID 失败。
   - 已信任设备重连不需要验证码；撤销后必须重新配对。
   - protobuf 未知字段被保留兼容；主版本不兼容被拒绝；现有字段号快照不变。
   - Windows 与 Android 共用的固定身份签名/控制消息向量保持一致。
4. 门禁
   - Release build、全量 .NET 测试、Android Debug build/unit test。
   - `git diff` 不得出现 `.proto` 字段号、UDP 头布局或控制消息序列变化。

### 失败与回滚

若文档无法由代码和测试共同证明，则标记差异，不修改线上实现来迎合旧文档。R1 改动限制为文档、测试和必要的测试夹具，可独立回滚。

## R2：拆分 ControllerNetworkViewModel

### 总体策略

- 保持 `ListenSphere.Controller` 单一程序集，在 `Coordinators/` 下新增内部 Coordinator，不创建 csproj。
- 采用 Strangler Refactoring：原 ViewModel 保留公开属性、命令和 `ObservableCollection`，逐批转发到 Coordinator。
- Coordinator 不直接操作 WPF 控件或 `ObservableCollection`；通过不可变 snapshot/event 报告状态，由 ViewModel 在 Dispatcher 上投影。
- 每批只迁移一个职责域，测试通过后才开始下一批；不同时拆 `MainWindow.xaml`。

### 分批迁移顺序

1. `TransportCoordinator`
   - 接管 mDNS 发布、网络地址变化、无线/蓝牙/USB 可用性、监听生命周期和 transport selection。
   - 保留现有状态文字、自动恢复和主控主动断连抑制行为。
2. `RemoteDeviceCoordinator`
   - 接管验证码、Pairing、Trust Store、设备状态、disconnect/revoke 和可信设备刷新。
   - 对外提供设备 snapshot 与显式命令；不持有 UI 集合。
3. `AudioOutputCoordinator`
   - 接管播放设备枚举、Windows 默认输出跟随、主输出、端点音量/静音、热插拔和附加输出设备状态。
   - 保持每输出设备独立音量及 Windows 端点同步。
4. `LocalAudioRoutingCoordinator`
   - 接管系统 loopback、process loopback、本地应用捕获、附加输出路由和捕获生命周期。
   - 使用现有稳定 channel ID 与设置结构，不改变路由持久化键。
5. `MicrophoneHubCoordinator`
   - 接管录音设备、Windows 默认麦克风同步、电脑/远程麦克风、虚拟录音输出、监听和电平。
   - 保留默认关闭、静音、总音量和设备设置跳转行为。
6. `RemoteAudioCoordinator`
   - 接管 UDP/Bluetooth/USB 音频帧入口、session/channel 生命周期、jitter→DSP→mixer、远程电平和错误恢复。
   - 纳入无线音频持续丢包专项：当前人工回归已观察到缓冲升至 120 ms、累计丢失
     1205 帧、补偿 720 帧、平均抖动 8.5 ms，并出现“质量较差”。
   - 提取前先固定统计口径与可复现测试，区分真实网络丢包、接收队列溢出、线程
     调度停顿和 jitter buffer 误判；禁止仅通过继续增大缓冲掩盖问题。
   - 若根因属于生命周期、队列所有权或错误恢复，本批修复；缓冲分配和通用性能
     优化仍留给 R8。
7. `GroupMixerCoordinator`
   - 接管 group bus、EQ、dynamics、ducking、自动分组和处理链状态。
   - Scene 序列化仍留在现位置，等待 R4。

### 每批固定步骤

1. 为待迁移职责补足现状特征测试和生命周期测试。
2. 创建 Coordinator，并先由其包装现有服务；ViewModel API 不变。
3. 移动字段、事件订阅、异步循环和清理逻辑，保证每个 runtime resource 只有一个 owner。
4. 将 Coordinator snapshot 映射回原属性/集合；保留命令名和绑定路径。
5. 删除 ViewModel 中已迁移的重复实现，记录文件尺寸变化。
6. 执行 Release build、全量测试和该职责域人工回归，再进入下一批。

### 生命周期与并发约束

- 每个 Coordinator 实现幂等初始化和 `IAsyncDisposable`，取消 token、后台循环、事件解绑和设备资源由同一 owner 处理。
- 实时音频线程不得触碰 Dispatcher、日志 I/O、设置序列化或长锁。
- ViewModel 只聚合状态；Coordinator 之间通过显式方法、事件或只读 snapshot 协作，不互相读取 UI 集合。
- 保留现有 settings schema、channel/device ID、协议对象和诊断事件名称。

### 测试与人工回归矩阵

- 所有批次：Release 0 警告/错误；80 项以上 .NET 测试通过；Android 23 项以上测试通过。
- Transport/Device：无线、蓝牙、USB 模式切换；刷新 10 次；主动断连不自动重连；撤销信任。
- Output/Local Routing：默认输出跟随；端点音量；应用附加输出添加/删除；热插拔；停止捕获延迟。
- Microphone：默认麦克风同步；虚拟录音端点；监听设备；静音/电平；停止发送不重连。
- Remote Audio/Mixer：网络、蓝牙、USB 音频；丢包/重组；EQ、Dynamics、Ducking、Group Bus；10 分钟稳定性。
- 无线音频专项：稳定局域网连续传输 10 分钟，不持续显示“质量较差”，补偿帧和
  丢失帧不应无界增长，且不得出现可感知卡顿；报告需分别记录网络包丢失、接收
  队列丢弃、迟到帧、补偿帧、缓冲深度及平均/峰值抖动。

### 完成标准

- `ControllerNetworkViewModel.cs` 小于 30 KB，只保留 UI projection、命令绑定和 Coordinator 状态聚合。
- 每个 Coordinator 约 5–25 KB；超过 30 KB 必须在阶段报告中解释。
- 所有既有绑定、设置、协议和人工验收行为保持不变。
- R2 完成后才允许开始 R3 的 XAML 结构拆分。
