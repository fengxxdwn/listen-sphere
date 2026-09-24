# R9：多设备时钟治理

日期：2026-09-23。分支：`codex/r9-multidevice-clocks`。
基线：R8 已获用户验收，本地 main 固化至 `9fda4be`；本阶段不合并 main、不进入架构门禁或 R10。

## 实际修改

- 在现有 AdaptivePlaybackController、IAudioResampler 体系上分离时钟来源、远端漂移估计、连续采样修正。
- 远端每路 PCM 进入混音器前独立修正，混音后各 WasapiPlaybackSink 再根据各自播放缓冲修正。
- 修正混音循环对 PeriodicTimer 每次唤醒恰好代表 10 ms 的假设：使用单调时间累计应消费帧数，单次最多补齐 8 帧；长时间挂起不无限追赶。
- 新增确定性时钟、两小时模拟、连续波形、重连和分配回归测试。

## 文件变更

- `src/ListenSphere.Audio.Abstractions/AudioClockContracts.cs`：IPlayoutClock、IRemoteClockEstimator、IAdaptiveAudioResampler（继承 IAudioResampler）。
- `src/ListenSphere.Audio.Engine/StopwatchPlayoutClock.cs`：单调时钟及有界播放节拍调度器。
- `src/ListenSphere.Audio.Engine/RemoteClockEstimator.cs`：固定容量回归窗口、漂移估计、时间戳回绕和 epoch 重置。
- `src/ListenSphere.Audio.Engine/AdaptiveLinearResampler.cs`：Float32 双声道小比例连续插值，跨块保留相位及最后一个采样。
- `src/ListenSphere.Audio.Engine/AdaptivePcmStreamBuffer.cs`：每路自有 PCM 环形缓冲与修正器。
- 修改 AdaptivePlaybackController、RemotePcmMixer、WasapiPlaybackSink、RemoteAudioCoordinator，接入上述能力。
- 新增 AudioClockTests、AdaptiveMixerClockTests、AudioClockBoundaryTests；更新旧播放控制器测试的紧急保护阈值及估计预热条件。
- 本报告。未修改 Android、protobuf、UDP 包头、设置键、场景格式或 XAML。

## 架构变化

时钟观察由 IRemoteClockEstimator 负责，IPlayoutClock 提供可替换时间源；AdaptivePlaybackController 组合估计与低通缓冲反馈，IAdaptiveAudioResampler 执行采样修正。

每路 RemotePcmMixer 流持有独立估计器、重采样相位与 PCM 环；每个播放 sink 持有独立输出修正器。输入数据在 Enqueue 返回前复制到自有存储，不缓存借用数组。移除流即解除其所有状态引用，重新接入从新时钟状态开始。

保留不带时间戳的 Enqueue 入口及其原有队列语义；生产远端流统一调用带时间戳入口。同一流应使用一致的入口，不混用时钟策略。

漂移估计采用约 60 秒固定容量回归窗口，至少 4 秒观察后才输出估计，隔离短时到达抖动；支持 32/64 位回绕、源重置、本地时间回退和超过 2 秒的中断重建。小幅乱序观察不污染估计。

## 行为变化

- 常规漂移改用连续小比例重采样：比率 0.998–1.002，修正变化速度限制为每秒 100 ppm。没有新增协议或网络格式。
- 整帧丢弃仅保留为紧急保护；标准播放缓冲超过 180 ms、蓝牙弹性配置超过 340 ms 时触发，环形缓冲容量耗尽也仍有保护。
- 普通输出先积累约 60 ms，蓝牙弹性输出约 100 ms 再播放。由于帧粒度及插值保留一个采样，实际启动通常约为 70/110 ms。这是明确的启动延迟变化，需人工确认。
- 输出设备时钟通过实际缓冲消耗的反馈收敛，没有假称读取硬件 IAudioClock。现有漂移数值仍是源时间戳相对单调时钟的估计，不是直接测得的 DAC 漂移。
- 现有 DriftCorrections 统计继续计数紧急丢帧，不把连续重采样帧混入该历史计数。

## 自动测试

最终门禁命令：

```powershell
dotnet build ListenSphere.sln -c Release --no-restore -m:1
dotnet test ListenSphere.sln -c Release --no-build --no-restore --logger trx --results-directory TestResults/R9/Final
# JDK17、SDK34、Gradle8.10.2，通过临时 L: 英文路径执行：
gradle.bat assembleDebug testDebugUnitTest --no-daemon --offline --rerun-tasks '-Pkotlin.incremental=false'
git diff --check
```

