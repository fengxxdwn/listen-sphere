# ListenSphere P2 实施状态与验收

## 已实现

- 枚举 Windows 默认 Multimedia Render Endpoint 上的 Audio Session。
- 使用 Session Instance ID 作为运行期稳定标识，并过滤 Expired 会话。
- 读取应用名称、图标、进程、会话状态、音量、静音和 Audio Meter Peak。
- 每 500 ms 刷新快照，新会话自动出现、过期会话自动移除。
- 每个会话可独立控制系统音量与静音；音量滑块使用 120 ms 防抖。
- P2 只操作 Windows 已有 Audio Session，不捕获或重放独立应用 PCM。

## 自动验证

- Release 构建零警告、零错误。
- 自动测试覆盖名称回退、真实会话快照范围、Session ID 唯一性和监控启停。

## 人工验收结论

2026-07-29，用户确认 P2 人工审核流程全部通过，P2 标记完成并进入 P3。

## 当前边界

- 只监控 Windows 当前默认 Multimedia 输出端点。
- 某些 UWP、系统或受保护进程可能不提供可执行文件路径，此时使用回退名称和占位图标。
- 一个应用创建多个 Windows Audio Session 时仍显示多个独立卡片。
- 应用停止发声但会话尚未 Expired 时，卡片继续存在属于正常生命周期。
