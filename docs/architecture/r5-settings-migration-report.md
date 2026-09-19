# R5 配置迁移阶段报告

日期：2026-09-19
基线：aa688c1
分支：codex/r5-settings-migration

## 实际修改
新增纯函数 ISettingsMigration（SourceVersion / TargetVersion / Migrate），SettingsMigrationPipeline 提供 v10→11→12→13→14→15 连续链。
v10→11 从语音/游戏/媒体分组推导显式语音闪避角色；v11 以后步骤推进版本，沿用现有配置契约初始化默认值，保留显式值。
读取先解析 JSON，预检查版本信封（防止未来版本字段类型变化导致误判损坏），再反序列化契约、版本检查/迁移、归一化和验证。
读取迁移不直接改写文件，正常保存使用当前版本。

## 文件变更
- src/ListenSphere.Configuration/ISettingsMigration.cs
- src/ListenSphere.Configuration/SettingsMigrationPipeline.cs
- src/ListenSphere.Configuration/JsonSettingsStore.cs
- apps/ListenSphere.Controller/Services/ControllerSettingsCoordinator.cs
- apps/ListenSphere.Controller/ControllerViewModel.cs
- apps/ListenSphere.Sender/SenderViewModel.cs
- tests/ListenSphere.Core.Tests/SettingsMigrationTests.cs

## 架构变化
版本兼容规则从归一化中移至纯迁移管线。存储层持有不兼容状态并拦截后续保存，覆盖 Controller、Sender、关闭落盘和直接调用 Save 的入口。
保留原子临时文件替换机制，在写入前验证数值可序列化。

## 行为变化
v1–9、未来版本及缺失版本：使用内存默认值，保留原文件，不改名、不覆盖；Controller 和 Sender 在启动后显示不兼容提示。
损坏 JSON 继续备份到 corrupt 文件，并允许默认值正常落盘。
支持版本继续读取原有字段、场景和设置结构；未修改协议或音频行为。

## 自动测试
- Release build：0 错误、0 警告；最终构建耗时 3.20 秒。
- 全量 .NET：180/180（Architecture 8、Core 76、Protocol 14、Windows Technical 82）。
- 新增 R5 专项用例 24 项：每步迁移、v10–15 直升、幂等、输入不变、语音闪避、非法音量归一化、v1–9/未来版保护、缺失版本、未来字段类型变化、未加载先保存、取消/非法保存保留旧文件、损坏备份恢复。
- TRX：TestResults/R5/final（Git 忽略）。
- Android：JDK17、SDK34、Gradle8.10.2，通过临时 L: 盘符执行 assembleDebug testDebugUnitTest --no-daemon --offline。
- Android 构建成功，42 个任务使用有效增量缓存；XML 25 项、失败/错误 0，APK 55,544,824 B。未宣称重新执行了缓存命中的测试。
- 默认 Gradle wrapper 下载较慢，取消后改用 E:/Android/Gradle 已安装的同版本缓存；未修改依赖版本。临时 L: 已清理。
- Android 保留现有 SDK XML/实验性路径选项工具警告。
- git diff --check 通过。
- UI Automation：Release Controller 正常加载，检测到 206 个元素。

## 人工测试
待用户验收正常启动、场景/设置保存、重启保留以及不兼容提示。
未替换用户真实配置来测试旧版本；文件兼容与保存保护测试均使用独立临时目录。
未执行 Android 真机及多设备音频人工回归。

## 已知风险
迁移最低支持 v10；不兼容模式下用户本次调整仅保存在内存中，启动提示明确说明不保存。
Windows 实际硬件/UI 提示仍需人工验收；自动 UI 检查仅验证正常页面加载。
跨进程同时修改同一配置文件的协调不在本次新增范围，沿用已有同卷原子替换保障。

## 阶段是否通过
代码和自动门禁通过，待人工验收。R5 独立分支保留，验收后合并；未进入 R6。
