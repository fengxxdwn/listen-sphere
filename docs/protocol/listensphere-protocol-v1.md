# ListenSphere Protocol v1 草案

## 1. 范围和版本

ListenSphere Protocol 是与语言和平台无关的局域网协议。v1 包含发现、控制、配对和音频数据报规范。

- Protobuf 包：`listensphere.protocol.v1`
- 当前版本：`1.0`
- Major 不一致：立即拒绝并返回 `INCOMPATIBLE_VERSION`
- Major 相同、Minor 不同：保留未知字段，通过能力位协商
- 删除 Protobuf 字段后必须同时保留字段编号和名称
- 所有长度在分配内存前校验

## 2. 设备发现

主控端使用 mDNS/DNS-SD 发布 `_listensphere._tcp.local.`。

SRV：

- Target：主控端局域网主机名
- Port：TLS 控制通道端口
- TTL：建议 120 秒；每 60 秒刷新

TXT：

| Key | 含义 | 示例 |
|---|---|---|
| `pv` | 协议 Major.Minor | `1.0` |
| `id` | 小写 UUID | `00112233-4455-6677-8899-aabbccddeeff` |
| `name` | UTF-8 用户可见设备名，最长 64 字节 | `游戏主机` |
| `platform` | 平台标识 | `windows` |
| `caps` | 十六进制能力位 | `0000000000000005` |

TXT 不发布验证码、IP、证书私钥、会话密钥或应用列表。高级设置允许手动输入 IP/主机名和控制端口，但不定义第二套广播协议。

## 3. 控制通道

- 传输：TCP + TLS 1.3
- TLS 身份：每个安装生成并持久保存本地身份；配对后固定 SHA-256 公钥/证书指纹
- 帧：4 字节大端无符号长度，随后是一个 Protobuf `Envelope`
- 最大控制消息：1 MiB
- 空闲连接：每 2 秒心跳；连续 3 次超时转为断开
- `request_id`：请求方生成，用于关联响应；通知可为 0

控制通道负责配对、心跳、能力协商、开始/停止流、会话密钥传递、设备状态和错误消息。不得在控制通道传送连续 PCM。

P4 的 `StartStream` 由 Controller 在设备认证完成后发送，包含随机 SessionId、非零 StreamId、AES-256-GCM 临时密钥、4 字节 salt、UDP 端口和固定音频格式。密钥只存在于当前 TLS 连接及内存中；控制连接断开、设备撤销或流重启时立即作废。

v1 控制序列：

1. Sender 完成 TLS 1.3 握手后首先发送 `HelloRequest`，身份中的证书指纹必须与 TLS 客户端证书一致。
2. Controller 返回 `HelloResponse`，说明该 UUID/指纹是否已受信任。
3. 未受信任设备发送 `PairRequest`；成功后 Controller 返回包含自身身份的 `PairResponse`。
4. 已认证连接每 2 秒发送 `Heartbeat`，对端以相同 `request_id` 返回 `HeartbeatAck`。
5. 正常退出或协议错误使用 `Disconnect`；传输中断则直接进入离线状态。

`Envelope` 当前字段编号 10–18 已进入兼容性约束；删除消息字段时必须在 Protobuf 中 `reserved`，不得复用编号。

## 4. 配对

1. 主控端首次运行生成 TLS 身份和稳定设备 UUID。
2. 发送端通过 mDNS 找到主控端并建立临时 TLS 连接；此时只允许配对消息。
3. 主控端生成六位十进制验证码，显示 120 秒；同一来源最多连续失败 5 次，随后冷却 10 分钟。
4. 用户在发送端输入验证码。
5. `PairRequest` 携带发送端身份、公钥指纹、32 字节随机数和验证码。
6. 主控端原子地消费验证码，记录设备 UUID 与指纹，返回控制端随机数。
7. 发送端保存主控端指纹；双方后续连接都必须校验固定身份。
8. 撤销后删除信任记录和现有会话密钥，强制断开活动连接。

验证码不是长期密钥。首次配对模型以可信局域网和用户观察主控端屏幕为前提；主动转发攻击是 MVP 的剩余风险，后续可增加双端短认证字符串确认。

