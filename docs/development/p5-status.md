# P5：主控端完整界面与场景

## 1. 已实现内容

- Controller 主窗口已从技术验证空壳升级为中文声音总览界面。
- 可枚举、选择并即时切换 Windows 播放输出设备。
- 主音量直接映射到选定 Windows 输出端点。
- 本机应用声道继续提供图标、活动状态、电平、音量和静音。
- 已配对/在线远程设备显示为独立声道；在线时可调节 PCM 增益和静音。
- 场景可保存、覆盖、恢复和删除，包含输出设备、主音量、本机应用声道和远程设备声道。
- 设置以版本化 JSON 原子写入，首次启动提供四步中文引导。
- 网络、输出设备、会话和设置错误均在界面显示中文提示。

## 2. 文件变更列表

- `apps/ListenSphere.Controller/MainWindow.xaml`
- `apps/ListenSphere.Controller/ControllerViewModel.cs`
- `apps/ListenSphere.Controller/ControllerNetworkViewModel.cs`
- `apps/ListenSphere.Controller/App.xaml.cs`
- `src/ListenSphere.Configuration/ConfigurationContracts.cs`
- `src/ListenSphere.Configuration/JsonSettingsStore.cs`
- `src/ListenSphere.Audio.Abstractions/AudioContracts.cs`
- `src/ListenSphere.Audio.Engine/PcmGainProcessor.cs`
- `src/ListenSphere.Windows.Audio/WasapiOutputVolumeController.cs`
- `src/ListenSphere.Network/ControlChannel.cs`
- `tests/ListenSphere.Core.Tests/CoreContractTests.cs`

## 3. 核心实现说明

设置文件位于 `%LOCALAPPDATA%\ListenSphere\Controller\settings.json`，当前版本为 3。
同一文件系统内先写临时文件再替换正式文件，避免进程中断留下半个 JSON。

本机应用仍由 Windows Audio Session 控制，未捕获独立应用 PCM。远程声道音量在
UDP 解密、重组和抖动缓冲之后、送入 WASAPI 播放之前以 Float32 线性增益处理。
主音量控制的是所选 Windows 输出端点，因此也会影响该端点上的其他 Windows 声音。

场景中的本机应用按规范化显示名称生成稳定标识；同名应用会被视为一个逻辑声道。
远程声道使用设备 UUID，重连后仍可应用原场景设置。

## 4. 编译命令

```powershell
dotnet restore ListenSphere.sln
dotnet build ListenSphere.sln -c Release --no-restore
dotnet test ListenSphere.sln -c Release --no-build
```

## 5. 运行方法

```powershell
dotnet run --project apps/ListenSphere.Controller/ListenSphere.Controller.csproj -c Release
```

首次启动会显示引导。完成后可在左侧生成配对码、保存场景，在顶部选择输出设备并调整主音量。

## 6. 手动测试步骤

1. 启动 Controller，确认首次引导文字完整，点击“进入聆界”。
2. 在顶部切换两个可用输出设备，确认状态文字显示所选设备且程序不崩溃。
3. 调整主音量，确认 Windows 对应输出端点音量同步变化。
4. 播放浏览器和音乐播放器，确认两个本机应用声道出现，并分别测试音量、静音和电平。
5. 输入场景名并保存；改变输出、主音量和声道状态后恢复场景，确认各项回到保存值。
6. 关闭并重开 Controller，确认首次引导不再出现，场景仍存在。
7. 如当前环境允许，连接 Sender，确认远程设备声道由离线变为“正在传输”，测试远程音量和静音。
8. 拔插输出设备后的自动恢复属于 P6；P5 只需点击“刷新设备”后重新选择。

## 7. 自动测试结果

2026-08-24：

- Release 全量编译：0 个警告，0 个错误。
- 自动测试：33/33 通过。
  - Architecture：1
  - Protocol：9
  - Windows Technical：10
  - Core：13
- Controller Release 启动冒烟：进程存活且 `Responding=True`，随后已关闭测试进程。

新增自动测试覆盖 Float32 增益/静音和版本化场景 JSON 往返及临时文件清理。

## 8. 已知问题

- 双电脑 P3/P4 实机验证仍因当前环境限制延期。
- 多个 Sender 的帧目前会进入同一播放队列；严格按时间戳对齐的多路混音尚未实现。
- 本机同名应用在场景中视为同一逻辑声道。
- 输出设备拔插后的自动重建属于 P6；P5 提供手动刷新和中文错误提示。
- 配置损坏自动备份并恢复默认值属于 P6；P5 会显示错误并以默认配置继续运行。

## 9. 尚未实现内容

- P6 的设备热插拔恢复、网络异常恢复强化、诊断导出、缓冲欠载/溢出统计、时钟漂移处理。
- Opus、Android、虚拟声卡和独立应用 PCM 不在 P5 范围内。

## 10. 是否满足本阶段验收标准

代码、自动测试和本机启动验收已满足。普通用户连接设备、远程声道听感和场景跨双机恢复
仍需人工测试，因此 P5 当前状态为“实现完成，等待人工验收”，不提前标记为完整验收通过。
