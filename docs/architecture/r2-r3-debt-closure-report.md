# R2/R3 架构欠账收口报告

## 基线与范围

- 基线提交：`ffc44de2c0733608479120ae91c31aefaf10bcd0`
- 工作分支：`codex/r2-r3-debt-closure`
- 实施日期：2026-09-02
- 本阶段只收口 Controller 网络展示层和 Shell 对话框边界，不进入 R4，不修改协议、
  设置格式、音频算法或 Android 行为。

## 实际修改

- 将 `ControllerNetworkViewModel` 改为组合式小型 Shell；Shell 创建并公开 Transport、
  RemoteDevices、AudioOutput、LocalRouting、Microphone、RemoteAudio、GroupMixer 七个
  展示模型。
- 七个展示模型只投影命令、集合与状态，并把属性变化从现有运行时适配层转发给 XAML；
  Transport、RemoteDevice、AudioOutput、LocalAudioRouting、MicrophoneHub、RemoteAudio
  和 GroupMixer Coordinator 仍是运行时资源唯一所有者。
- 原实现机械迁入 `ControllerNetworkRuntime`；根 `ControllerViewModel` 仍可通过
  Shell 的受控兼容入口完成初始化、设置快照、刷新和本机会话注册。
- 全部 Controller XAML 绑定改为七个命名子模型路径，不再直接绑定巨型 Network 根属性。
- AdditionalOutputDevice、AdditionalOutputRoute、RemoteChannel、RoutingRule 和 GroupBus
  卡片模型分别迁入独立文件；公共 Equalizer/Transport 展示契约单独存放。
- 首次引导与删除设备确认分别提取为独立 Dialog View，由
  `ControllerDialogHostView` 聚合。
- `MainWindow` 只保留 Window Chrome、Sidebar、Dashboard ContentHost 和 Dialog Host。
- Dashboard 设计时数据增加七个子模型入口，XAML 设计器继续显示示例数据。

## 文件尺寸

| 文件 | 收口前 | 收口后 |
| --- | ---: | ---: |
| `ControllerNetworkViewModel.cs` | 128,255 B | 9,571 B |
| `MainWindow.xaml` | 10,049 B | 2,094 B |
| `ControllerNetworkRuntime.cs` | 不存在 | 91,037 B |
| `RemoteChannelItemViewModel.cs` | 内嵌 | 30,816 B |
| 七个展示模型 | 不存在 | 805–3,047 B/个 |

`ControllerNetworkRuntime` 是原实现的机械兼容适配层，资源所有权仍在已拆出的七个
Coordinator 中。当前阶段没有在机械迁移同时重写其网络/音频流程，避免扩大行为风险；
后续只能在对应职责阶段逐步缩减，不能重新把资源所有权移回该适配层。

## 架构变化

```text
ControllerViewModel
  └─ ControllerNetworkViewModel (Shell)
       ├─ TransportViewModel
       ├─ RemoteDevicesViewModel
       ├─ AudioOutputViewModel
       ├─ LocalRoutingViewModel
       ├─ MicrophoneViewModel
       ├─ RemoteAudioViewModel
       └─ GroupMixerViewModel
            ↓ projection
       ControllerNetworkRuntime (compatibility adapter)
            ↓ lifecycle/commands
       Seven Coordinators (runtime resource owners)
```

Shell 的关闭流程先解除七个展示模型的属性订阅，再释放运行时适配层；重复关闭只执行一次。

## 行为变化

无用户可见业务行为变化。连接方式、设备信任、输出设备、附加输出、本机音源、
麦克风、EQ、Dynamics、Ducking、Group Bus、命令名称、设置键和协议字节保持不变。

## 自动测试

- `dotnet restore ListenSphere.sln`：通过。
- `dotnet build ListenSphere.sln -c Release --no-restore -m:1`：0 警告、0 错误。
- 全量 .NET：143/143 通过。
  - Architecture：6
  - Core：52
  - Protocol：14
  - Windows Technical：71
- 新增覆盖：
  - 七个 Network 子模型绑定路径门禁。
  - MainWindow ContentHost/Dialog Host 页面边界。
  - 三个 Dialog XAML 组件与 code-behind 完整性。
  - Shell 小于 30 KB、卡片模型独立文件边界。
  - 命令对象转发。
  - 展示订阅解除、运行时只释放一次的关闭生命周期。
- Android（JDK 17、SDK 34、Gradle 8.10.2）：
  `assembleDebug testDebugUnitTest --no-daemon` 成功，25/25 通过，Debug APK 存在。
- `git diff --check` 与 Git 索引卫生：提交前最终执行。
- TRX：被忽略的 `TestResults/R2-R3-debt-closure/dotnet/`。

## UI Automation 与人工测试

- Release Controller 窗口正常显示并响应，最终 UI Automation 共发现 212 个元素。
- “刷新应用”“刷新播放设备”“导出诊断包”“启用麦克风中枢”均存在。
- 初始化 8 秒后 Windows 默认麦克风保持单选，实际设备为 `Mic (MCHOSE V9 PRO)`。
- 人工视觉与音频交互回归待用户验收；本分支在验收前不合并到 `main`。

## 已知风险

- `ControllerNetworkRuntime.cs` 仍为 91 KB 兼容适配层；本阶段优先完成公开 Shell、
  展示模型和 XAML 边界，未在同一批次重写已稳定的网络/音频编排。
- `RemoteChannelItemViewModel.cs` 为 30.8 KB，包含 EQ、Dynamics、网络质量和传输状态；
  职责仍集中，但已与 Shell/运行时分离。若后续继续拆分，必须保持设置和绑定语义。
- Android 构建仍输出既有 SDK XML 版本和 `android.overridePathCheck` 环境警告，
  没有 Kotlin/Java 编译警告或测试失败。

## 阶段是否通过

自动化门禁与 UI 冒烟通过；等待人工视觉、交互和音频回归验收。验收前阶段状态为
**代码完成，人工验收待定**，不得进入 R4。
