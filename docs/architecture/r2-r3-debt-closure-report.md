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
- 全量 .NET：147/147 通过。
  - Architecture：7
  - Core：52
  - Protocol：14
  - Windows Technical：74
- 新增覆盖：
  - 七个 Network 子模型绑定路径门禁。
  - MainWindow ContentHost/Dialog Host 页面边界。
  - 三个 Dialog XAML 组件与 code-behind 完整性。
  - Shell 小于 30 KB、卡片模型独立文件边界。
  - 命令对象转发。
  - 展示订阅解除、运行时只释放一次的关闭生命周期。
  - 附加输出删除按钮必须直接绑定卡片 `RemoveCommand`。
  - 删除命令必须原样转发稳定的 ChannelId 和 DeviceId。
- Android（JDK 17、SDK 34、Gradle 8.10.2）：
  `assembleDebug testDebugUnitTest --no-daemon` 成功，25/25 通过，Debug APK 存在。
- `git diff --check` 与 Git 索引卫生：提交前最终执行。
- TRX：被忽略的 `TestResults/R2-R3-debt-closure/dotnet/`。

## UI Automation 与人工测试

- Release Controller 窗口正常显示并响应，最终 UI Automation 共发现 212 个元素。
- “刷新应用”“刷新播放设备”“导出诊断包”“启用麦克风中枢”均存在。
- 初始化 8 秒后 Windows 默认麦克风保持单选，实际设备为 `Mic (MCHOSE V9 PRO)`。
- 人工视觉与音频交互回归待用户验收；本分支在验收前不合并到 `main`。

## 人工验收缺陷修复

人工验收发现“本地声音”附加输出再次无法删除。当前设置仍保存两条路由，而最新日志
没有对应的 `Removing secondary output route` 事件，证明点击没有进入协调器。

根因是卡片已经持有稳定的 `RemoveCommand`，但 XAML 仍使用父视图 `Click` 事件，
并依赖父级 DataContext 才能调用删除。展示层拆分后该路径可能提前返回。

修复后删除按钮直接绑定当前卡片的 `RemoveCommand`，移除父视图 code-behind 事件。
协调器继续负责立即移除配置和快照，再异步释放播放路由；没有修改设置格式或音频行为。
修复后的 Release 构建为 0 警告、0 错误，全量 .NET 145/145 通过。等待用户重新执行
两条“本地声音”路由的删除验收。

再次复验时发现同一 Release 路径存在两个 Controller 进程，两个实例同时读取并写入
同一份 `settings.json`。其中一个窗口删除路由后，另一个旧实例可用过期快照再次保存，
导致路由恢复并表现为“无法删除”。因此新增命名互斥量单实例保护：首个 Controller
持有互斥量，后续重复启动立即退出，退出时可靠释放。实测连续启动两次仅保留首个
进程；第二个进程立即退出。新增互斥、并发拒绝和释放后重启测试，修复后的 Release
构建为 0 警告、0 错误，全量 .NET 146/146 通过。

用户继续复验发现第一次点击后存在可感知延迟，多次点击才看到卡片消失。删除配置本身
已经同步完成，但展示层仍等待协调器快照重建，命令同时等待 WASAPI 路由与捕获资源
清理。现在第一次点击先从当前输出设备的展示集合移除路由并刷新音源数量，再让出一次
Dispatcher 渲染周期，之后更新配置并在后台完成资源清理；删除按钮命中区域同步扩大。
新增即时投影删除测试，最终 Release 构建 0 警告、0 错误，全量 .NET 147/147 通过。

继续按完整链路复验后，UI Automation 直接调用一次在约 119 ms 内移除卡片，配置约
346 ms 落盘；前台可见窗口的真实鼠标单击也在约 453 ms 内完成。由此排除设置、
协调器、快照和持久化要求重复调用。剩余不稳定点是 WPF Button 默认在鼠标抬起时
执行命令，窗口激活、短暂卡顿或按下/抬起间指针偏移会取消一次 Click。两处附加
输出删除入口改为 `ClickMode="Press"`，首次按下即执行。最终实测在保持鼠标按下、
尚未抬起时配置已于约 419 ms 清空；架构测试固定两处入口的 Press 语义。

## 已知风险

- `ControllerNetworkRuntime.cs` 仍为 91 KB 兼容适配层；本阶段优先完成公开 Shell、
  展示模型和 XAML 边界，未在同一批次重写已稳定的网络/音频编排。
- `RemoteChannelItemViewModel.cs` 为 30.8 KB，包含 EQ、Dynamics、网络质量和传输状态；
  职责仍集中，但已与 Shell/运行时分离。若后续继续拆分，必须保持设置和绑定语义。
- Android 构建仍输出既有 SDK XML 版本和 `android.overridePathCheck` 环境警告，
  没有 Kotlin/Java 编译警告或测试失败。

## 阶段是否通过

自动化门禁、UI 冒烟与人工视觉、交互和音频回归均已通过。用户于 2026-09-02
确认附加输出单次删除验收通过。本阶段状态为 **通过**；本报告提交后停止，不自动进入 R4。