- Release：0 警告、0 错误，最终构建 1.75 秒。
- .NET：209/209，Architecture 8、Core 105、Protocol 14、Windows Technical 82，失败/跳过均为 0；Core（含两小时模拟）约 16 秒。
- Android：36/36，XML 失败/错误 0；构建 33 秒，42 项任务全部重跑。Debug APK 已生成于原 Gradle 输出目录。
- Android 首次调用因 PowerShell 未引用带点的参数失败，修正命令引号后完整重跑成功；未变更依赖版本。
- ±100/±300 ppm 源时钟加 ±3 ms 确定性抖动：两小时音频时间模拟中稳定缓冲在断言的 50–80 ms 范围内，漂移估计误差不超过 10 ppm。
- 四路独立源同时运行 720,000 个 10 ms 混音周期（两小时虚拟墙钟），每路真实执行 PCM 重采样与混音：混音欠载/溢出均为 0，周期采样的总积压限制在 8–40 帧。
- 四种输出设备漂移分别模拟两小时：缓冲始终在 30–110 ms 范围，第二小时首尾差不超过 0.1 ms。
- 0.998/1/1.002 比率下分块与整块波形一致到 1e-5，连续正弦采样无块边界跳变；覆盖重采样参数错误、时间戳回绕/重置、乱序、重新连接及输入所有权。
- 带时间戳的混音路径预热后 10,000 帧：当前线程新增分配 0 B。此数字不代表完整网络/声卡链路零分配。
- 15.625 ms 粗粒度定时器模拟 1,000 秒仍调度 100,000 帧，长时间暂停最多补齐 8 帧。
- `git diff --check` 通过。日志与 TRX 位于被忽略的 `TestResults/R9/`，APK 不提交 Git。

上述两小时均为加速确定性模拟，不是两小时实机运行；四源测试使用静音 PCM，波形连续性由独立正弦测试验证，不冒充听感验收。

## 人工测试

2026-09-24 已重新连接平板：`adb devices -l` 显示设备 `4e786709` 为 `device`，系统启动完成，移动端已安装 `0.5.1-stage5`，与当前项目版本一致。用户随后反馈“验收通过”。该反馈作为本阶段人工验收结论；未取得逐项试听记录或两小时实机长测日志，不能把连接检查或用户反馈记作已量化完成两小时长测。

下列详细回归与两小时实机数据仍需在 Release Candidate 前补齐：

1. 无线、蓝牙、USB 分别验证开始、停止、重连以及连续播放。
2. 至少两路独立源和两种输出设备进行两小时长测，在 0/30/60/90/120 分钟记录缓冲、欠载、溢出、漂移及主观延迟，确认没有持续增长或周期性明显爆音。
3. 验证本机音源、附加输出增删、麦克风、EQ、Dynamics、Ducking 和 Group Bus。
4. 验证设备断开/重新连接、切换默认输出和程序退出，无卡死与残留播放。

可对已启动 Controller 用现有 `scripts/Invoke-ListenSphereSoak.ps1 -ProcessId <PID> -DurationMinutes 120 -OutputDirectory TestResults/R9/device-soak` 采集进程指标。该脚本不自动读取音频缓冲或判断听感，仍需同时记录界面诊断与人工试听。

## 已知风险

- 线性插值只用于小漂移修正，不是通用高质量采样率转换；高频信号品质及真实多设备听感待实测。
- 漂移收敛是有界缓冲反馈，不提供设备间采样级同步、共同绝对播放时刻或自动对齐声卡固有延迟的保证。
- 长暂停、严重网络抖动、设备 epoch 改变仍可触发清空/预缓冲或紧急丢帧，不能承诺这些异常完全无声学瞬态。
- 此次没有修改移动端采集时钟；重采样集中在 Windows 接收和播放边界。
- R8 的长时句柄净增长风险保留至实机/RC 复核，未用虚拟时钟结果替代资源长测。
- GitHub 的历史网络超时风险仍在，远端同步以实际 push 结果为准。

## 阶段是否通过

R9 实现与自动门禁通过；用户于 2026-09-24 确认“验收通过”，阶段按用户验收结案。两小时实机长测及逐项诊断记录未取得，保留为 Release Candidate 风险与门禁，不宣称这些指标已实测通过。按既定阶段流程合并 main；不自动进入后续架构门禁、R10 或 Release Pipeline。
