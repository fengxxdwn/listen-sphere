# R4 ControllerViewModel 拆分报告

## 阶段基线

- 分支：`codex/r4-controller-view-model`
- 起始提交：`a6a05d6a885dccdfba9f6696c861a4ca516fee48`
- 实施边界：仅治理 Controller 展示层、场景服务、设置保存协调与相关内部绑定；未修改协议、配置键语义、场景 JSON 格式或 Android 行为。

## 实际修改

- 将场景管理提取为 `SceneViewModel`，将场景捕获、应用、增删、复制、重命名和导入合法化提取为 `ISceneService`。
- 将场景 JSON 读写提取为 `ISceneSerializationService`，将 WPF 文件选择提取为可替换的 `IFileDialogService`。
- 将诊断导出、首次引导和本机会话分别提取为 `DiagnosticsViewModel`、`FirstRunViewModel` 和 `LocalSessionsViewModel`。
- 将本机会话枚举、按进程聚合、音量防抖、静音、图标加载、路由投影及卡片模型移出 `ControllerViewModel`。
- 新增 `ControllerSettingsCoordinator`，统一设置加载、快照生成、300 ms 去抖、最新请求门禁、保存失败报告和关闭时落盘。
- 修复协调器关闭边界：取消并等待所有排队保存，再释放持久化门禁，避免延迟任务与关闭释放竞态。
- 更新内部 XAML 绑定和设计时数据，使页面经由各子模型读取命令、集合和状态。
- 增加 R4 架构、设置、场景序列化和生命周期回归测试。

## 文件变更

新增的主要实现：

- `Presentation/SceneViewModel.cs`
- `Presentation/DiagnosticsViewModel.cs`
- `Presentation/FirstRunViewModel.cs`
- `Presentation/LocalSessionsViewModel.cs`
- `Presentation/Items/AudioSessionItemViewModel.cs`
- `Presentation/ObservableViewModel.cs`
- `Services/ControllerSettingsCoordinator.cs`
- `Services/SceneService.cs`
- `Services/ScenePersistenceServices.cs`
- `tests/ListenSphere.Windows.TechnicalTests/R4ControllerTests.cs`

更新的主要入口与绑定：

- `ControllerViewModel.cs`
- `Presentation/ControllerDashboardViewModel.cs`
- `App.xaml.cs`
- `Views/SidebarView.xaml`
- `Views/ControllerDashboardView.xaml`
- `Views/ContentHeaderView.xaml`
- `Views/LocalApplicationsView.xaml`
- `Views/FirstRunGuideDialogView.xaml`
- `Design/ControllerDashboardDesignData.cs`
- `tests/ListenSphere.Architecture.Tests/ArchitectureTests.cs`

## 架构变化

`ControllerViewModel` 由承担场景、本机会话、诊断、首次引导和设置持久化的单体模型，缩减为负责初始化、子模型生命周期、Shell 导航和设置快照编排的聚合 Shell。

最终规模：

| 组件 | 大小 | 行数 |
| --- | ---: | ---: |
| `ControllerViewModel.cs` | 9,421 B | 234 |
| `SceneViewModel.cs` | 7,600 B | 243 |
| `DiagnosticsViewModel.cs` | 1,764 B | 58 |
| `FirstRunViewModel.cs` | 693 B | 29 |
| `LocalSessionsViewModel.cs` | 14,930 B | 405 |
| `AudioSessionItemViewModel.cs` | 12,840 B | 373 |
| `ControllerSettingsCoordinator.cs` | 5,255 B | 172 |

`ControllerViewModel.cs` 已低于 25 KB 目标，且不再直接包含会话卡片模型或 WPF 文件对话框。

## 行为变化

无计划内用户可见行为变化。保留现有：

- 场景文件 JSON 格式和字段语义；
- 设置版本、设置键和快照内容；
- 本机会话的聚合、音量、静音和路由语义；
- 首次引导、诊断导出及现有命令名称与可用条件；
- Windows/Android 协议及运行时音频链路。

## 自动测试

- Release 构建：通过，0 错误、0 警告。
- .NET 全量测试：154/154 通过。
  - Architecture：8/8
  - Core：52/52
  - Protocol：14/14
  - Windows Technical：80/80
- R4 专项测试：6/6 通过，覆盖去抖最新快照、关闭排空、设置加载、场景合法化、场景格式往返和首次引导落盘。
- Android：`assembleDebug testDebugUnitTest --no-daemon` 通过；25/25 单元测试通过，0 失败。
- Debug APK：`apps/ListenSphere.Mobile/app/build/outputs/apk/debug/app-debug.apk` 已生成，55,544,824 B。
- R4 修改文件格式校验：通过。
- `git diff --check`：通过。

全仓库 `dotnet format --verify-no-changes` 仍会报告既有 `BluetoothRfcommProbeHost.cs` 空白格式欠账；该文件不属于 R4 差异，本阶段未扩大修改范围。

## UI 冒烟与人工测试

已完成自动 UI 冒烟：

- Release Controller 成功显示，UI Automation 枚举到 212 个元素；
- “保存当前”“导入”“刷新应用”“导出诊断包”“生成配对码”等入口可见且启用；
- 无场景选中时，恢复、删除等依赖场景的命令保持禁用；
- 通过窗口关闭命令约 235 ms 正常退出；
- 关闭前后设置文件保持 v15，场景数、应用路由数和“跟随默认输出”状态不变。

人工视觉与交互回归尚待用户在最终启动的 Release 应用中验收，重点检查：侧栏场景操作、本机应用刷新/音量/静音/路由、诊断导出、首次引导和正常关闭。

## 已知风险

- 本次调整了多个内部绑定路径；自动页面加载与 UI Automation 已通过，但复杂实际设备组合仍需人工回归。
- 设置关闭竞态已有专项测试和优雅关闭冒烟覆盖；异常断电或进程强杀仍不保证最后一笔尚未落盘的 UI 修改被保存，这与原有持久化边界一致。
- Android 未改代码，沿用本阶段已完成的构建与单元测试结果，未重复执行真机音频回归。

## 阶段是否通过

R4 代码实施与自动门禁通过；阶段最终状态为“待人工验收”。人工验收通过前不进入 R5。