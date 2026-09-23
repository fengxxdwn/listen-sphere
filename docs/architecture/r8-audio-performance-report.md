# R8 — 实时音频性能治理

## 实际修改

基线：已人工验收 R7 的 `12938ca`；分支：`codex/r8-audio-performance`。本阶段不修改协议字节、配置键、音频格式、UI 或 Android 行为，不进入 R9。

先在未优化实现上运行分配测试，再逐点优化 Windows/.NET 网络音频热路径。未为追求“零分配”把共享 PCM 或静音数据放进不明确的池化生命周期。

## 文件变更

- `src/ListenSphere.Protocol/AudioPayloadProtector.cs`：新增写入调用者 Span 的加密重载；原分配式 API 保留。
- `src/ListenSphere.Network/UdpAudioSender.cs`：每个 sender 预分配 1,200 B 加密数据报缓冲，直接分片加密，删除中间分片集合、明文复制和每包输出数组。
- `src/ListenSphere.Network/UdpAudioReceiver.cs`：接收循环自有 65,536 B 缓冲，避免 UdpClient 每包返回新数组；完整处理后复用，覆盖 UDP 最大长度而不截断正常协议检查。
- `src/ListenSphere.Network/AudioFrameReassembler.cs`：复用过期 key 工作列表，去掉每片 LINQ 扫描临时数组；输出 PCM 仍独立所有。
- `src/ListenSphere.Network/AudioJitterBuffer.cs`：保留原入口，新增调用者自有 staging List 重载；接收会话独占 staging，发布的是独立 frame，不是 List。
- `tests/ListenSphere.Core.Tests/AudioAllocationTests.cs`：5 项分配基线/门禁测试。
- `tests/ListenSphere.Core.Tests/AudioBufferOwnershipTests.cs`：3 项缓冲边界、并发发送及静音隔离测试。
- `tests/ListenSphere.Core.Tests/AudioPipelineSoakTests.cs`：普通门禁运行 3 秒；指定环境变量后执行 30 分钟或 120 分钟加密回环管线测试。
- `scripts/Invoke-ListenSphereAudioPipelineSoak.ps1`：持续记录分配总量、GC 次数、托管堆、私有内存、工作集、线程、句柄、缺包/补帧、队列及混音欠载/溢出。

## 架构变化

没有新项目或运行时依赖。所有权限定为：

| 缓冲 | 所有者 | 可复用边界 |
| --- | --- | --- |
| 加密发送数据报 | 单个 UdpAudioSender | sendGate 包含整个 await SendAsync；完成后才能覆盖 |
| 原始接收数据报 | 单个接收循环 | ProcessDatagram 同步返回后；Span 不存入异步 Channel |
| ReadyFrames staging | 单个 ReceiverSession | 单接收循环清空/填充；Channel 仅持有 frame |
| 解密明文、完成帧、静音补帧 | 独立 byte[] | 可能被重组、混音、多输出保存，故不提前回收或共享 |

没有借用 ArrayPool/MemoryPool 内存跨 await 或跨帧保存。此次优先选择生命周期简单、可验证的实例自有预分配。混音器已有调用者输出缓冲，测量为零分配，不做无收益改写。

已检查 PcmFrameSlicer 的“仅回调期间有效”约定及 LocalAudioRouting/MicrophoneHub/SecondaryPlaybackRoute 复制边界：这些副本承担异步/独立增益处理所有权，本阶段保留。

## 行为变化

正常传输、包头、GCM AAD/nonce/tag、分片尺寸、时间戳、音量及混音处理保持原语义。保留原 API；新增 Span 与 staging 重载仅供低分配调用。

不会改变缓冲目标、设备时钟或采用新的重采样算法。静音数组保持彼此独立，避免下游修改一个补帧污染其他补帧。

## 自动测试

### 分配基线与结果

Release 预热 100 次、同步路径测量 2,000 次；发送测量 1,000 帧，100 帧预热。原始结果保存在被忽略的 `TestResults/R8/` TRX。

| 路径 | 优化前 | 优化后 | 说明 |
| --- | ---: | ---: | --- |
| 发送 + 原始 UDP 排空 | 17,602.34 B/帧 | 5,104.66 B/帧 | 进程总分配，包含排空端；约下降 71%，不是纯 sender 或整个音频应用指标 |
| 重组 | 5,296 B/帧 | 4,168 B/帧 | 输入数组预建，含独立 3,840 B 输出 PCM；约下降 21% |
| 解密一个 1,124 B 分片 | 1,272 B | 1,272 B | 保留明文所有权和 AesGcm 创建 |
| 普通 jitter（旧兼容入口） | 208 B/帧 | 208 B/帧 | 含输入 frame record |
| 接收端复用 staging 的 jitter | — | 120 B/帧 | 移除每次 List/背后数组；保留原排序节点 |
| 每隔一帧缺失的 jitter | 4,136 B/次 | 4,136 B/次 | 含独立静音 PCM，刻意不共享 |
| mixer 入队+混音 | 0 B/帧 | 0 B/帧 | 预热后的调用路径 |

