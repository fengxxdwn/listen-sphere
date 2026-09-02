# ListenSphere R3 UI 与展示层治理进度

## 阶段目标

R3 在 R2 已完成运行时职责拆分的基础上治理 WPF 展示层。阶段内只调整 XAML
组织、视图边界和展示层聚合，不改变音频链路、网络协议、设置结构、命令名称、
绑定路径或 Android 行为。

R3 共分 5 批：

1. 设计资源：调色板、画刷、基础控件和卡片样式资源字典。
2. 主窗口框架：标题栏、侧栏、页面容器和通用布局 UserControl。
3. 音源与路由：音源卡片、输出设备卡片和路由面板视图。
4. 调音与麦克风：EQ、动态处理、分组混音和麦克风中枢视图。
5. 展示层收口：页面级 ViewModel、转换器、设计时数据和最终回归。

## 当前结论

- R3 总进度：`4/5` 批代码与自动化门禁完成，等待第 4 批人工验收。
- 当前批次：调音、分组混音与麦克风中枢视图拆分。
- 业务行为变化：无。
- 协议、设置和 Android 变化：无。

## 第 1 批：设计资源拆分

### 实际修改

- 新增 `Themes/Palette.xaml`，集中保存 14 个既有颜色画刷。
- 新增 `Themes/Controls.xaml`，集中保存既有文本、卡片、按钮、开关、提示、
  滑块、进度条、滚动条、下拉框、文本框和列表样式。
- `Controls.xaml` 直接合并其依赖的 `Palette.xaml`，`MainWindow.xaml` 只加载
  控件字典；资源仍保持窗口级作用域，没有把默认样式提升到应用级。
- 页面主体内的局部模板、触发器和数据模板仍保留原位，等待后续视图拆分。

### 尺寸变化

| 文件 | 拆分前 | 拆分后 |
| --- | ---: | ---: |
| `MainWindow.xaml` | 179,304 B / 2,317 行 | 148,421 B / 1,825 行 |
| `Themes/Palette.xaml` | 不存在 | 1,006 B / 17 行 |
| `Themes/Controls.xaml` | 不存在 | 26,546 B / 487 行 |

本批减少的是主窗口单文件体积；资源总量没有通过删除视觉参数来缩减。

### 不变量

- 画刷 key、颜色值、Style key、TargetType、BasedOn 和 Setter 保持不变。
- 控件树、Binding、Command、事件处理器和 DataTemplate 保持不变。
- 字典只在 `MainWindow` 内合并，避免影响其他窗口。
- 不修改 Controller/Sender/Android 的运行时逻辑。

## 第 2 批：主窗口框架拆分

### 实际修改

- 新增 `Views/WindowTitleBar`，承接标题栏视觉、窗口拖动、双击最大化、最大化后
  还原拖动以及最小化、最大化/还原、关闭按钮行为。
- 新增 `Views/SidebarView`，承接控制中心状态、场景预设和一次性配对码区域。
- 新增 `Views/ContentHeaderView`，承接声音中枢标题、发送连接、网络状态和刷新操作。
- `MainWindow` 只保留窗口宿主、页面主体和页面级交互；删除已迁移的标题栏事件代码。
- 公共主题由 `App.xaml` 合并，保证独立 UserControl 在构造时即可解析静态资源。
  Controller 当前仅有一个主窗口，因此现有界面作用结果不变。

### 尺寸变化

| 文件 | 第 1 批后 | 第 2 批后 |
| --- | ---: | ---: |
| `MainWindow.xaml` | 148,421 B / 1,825 行 | 136,061 B / 1,603 行 |
| `MainWindow.xaml.cs` | 约 10.2 KB / 309 行 | 7,955 B / 249 行 |

新增框架视图：

- `WindowTitleBar.xaml`：2,719 B / 56 行。
- `WindowTitleBar.xaml.cs`：3,407 B / 117 行。
- `SidebarView.xaml`：5,588 B / 116 行。
- `ContentHeaderView.xaml`：2,711 B / 58 行。

### 第 2 批自动化门禁

- Controller Debug XAML 编译：通过，0 警告、0 错误。
- 全解决方案 Release build：通过，0 警告、0 错误，约 3.04 秒。
- 全量 .NET 测试：132/132 通过，四个测试项目分别保存 TRX。
- Android `assembleDebug testDebugUnitTest --no-daemon`：通过，约 9.78 秒。
- Android 单元测试 XML：25/25 通过，0 failures。
- Release Controller 启动 8 秒后主窗口句柄、标题和响应状态正常。
- `git diff --check`：无空白错误；仅有既有 LF/CRLF 提示。

原始输出保存在被忽略的 `TestResults/R3/batch-2/`。

## 第 3 批：音源与路由视图

### 实际修改

- 新增 `Views/AudioSourceRoutingView`，承接声音来源、远端声道、分组入口、播放
  详情、输出设备卡片、附加输出卡片及音源到输出设备的拖放路由。