## 5. 音频格式

默认协商值：

| 属性 | v1 默认 |
|---|---|
| Sample rate | 48,000 Hz |
| Sample format | IEEE 754 Float32 little-endian |
| Channels | 2，交错立体声 |
| Frame duration | 10 ms（480 sample frames） |
| Optional frame duration | 20 ms（960 sample frames） |
| Codec | PCM Float32 |

Opus 的编号已保留，但 MVP 不发送 Opus。时间戳表示从流起点开始的 sample frame 数，不使用墙上时钟。

## 6. UDP 音频包头

所有多字节整数均为小端。固定头为 60 字节，最大 UDP 数据报为 1,200 字节。

| Offset | Size | 字段 |
|---:|---:|---|
| 0 | 4 | ASCII `LSPA` |
| 4 | 1 | MajorVersion |
| 5 | 1 | MinorVersion |
| 6 | 2 | Flags；bit 0 表示 AES-GCM |
| 8 | 2 | HeaderLength，v1 固定 60 |
| 10 | 1 | Codec；1=PCM Float32，2=Opus（保留） |
| 11 | 1 | SampleFormat；1=Float32 LE |
| 12 | 1 | ChannelCount |
| 13 | 1 | Reserved，必须写 0 |
| 14 | 4 | SampleRate |
| 18 | 2 | FrameSamples |
| 20 | 1 | FragmentIndex，从 0 开始 |
| 21 | 1 | FragmentCount，至少 1 |
| 22 | 16 | RFC 4122/network-order SessionId UUID |
| 38 | 4 | StreamId |
| 42 | 4 | PacketSequence |
| 46 | 4 | FrameSequence |
| 50 | 8 | Timestamp，单位为 sample frame |
| 58 | 2 | PayloadLength |

校验顺序：数据报最小长度 → Magic → Major → HeaderLength → 枚举和格式范围 → 分片范围 → 精确 PayloadLength → SessionId/StreamId → AEAD → 去重/重组。失败包直接丢弃并只增加聚合计数。

`PacketSequence` 每个数据报递增并允许 `uint32` 自然回绕；`FrameSequence` 每个完整音频帧递增。一个帧的全部分片共享 SessionId、StreamId、FrameSequence、Timestamp 和格式。

## 7. 分片和重组

- 单片明文预算由 `1200 - 60 - 16` 计算；16 字节为 AES-GCM Tag。
- FragmentCount 最大 255；超过限制的帧不得发送。
- 重组键为 `(SessionId, StreamId, FrameSequence)`。
- 重复 FragmentIndex 丢弃。
- 缺片超过抖动窗口后丢弃整帧并输出等长静音。
- 不等待过期帧，不允许缺帧导致缓冲无限增长。

## 8. 音频加密

控制通道在认证成功并开始流时传递随机 256 位会话密钥和 4 字节 session salt。每个数据报使用 AES-256-GCM：

- Nonce：`session salt (4) || StreamId LE (4) || PacketSequence LE (4)`
- AAD：完整 60 字节包头
- Payload：ciphertext 后接 16 字节 authentication tag
- PayloadLength：包含 ciphertext 和 tag

同一密钥下不得复用 `(StreamId, PacketSequence)`；流重启或序号可能重用前必须协商新密钥。认证失败的负载不得进入重组或音频队列。

## 9. 错误与隐私

稳定错误码在 `control.proto` 中定义。未知错误码显示通用中文错误，同时保留数值用于诊断。

协议日志允许记录版本、匿名会话关联号、延迟、丢包、缓冲欠载/溢出和错误码；禁止记录 PCM、验证码、会话密钥、私钥、完整证书、未过滤的设备名或应用列表。

## 10. 测试向量

`protocol/test-vectors/audio-header-v1.hex` 是固定 v1 包头的跨语言十六进制测试向量。UUID 使用 RFC 4122 字节顺序，其余多字节字段使用小端。未来客户端必须能解析该向量并重新生成完全相同的 60 字节结果。
