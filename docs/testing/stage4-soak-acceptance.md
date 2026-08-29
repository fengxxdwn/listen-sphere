# 阶段 4 第四批：传输稳定性验收

## 自动验收

```powershell
dotnet build ListenSphere.sln -c Release --no-restore
dotnet test ListenSphere.sln -c Release --no-build
.\scripts\Invoke-ListenSphereAndroidTests.ps1 -Configuration All `
    -GradleUserHome E:\Android\Gradle `
    -GradleExecutable E:\Android\Gradle\wrapper\dists\gradle-8.10.2-bin\e0thjr3we83usdufs66z371ne\gradle-8.10.2\bin\gradle.bat
```

自动测试包含 30 分钟等效的 10 ms 音频帧序列，以及 10,000 次无线网络/蓝牙
重连状态切换。它验证缓冲有界、丢帧补偿、退避隔离和状态重置，但不替代真实无线硬件测试。

Android 测试包装脚本会在仓库路径包含中文字符时临时映射英文盘符，规避 Gradle 测试
工作进程的类路径问题；脚本退出时会自动撤销映射。`GradleUserHome` 应指向本机 Gradle
缓存，未指定时沿用当前环境变量。
若本机已有 Gradle，可用 `GradleExecutable` 直接指定，避免 Wrapper 再次下载；其他机器可
省略该参数并使用项目自带 Wrapper。

## 真实链路耐久测试

从仓库根目录启动监控：

```powershell
.\scripts\Invoke-ListenSphereSoak.ps1 -Application Controller -DurationMinutes 30 -KeepRunning
```

脚本启动 Release Controller，每 5 秒记录窗口响应、工作集、私有内存、句柄、线程和
CPU 时间。结果保存在 `artifacts/stage4-soak` 的 CSV 与 JSON 中。测试期间由测试人员在
移动端完成连接与播放操作；脚本不会操作移动设备，也不会采集音频。

## 测试矩阵

每项至少连续运行 30 分钟；发布候选版本建议延长到 60 分钟。

| 场景 | 操作 | 通过标准 |
| --- | --- | --- |
| 同一路由器 Wi-Fi | 连续播放，并切换一次输出设备 | 无闪退；无持续卡顿；会话可恢复 |
| Windows 移动热点 | 平板连接电脑热点并连续播放 | 能发现或手动连接主控端；无重连循环 |
| 蓝牙 RFCOMM | 分别测试 IMA ADPCM、AAC-LC | 无持续掉帧；诊断统计持续更新 |
| 双蓝牙 | 蓝牙来源输出到蓝牙耳机/音箱 | 自动启用抗抖配置；优先使用压缩编码 |
| 模式切换 | Wi-Fi → 蓝牙 → Wi-Fi，各 10 次 | 旧会话释放；设备归属正确；不自动抢连 |
| 主动断连 | Controller 主动断连 10 次 | 移动端不自动重连，用户重连仍可用 |

## 记录要求

- 保存脚本生成的 CSV 和 JSON。
- 导出一次应用诊断包，确认包含网络/蓝牙质量统计。
- 记录设备型号、Windows 蓝牙适配器、编解码格式、声道模式和输出设备。
- 若失败，记录发生时间和界面错误文字；不要提交 PCM、密钥、验证码或证书。

真实设备矩阵需要人工操作，因此只有完成上述矩阵后才能标记“人工验收通过”。
