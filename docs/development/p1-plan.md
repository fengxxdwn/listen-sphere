# ListenSphere P1：Windows 音频技术验证计划

## 1. 本阶段目标

P1 只验证 Windows 音频技术链路，不引入正式网络、配对、场景或完整界面：

1. 枚举活动输出设备和默认设备。
2. 选择设备并完成 WASAPI shared-mode 播放。
3. 使用 WASAPI Loopback Capture 捕获指定输出端点。
4. 转换到 48 kHz、Float32、双声道。
5. 按 10 ms 生成 `AudioFrame` 并通过有界队列传递。
6. 计算 Peak/RMS 电平，通过低频快照显示。
7. 连续运行 10 分钟并记录稳定性数据。

## 2. 当前项目状态

P0 已提供：

- 平台无关音频接口和默认格式；
- `ListenSphere.Windows.Audio` NAudio 适配层；
- Controller/Sender WPF 空壳和依赖注入入口；
- 实时线程规则、诊断边界和 Windows 技术测试项目。

P1 不使用 ListenSphere Protocol，不向局域网发送音频。

## 3. 技术方案与涉及模块

- `ListenSphere.Windows.Audio`
  - NAudio `MMDeviceEnumerator` 枚举端点。
  - `WasapiOut` shared mode 验证播放。
  - `WasapiLoopbackCapture` 捕获指定输出端点。
  - 适配 `IAudioDeviceManager`、`IAudioCaptureSource` 和 `IAudioPlaybackSink`。
- `ListenSphere.Audio.Engine`
  - 增加 10 ms frame slicer、格式归一化入口和电平计算器。
  - 使用预分配/池化内存和容量可配置的有界队列。
- `ListenSphere.Sender`
  - 最小验证页：设备选择、开始/停止、捕获状态、Peak/RMS 和错误文本。
- `ListenSphere.Windows.TechnicalTests`
  - 无硬件单元测试与需要人工设备的标记式集成测试分开。

## 4. 实施顺序

1. 枚举设备，稳定标识使用 MMDevice ID，不使用列表下标。
2. 实现播放 sink，并用内置生成的低音量正弦测试信号验证指定设备。
3. 实现 loopback capture；回调只复制到池化缓冲并投递有界队列。
4. 在后台处理循环完成声道映射、采样格式转换、重采样和 10 ms 切帧。
5. 实现 Peak/RMS，按不高于 20 Hz 的频率发布不可变 UI 快照。
6. 增加取消、重复启停、设备消失和应用退出清理。
7. 执行 10 分钟稳定性测试并记录 CPU、内存、GC、欠载、溢出和未处理异常。

## 5. 风险与失败行为

- 捕获设备移除：停止当前源、报告可恢复错误、刷新设备列表；不得崩溃。
- 格式变化：在音频线程外重建转换链；旧帧按 generation 丢弃。
- 队列满：丢弃最旧帧并增加 overflow 计数，禁止阻塞捕获回调。
- 播放欠载：写入静音并增加 underflow 计数。
- 受保护或独占内容无数据：显示系统限制，不尝试绕过。
- 测试回放可能形成反馈：默认只允许写调试 WAV 或播放测试信号；loopback 直通播放必须有显式防反馈开关。

## 6. 自动测试与手动验收

自动测试：

- 设备模型映射和默认设备选择。
- PCM 格式转换、声道映射和 10 ms 帧边界。
- Peak/RMS 已知向量。
- 有界队列溢出策略和取消。
- 重复 Start/Stop、异常回调和资源释放。

手动验收：

1. 列出系统全部活动输出设备并标出默认设备。
2. 选择耳机，播放测试音后停止，系统音量状态不被破坏。
3. 播放浏览器音频，Sender 显示捕获和实时电平。
4. 停止/启动捕获 20 次，无残留占用或未处理异常。
5. 捕获时拔出设备，程序不崩溃并给出可恢复中文提示。
6. 连续捕获 10 分钟，内存不持续增长，音频回调无阻塞日志或 UI 调用。

只有上述项目全部通过，才进入 P2 Windows Audio Session 管理。