- 新增 `Views/LocalApplicationsView`，承接本机应用标题、空状态、应用卡片、
  Windows 原始路由说明及聆界附加输出管理。
- 新增 `AudioRoutingDragState`，统一本机音源、远端音源和应用卡片的拖动阈值、
  交互控件排除、数据格式和拖放效果。
- 从 `MainWindow.xaml.cs` 删除已迁移的拖放、应用音量设置和附加输出事件代码。

### 应用多端点会话聚合

- 应用卡片以现有稳定应用身份（优先规范化程序路径）作为主键，不再以
  “输出设备 ID + Windows 会话 ID”生成多张卡片。
- 同一应用在多个 Windows 输出端点上的会话保留为卡片子会话；界面显示
  `Windows 输出：N 个设备`及端点名称。
- 卡片峰值取所有子会话最大值，发声状态为任一子会话活动，静音状态为全部子会话
  静音；用户调整卡片音量或静音时同步写入全部子会话。
- 聆界附加输出继续以稳定 `RoutingChannelId` 保存一次；删除、拖放和设置恢复不因
  Windows 子会话数量重复执行。
- 子会话消失时同步清理音量防抖、基础音量和静音缓存，应用最后一个会话消失时才
  注销本机应用捕获来源。

### 尺寸变化

| 文件 | 第 2 批后 | 第 3 批后 |
| --- | ---: | ---: |
| `MainWindow.xaml` | 136,061 B / 1,603 行 | 48,394 B / 738 行 |
| `MainWindow.xaml.cs` | 7,955 B / 249 行 | 3,291 B / 118 行 |

新增视图及交互：

- `AudioSourceRoutingView.xaml`：55,215 B / 611 行；其中调音和分组区域将在第 4 批
  继续拆分，不能作为最终文件尺寸。
- `LocalApplicationsView.xaml`：15,960 B / 269 行。
- 两个视图 code-behind 合计 116 行；共享拖放状态 81 行。

### 第 3 批自动化门禁

- Controller Debug XAML 编译：通过，0 警告、0 错误。
- 全解决方案 Release build：通过，0 警告、0 错误，约 3.05 秒。
- 全量 .NET 测试：136/136 通过。
  - Architecture：2
  - Core：52
  - Protocol：14
  - Windows Technical：68
- 新增 1 项多端点应用卡片回归测试，覆盖稳定身份、两个子会话、端点汇总、峰值、
  活动状态及音量/静音扇出。
- Android `assembleDebug testDebugUnitTest --no-daemon`：通过，约 12.61 秒。
- Android 单元测试 XML：25/25 通过，0 failures，0 errors。
- Release Controller 启动 8 秒后主窗口句柄、标题和响应状态正常。
- 运行时 UI Automation 冒烟检查：当前 `wallpaper64` 应用名称节点为 1，输出摘要
  为 `Windows 输出：扬声器 (MCHOSE V9 PRO)`，未再出现重复应用卡片。
- 人工回归发现音频连接期间添加附加输出会卡顿，删除时可能长时间冻结。根因是
  WASAPI 端点构造、启动、停止或释放可能在异步方法返回第一个未完成任务之前同步
  阻塞，调用链仍位于 UI Dispatcher。现将完整路由变更事务调度到后台，并复用
  `reconcileGate` 串行化设备协调、添加和删除；添加时先发布“正在路由”快照，删除
  时先移除配置、活动路由和 UI 投影，再后台停止捕获与播放端点。
- 新增 1 项同步阻塞回归测试：模拟播放端点启动和停止各阻塞 400 ms，验证添加和
  删除 API 均在 250 ms 内返回调用线程，随后资源生命周期完整结束。
- 继续修复“添加尚未结束时删除失败”的竞态：每次路由意图分配递增版本，删除先同步
  清除配置、活动路由和 UI 投影，再等待协调锁释放后台资源；仍在执行的旧添加任务会
  因版本失效而停止发布，并由删除任务回收其延迟创建的播放端点，避免音源重新出现。
- 新增 1 项删除竞态回归测试：模拟播放端点启动阻塞 600 ms，在启动期间删除音源，
  验证路由立即从快照消失、添加结果失效、后台结束后不复现且端点完整停止和释放。
- 实机复查发现 Windows 播放设备集合重建后，`SelectedValue` 未能恢复当前默认设备，
  同一轮设备变化还会因互斥分支跳过附加输出投影刷新。现改为按当前设备 ID 计算稳定
  `SelectedIndex`（忽略刷新期间的临时 `-1`），并在设备集合或主设备任一变化时都刷新
  本地路由投影。UI Automation 验证 3 个播放设备中已有且仅有 1 个选中项；手动调用
  两个路由删除按钮后，界面删除项与持久化 `audioOutputRoutes` 均从 2 条归零。
