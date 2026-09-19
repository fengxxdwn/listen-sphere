# R6 Network 拆分报告

日期：2026-09-19。基线：0c0d07f。分支：codex/r6-network-decomposition。

## 实际修改

将 ControlChannel.cs 和 UdpAudioTransport.cs 按已有职责机械拆分。原 namespace、程序集、公开类型、方法签名及调用语义保留。
控制连接通过 partial 文件分离握手代码，运行时仍为同一个对象；前缀读取迁至 ControlTransportPreface 的 internal 方法。

## 文件变更与架构变化

| 文件 | 职责 | 拆分后约大小 |
| --- | --- | ---: |
| ControlChannel.cs | 既有控制契约、身份辅助类型 | 2 KB |
| ListenSphereControlServer.cs | 服务监听、消息处理、流管理和释放 | 18 KB |
| ListenSphereControlServer.Connection.cs | 服务端 TLS、Hello、身份验证和连接处理 | 10 KB |
| ListenSphereControlClient.cs | 客户端消息、心跳和释放 | 11 KB |
| ListenSphereControlClient.Connection.cs | 客户端连接、配对及音频会话解析 | 8 KB |
| ControlTransportPreface.cs | 传输前缀解析与读取 | 2 KB |
| UdpAudioTransport.cs | 原有音频会话、帧和统计契约 | 3 KB |
| UdpAudioSender.cs | 加密、分片与发送 | 4 KB |
| UdpAudioReceiver.cs | 接收、认证、会话资源与输出编排 | 11 KB |
| AudioFrameReassembler.cs | 分片重组和过期处理 | 3 KB |
| AudioJitterBuffer.cs | 抖动缓冲及缺帧补偿 | 6 KB |
| PacketSequenceTracker.cs | 缺包、重复、迟到及回绕跟踪 | 2 KB |

ControlChannel.cs 原为 49,112 B，UdpAudioTransport.cs 原为 27,565 B。
原 ListenSphereControlServer、ListenSphereControlClient、UdpAudioSender、UdpAudioReceiver 继续作为兼容入口；未增加重复包装对象或 csproj。
Connection 是文件职责边界，不是独立资源所有者。监听器、Socket、TLS、取消源和会话仍由原对象管理，避免机械拆分改变释放次序。

## 行为变化

无计划内行为变化。protobuf 字段、帧前缀、TLS 参数、UDP 包头、错误消息与取消路径保持原实现。
逐类原文比对确认 Sender、Receiver、FrameReassembler、JitterBuffer、SequenceTracker 方法体相同；控制连接方法体除前缀读取调用限定名外相同。最后仅整理空白格式。

## 自动测试

- Release build：0 错误、0 警告，9.26 秒。
- 全量 .NET：183/183（Architecture 8、Core 79、Protocol 14、Windows Technical 82）。TRX 位于被忽略的 TestResults/R6。
- 新增 NetworkDecompositionTests：3 项通过，覆盖乱序/重复分片、分片缺失及 200 ms 超时、发送取消和释放后密钥清零。
- 既有加密 UDP 端到端、抖动补帧、序号回绕、TLS 配对/证书固定/重连和协议固定向量测试全部通过。
- Android：JDK17 / SDK34 / Gradle8.10.2，assembleDebug testDebugUnitTest --no-daemon --offline --rerun-tasks 成功，42 个任务重新执行，49 秒；XML 25/25，失败/错误 0。
- Debug APK 已重新生成，55,544,816 B；保留于 Gradle 输出目录。
- 临时英文盘符 L: 已清理。
- git diff --check 通过。
- UI Automation：Release Controller 正常显示，318 个元素；窗口保留用于人工验收。

## 人工测试

待用户验证无线、蓝牙、USB 的连接/主动断连/重连；本机、附加输出、麦克风、EQ、Dynamics、Ducking 和 Group Bus 音频回归。
本次未声称已完成多设备真机测试。

## 已知风险

本阶段采用机械迁移和 partial 连接文件，尚未把每个连接的状态提取为独立对象；这是保留资源所有权的明确选择。
Android 工具仍输出既有 SDK XML 版本与实验性路径选项警告，构建和测试成功。
实际设备传输回归仍待人工确认。

## 阶段是否通过

代码与自动门禁通过，待人工验收；独立分支提交，验收后合并 main。未进入 R7。
