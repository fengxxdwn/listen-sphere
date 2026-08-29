# ListenSphere P3 实施状态与验收

> 状态更新（2026-08-24）：自动验收已通过；受当前测试环境限制，双电脑局域网人工验收延期。该延期不阻塞 P4 本机开发，但在首个对外发布版本前必须补做。

## 已实现

- Controller 通过 DNS-SD 发布 `_listensphere._tcp.local`，端口来自 SRV。
- TXT 仅包含协议版本、设备 UUID、名称、平台和能力。
- Sender 持续执行 mDNS 查询，按设备 UUID 去重并显示 Controller。
- 控制通道使用 TCP、TLS 1.3、双向自签名设备证书和 Protobuf。
- 控制消息使用 32 位大端长度前缀，单消息上限 1 MiB。
- 首次配对采用六位单次验证码；有效期 5 分钟，连续失败 5 次后限制 10 分钟。
- 配对绑定设备 UUID、TLS 证书 SHA-256 指纹和双方随机数。
- 双方将信任记录保存到 `%LocalAppData%\ListenSphere\<Role>`。
- 后续连接必须匹配固定证书指纹；Sender 发现已信任 Controller 后自动连接。
- 每 2 秒发送心跳，6 秒未收到正确响应视为断线。
- Controller 可以查看可信设备并撤销信任。
- Protocol v1 已加入 Hello、Pair、HeartbeatAck 和 Disconnect 消息。

## 安全边界

- 首次配对使用临时自签名 TLS 与用户核对验证码；可信局域网 MVP 仍保留主动中间人攻击风险。
- 配对后使用双向证书固定，设备 UUID 相同但证书变化会拒绝连接。
- 身份私钥以 PFX 保存并由 Windows 当前用户密钥容器加载。
- TXT 不发布证书指纹、验证码、密钥或其他敏感信息。
- P3 不发送 PCM 或音频会话密钥；UDP 音频密钥下发属于 P4。

## 自动验收

```powershell
dotnet restore ListenSphere.sln
dotnet build ListenSphere.sln -c Release --no-restore
dotnet test ListenSphere.sln -c Release --no-build
```

TLS 集成测试在 Windows 上需要访问当前用户证书密钥容器；受限沙箱运行时应在正常用户权限环境执行。

测试覆盖：

- 控制帧大端长度、往返、超长与截断拒绝。
- 六位验证码格式、单次使用。
- 信任记录持久化与撤销。
- 本机真实 TLS 1.3 首次配对、双方固定证书与无验证码重连。
- 原有协议、音频核心、架构和 Windows 技术测试回归。

## 人工验收流程

1. 在同一局域网的两台 Windows 设备上分别启动 Controller 和 Sender。
2. Controller 应显示 `_listensphere._tcp.local` 正在发布及 TCP 端口。
3. Sender 应在约 3 秒内自动显示 Controller 名称和 IP，无需手工输入地址。
4. Controller 点击“生成验证码”，确认显示六位数字和过期时间。
5. Sender 选择 Controller、输入验证码并点击“连接/配对”。
6. 双方状态应变为 Connected；Controller 的可信设备列表出现 Sender。
7. 输入错误验证码 5 次，确认后续尝试被限流；重新测试前等待限流结束或清理测试身份目录。
8. 关闭 Sender，Controller 应在数秒内显示设备离线。
9. 重新启动 Sender，不输入验证码；发现 Controller 后应自动重连。
10. 在 Controller 撤销 Sender 信任，重新连接应要求新验证码。
11. 重启双方，确认设备 UUID 与信任记录保持不变。
12. 保持连接 10 分钟，确认心跳稳定、无持续 CPU/内存增长和 UI 卡顿。

## P4 入口

P3 人工验收通过后进入 P4：控制通道下发 UDP 会话密钥，Sender 将 48 kHz Float32 双声道 10 ms 帧分片、AEAD 加密并通过 UDP 发送，Controller 完成校验、重组、抖动缓冲和播放。
