# P6：稳定性与诊断

## 1. 已实现内容

- 使用 Windows MMDevice 通知监听输出设备接入、移除、默认项和状态变化。
- 默认使用 Windows `Console` 输出角色，并可选择“跟随 Windows 系统默认输出”。
- 设备变化经 600 ms 合并后自动重新枚举端点并重建 WASAPI 播放。
- Sender 同样监听捕获设备变化；曾经请求捕获时会刷新设备并自动重建捕获链路。
- 单次播放写入异常会隔离故障播放端，不再终止 UDP 接收循环。
- Controller 使用固定 10 ms 播放节拍混合多个 Sender；每个会话使用有界队列，缺帧补静音，Float32 求和后限幅。
- Sender 的 mDNS 发现循环异常后等待 2 秒并自动重启；心跳断开后的既有自动重连继续生效。
- 配置 JSON 损坏或版本无效时自动改名备份，并恢复版本化默认设置。
- UDP 输出队列新增深度、溢出、活动会话、抖动缓冲、估算丢包、迟到包与重复包统计。
- Sender 显示控制通道心跳 RTT；Controller 显示丢包、混音活动流、缺帧和削波统计。
- WASAPI 播放新增欠载、溢出、缓冲毫秒数、漂移估计和延迟上限纠偏统计。
- Controller 提供一键诊断 ZIP，包含运行时快照、最多 512 条结构化事件、最近三份滚动日志和隐私说明。
- Controller 与 Sender 持久化隐私过滤后的 JSONL 日志；单文件上限 5 MiB。
- 诊断属性会过滤 PCM、Payload、密钥、Secret、证书及证书指纹字段。
- 两个窗口都在 UI 可继续调度时异步释放业务链路；容器和网络服务支持幂等释放，正常退出不再残留或崩溃。

## 2. 文件变更列表

- `src/ListenSphere.Configuration/JsonSettingsStore.cs`
- `src/ListenSphere.Diagnostics/DiagnosticsContracts.cs`
- `src/ListenSphere.Diagnostics/DiagnosticArchiveService.cs`
- `src/ListenSphere.Audio.Abstractions/AudioContracts.cs`
- `src/ListenSphere.Audio.Engine/AdaptivePlaybackController.cs`
- `src/ListenSphere.Network/UdpAudioTransport.cs`
- `src/ListenSphere.Windows.Audio/WasapiPlaybackSink.cs`
- `src/ListenSphere.Windows.Devices/WasapiDeviceNotificationSource.cs`
- `apps/ListenSphere.Controller/ControllerNetworkViewModel.cs`
- `apps/ListenSphere.Controller/ControllerViewModel.cs`
- `apps/ListenSphere.Controller/MainWindow.xaml`
- `apps/ListenSphere.Sender/SenderNetworkViewModel.cs`
- Core 与 Windows Technical 测试。

## 3. 核心实现说明

设备通知回调只记录轻量事件并触发延迟恢复，不直接执行设备枚举或更新 UI。
恢复任务在 Dispatcher 上更新设备集合，并通过播放锁串行切换端点。故障播放对象会先从
实时路径移除，再安全停止和释放。

播放缓冲目标为 60 ms，容差为 40 ms。超过 100 ms 时丢弃一个新到帧以阻止延迟持续增长；
低于 20 ms 的状态边沿计为欠载。漂移由远端采样帧时间戳与本地单调时钟估算，并使用指数
平滑；流时间戳回退时自动重置估算器。

损坏设置备份文件与原设置位于同一目录，名称形如
`settings.json.corrupt-20260824-120000000.json`。诊断包默认导出到
`文档\ListenSphere Diagnostics`，不包含设置文件、可信设备文件、证书或真实音频。

## 4. 编译命令

```powershell
dotnet restore ListenSphere.sln
dotnet build ListenSphere.sln -c Release --no-restore
dotnet test ListenSphere.sln -c Release --no-build
```

## 5. 运行方法

```powershell
dotnet run --project apps/ListenSphere.Controller/ListenSphere.Controller.csproj -c Release
dotnet run --project apps/ListenSphere.Sender/ListenSphere.Sender.csproj -c Release
```

## 6. 手动测试步骤

1. Controller 播放远程声音时拔下当前耳机，确认程序不退出并显示恢复状态。
2. 插回耳机，等待约 1 秒，确认输出列表自动刷新并恢复；必要时重新选择该端点。
3. 禁用再启用网络适配器，确认 Sender 显示中断，并在 mDNS 恢复后自动重新连接。
4. 运行 30 分钟，记录界面中的队列深度、缓冲毫秒、补静音和漂移，确认延迟不持续增长。
5. 点击“导出诊断包”，确认文档目录出现 ZIP，内含 `runtime.json`、`events.json` 和
   `privacy.txt`。
6. 退出程序，备份后把 `%LOCALAPPDATA%\ListenSphere\Controller\settings.json`
   改成无效 JSON，再启动；确认出现中文恢复提示并产生 `.corrupt-*.json` 备份。
7. 完成 5 次耳机拔插、5 次断网恢复和 5 次 Sender 重启；程序不应崩溃。

## 7. 自动测试结果

2026-08-24（P6.1 稳定性修复后）：

- Release 全量编译：0 个警告，0 个错误。
- 自动测试：39/39 通过。
  - Architecture：1
  - Protocol：9
  - Windows Technical：11
  - Core：18
- 新增覆盖：多流混音/缺帧/削波、包序号缺口/迟到/重复/回绕、损坏配置恢复、漂移估计/缓冲决策、运行中日志归档及隐私过滤、设备通知注册与注销。
- Controller 启动、显示窗口、正常关闭：退出码 0。
- Sender 启动、显示窗口、正常关闭：退出码 0。

## 8. 已知问题

- 实际设备驱动对“重新插入同一端点”的 ID 和可用时序处理不同，必须人工验证。
- `[部分解决]` 当前纠偏采用整帧丢弃，不是高质量异步重采样；优先目标是防止延迟无限累积。
- `[部分解决]` 多 Sender 已能混音，但按 Controller 本地播放节拍消费；严格跨机器时间戳对齐和时钟同步仍未实现。
- `[部分解决]` Sender 可显示控制 RTT；Controller 尚未从 Sender 收集每设备 RTT 遥测。
- `[环境阻塞]` 双电脑真实网络、两路真实音频和 2 小时稳定性测试仍因当前环境限制延期。
- `[系统限制]` Windows Audio Session 能控制现有会话音量/静音，但不能保证迁移所有应用的输出端点，也不能捕获受保护内容、独占模式或所有特殊驱动路径。

## 9. 尚未实现内容

- 2 小时双机稳定性测试和完整性能测量。
- 跨设备时钟同步、严格时间戳对齐和高质量漂移重采样。
- Android P7、Opus、虚拟声卡及独立应用 PCM 均未开始。

## 10. 是否满足本阶段验收标准

代码、自动测试、真实启动/关闭和可自动验证的恢复/诊断项已满足。耳机热插拔、断网恢复、30 分钟与
2 小时稳定性仍依赖人工环境，因此 P6 状态为“实现完成，等待人工验收”，不能提前宣布
Windows MVP 已完整验收，也不开始 P7。