分配门禁：重组 ≤4,500 B/帧，发送+排空 ≤9,000 B/帧，复用 staging ≤160 B/帧，mixer =0；其他路径设上界以捕获回退。异步发送统计使用进程分配计数，允许测试基础设施抖动，不能当作微秒级 benchmark。

### 构建与兼容

- Release build：0 警告、0 错误（首次 3.83 秒；2026-09-23 收尾复核 2.39 秒）。
- 全量 .NET：192/192（Architecture 8、Core 88、Protocol 14、Windows Technical 82）；2026-09-23 收尾复核再次全部通过。
- 新增长测节拍校准后 3 秒 smoke 通过；生产代码未再变更。
- Android：JDK17 / SDK34 / Gradle8.10.2，`assembleDebug testDebugUnitTest --no-daemon --offline --rerun-tasks -Pkotlin.incremental=false`，42 项任务重跑，32 秒；XML 36/36，失败/错误 0。
- 原固定向量、UDP 乱序/重复/缺片、取消/释放测试通过；新增调用者缓冲尾部不被覆盖、并发发送仍产生完整独立 PCM、静音帧相互隔离测试通过。

### 30 分钟稳定性测试

命令：`./scripts/Invoke-ListenSphereAudioPipelineSoak.ps1 -DurationMinutes 30 -OutputDirectory TestResults/R8/soak-paced`。

测试运行真实加密 UDP loopback → 解密 → 分片重组 → jitter → RemotePcmMixer；不使用真实声卡，不改用户设备、配置或信任。前 60 秒预热，不纳入内存趋势判断。采样每 5 秒一次，CSV 逐次刷新。

第一轮试跑发现 Windows 定时器分辨率导致约 64 帧/秒，已主动停止，不计为验收。工具改为按 Stopwatch 累计应发送帧数补齐负载，目标 100 帧/秒；超过 2 秒调度欠账直接失败，不悄悄降低负载。此处只校准测试工具，没有修改产品播放时钟。

最终 30 分钟测试通过：实际运行 30:00.010，发送并完成 180,000 帧；丢包、补帧、输出溢出、混音欠载和溢出均为 0。预热后私有内存 31,375,360 → 30,322,688 B，范围 30,294,016–34,308,096 B，回归斜率 −0.021 MiB/分钟。线程 18 → 15（范围 15–19）；句柄 353 → 379（范围 345–382），净增 26，在预设门限内，但不能据此排除慢速句柄增长，须在 RC 两小时测试中复核。Jitter 保持 2–3 帧，输出队列 0–2 帧。

此合成测试按收到的帧驱动混音消费，不模拟声卡的独立播放时钟，因此零混音欠载不代表真实播放设备不会欠载。

门禁要求：发送负载达到目标 98% 以上、完成帧不少于发送帧 98%、认证失败/输出溢出/混音欠载溢出为零、最终 jitter ≤12 帧、输出队列 ≤128。预热后私有内存回归斜率 <0.5 MiB/分钟、首尾增长 <32 MiB、线程增长 ≤16、句柄增长 ≤32。CSV 同时保留堆与 GC 锯齿轨迹，有限时段门槛不构成无限时运行证明。

## 人工测试

待用户验证本机、无线、蓝牙、USB、附加输出、麦克风、EQ、Dynamics、Ducking、Group Bus 的真实声音表现，以及停止/重连。合成回环不代替声卡驱动、设备时钟、实际网络或厂商后台行为验证。

## 已知风险

- 尚有必要的 PCM 输出、解密与静音分配；没有宣称整个音频管线零分配。
- Android 发送端仍有 ByteBuffer/加密数组分配；本次没有移动端分配剖析数据，不以 Windows 数字冒充 Android 优化结果。
- 真实设备时钟、PeriodicTimer 精度、自适应播放仍属 R9 和实机回归边界；此次仅保证测试按预期帧率施加载荷。
- 2 小时稳定性测试按计划留给 Release Candidate；脚本支持 `-DurationMinutes 120`，本阶段不冒称完成。
- 实施期间 GitHub 同步曾超时，远端同步以最终推送结果为准。
- Android 原有路径实验选项与 SDK XML 工具版本警告仍存在。

## 阶段是否通过

实现、短时自动门禁及 30 分钟合成管线稳定性测试通过；人工音频验收尚待完成，句柄净增长保留为 RC 复核风险。停留 R8，不合并 main，不进入 R9。