- 后续复现确认删除还存在两类晚到覆盖：并发快照可能复用修订号并让旧路由投影最后
  写回，删除前的设置保存也可能晚于删除后的空配置落盘。现分别串行化路由快照生成、
  为设置保存增加最新请求版本门禁，并将行尾删除改为具名 WPF `Click` 入口；点击后
  强制应用最新快照并显示具体设备/音源反馈。新增 1 项 64 路并发发布测试，验证修订号
  唯一连续；测试遗留路由已清空。
- `git diff --check`：无空白错误；仅有既有 LF/CRLF 提示。

原始输出保存在被忽略的 `TestResults/R3/batch-3/`。

## 第 4 批：调音与麦克风视图

### 实际修改

- 新增 `GroupMixerView`，承接分组总线、自动路由开关、已学习规则及规则删除入口；
  外层 Popup、开关状态和 PlacementTarget 仍由音源路由视图持有。
- 将声道调音拆为 `ChannelTuningHeaderView`、`ChannelDynamicsView` 和
  `ChannelEqualizerView`，分别承接来源标题/用途分组/预设、前级与动态处理、十段 EQ
  与曲线；原 Popup 和远端声道 DataContext 保持不变。
- 新增 `MicrophoneHubView` 聚合麦克风中枢，并进一步拆分 `MicrophoneRoutingView` 与
  `MicrophoneMonitoringView`；Windows 麦克风设置跳转事件从 `MainWindow` 迁入
  `MicrophoneRoutingView`。
- 未修改绑定路径、命令、设置结构、音频协调器、网络协议或 Android 行为。

### 尺寸变化

| 文件 | 第 3 批后 | 第 4 批后 |
| --- | ---: | ---: |
| `MainWindow.xaml` | 48,394 B / 738 行 | 35,561 B / 563 行 |
| `MainWindow.xaml.cs` | 3,291 B / 118 行 | 2,412 B / 90 行 |
| `AudioSourceRoutingView.xaml` | 55,215 B / 611 行 | 29,150 B / 398 行 |

新增 XAML 视图合计 434 行；其中分组混音 113 行、调音三个子视图 129 行、麦克风中枢
三个视图 192 行。拆分仅改变文件所有权，不删减现有控件。

### 第 4 批自动化门禁

- Controller Debug XAML 编译：通过，0 警告、0 错误。
- 全解决方案 Release build：通过，0 警告、0 错误，约 2.60 秒。
- 全量 .NET 测试：136/136 通过（Architecture 2、Core 52、Protocol 14、
  Windows Technical 68）。
- Android `assembleDebug testDebugUnitTest --no-daemon`：通过，约 9 秒；单元测试
  XML 25/25 通过，0 failures，0 errors，Debug APK 存在。
- Release Controller 启动 8 秒后窗口标题、句柄和响应状态正常；UI Automation
  验证麦克风中枢、启用开关、4 个设备下拉控件及分组入口存在，分组 Popup 可打开且
  “分组混音器”内容成功呈现。

## 自动化门禁

- Controller Debug XAML 编译：通过，0 警告、0 错误。
- 全解决方案 Release build：通过，0 警告、0 错误，约 3.64 秒。
- 全量 .NET 测试：132/132 通过。
  - Architecture：2
  - Core：52
  - Protocol：14
  - Windows Technical：64
- Android `assembleDebug testDebugUnitTest --no-daemon`：通过，约 12.70 秒。
- Android 单元测试 XML：25/25 通过，0 failures，0 errors。
- Debug APK：生成成功。
- `git diff --check`：无空白错误；仅有既有 LF/CRLF 提示。
- 首次人工启动发现 WPF 兄弟级合并字典不能可靠满足 `StaticResource` 加载时依赖，
  导致 `Foreground=UnsetValue` 并在首次布局时退出。已改为由 `Controls.xaml`
  直接合并 `Palette.xaml`；Release 重新构建 0 警告、0 错误，启动 8 秒后主窗口
  句柄和标题均正常，进程持续响应。

原始输出保存在被忽略的 `TestResults/R3/batch-1/`。

## 人工验收清单

- 启动后窗口背景、侧栏、卡片、文字颜色与拆分前一致。
- Button、ToggleButton、CheckBox、ComboBox、Slider 和 ScrollBar 的 hover、
  pressed、checked、disabled 状态与拆分前一致。
- 窗口标题栏最小化、最大化、关闭按钮外观和交互正常。
- 各页面切换、音源卡片、路由展开区、麦克风中枢无资源缺失或白色默认控件。
- 高 DPI、窗口缩放和圆角边界无新增视觉异常。

第 3 批人工验收需确认同一 PID 跨两个 Windows 输出端点只显示一张应用卡片，
路由展开区准确列出多个端点，应用音量/静音控制全部子会话；同时回归本机、远端、
应用卡片拖放以及附加输出添加/删除同步。验收通过后进入第 4 批调音与麦克风视图。

R3 完成后新增独立功能阶段“主控端代理麦克风与反向音频链路”，不插入当前结构
治理批次。实施范围和兼容边界见
[`future-controller-proxy-microphone-plan.md`](../development/future-controller-proxy-microphone-plan.md)。
