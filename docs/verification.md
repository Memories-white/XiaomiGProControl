# 验证记录与完成边界

> 汇总日期：2026-11-06。历史温度和转速只表示采样结果，不是持续压力测试。

## 测试分层

| 类别 | 工具 | 是否访问硬件 |
| --- | --- | --- |
| 协议测试 | `GProControl.ProtocolSmokeTests` | 否 |
| 合成日志 / 配置测试 | `GProControl.HardwareLogTests` | 否 |
| 本机遥测 | `GProControl.TelemetrySmokeTests` | 只读 |
| 服务探针 | `GProControl.ServiceProbe` | 依参数决定；`--hex`/`--wmi` 会提交请求 |
| 设备验证 | `GProControl.HardwareSmokeTests` | 依参数决定，部分测试会写入并恢复 |
| 灯效探针 | `GProControl.LightingProbe` | 默认只读，带 validate/restore 参数可能写入 |
| 安装验收 | `scripts/test-installer.ps1` | 安装系统集成；应用 smoke 模式跳过恢复写入 |

CI 仅构建主程序及运行前两组离线测试，不执行真机写入工具。

## 已记录的验证

### 原厂服务与设备

- 四档性能模式经过实际写入与 EC 回读，受控测试后恢复原模式。
- 屏幕加速使用 `NotifyMsg31` 确认结果。
- Fn、Windows 键锁和触控板经服务执行记录及用户实际行为验证。
- 管道就绪必须完成真实连接和注册，不能只看管道对象存在。
- 自动自检：`selftest=True; sharedTask=True; cachedResult=True`。
- 显卡模式只读：`graphicVerified=True; graphicMode=1`。
- 同一模式不重复写入、非法模式拒绝：`noWriteGuards=True`。
- 显卡模式写入仍要求重启生效；最近一次只读验证没有切换或重启电脑。

### 传感器

管理员直接读取 `0x0900`，记录到：

```text
cpuTemp=90; gpuTemp=56; cpuFan=4707; gpuFan=4568
```

字段范围校验会拒绝无法解释的值。普通权限读取曾返回拒绝访问，应用因此需要管理员权限。

### 键盘灯效

```text
wave-roundtrip palette=2->0->2 direction=0->1->0
               brightness=3->0->3 speed=3->1->3 restored=True

power-off=verified
power-roundtrip=on->off->on restored=True
```

读取、写入、保存与恢复均使用 CMD 1/2/5，硬件版本由 CMD 6 检查。
回滚曾出现一次亮度延迟生效，已加入有限重试。配置测试覆盖旧版本兼容、关闭状态
保留波浪参数、重新加载和关闭恢复选项。

### 安装器

0.11.0 在 2026-11-06 再次完整通过以下安装验收，文件数仍为 527。新增的后台验收
确认托盘注册成功、主窗口隐藏及恢复：`BackgroundLifecyclePassed=True`。
离线配置测试通过旧设置兼容、记住后台选择、后台启动偏好及重新开启关闭询问。
关闭对话框逐项人工点击、真实重启登录后的自启动仍需补充验收；本次未重启系统。

0.10.0 分类安装包在独立临时目录中完成：

| 检查项 | 结果 |
| --- | --- |
| 首次安装 / 覆盖重装 / 卸载 | 返回码均为 0 |
| 集中语言目录 | 86 个，172 个资源文件 |
| 安装文件与运行缓存哈希 | 527 个文件一致 |
| 启动窗口 | `G Pro Control`，正常响应 |
| 用户设置 | 保留 |
| 与测试副本无关的已有开机任务 | 保留 |
| 测试安装与本版本缓存 | 卸载清理成功 |

安装包没有 Authenticode 签名。发布者文本与数字签名不是同一件事。

## 尚未完成

- 完整 Windows 重启后的灯效恢复验收。
- 波浪以外效果、其他档位、四区 RGB 和机身灯。
- 自定义风扇曲线 / 固定 RPM 的协议与读回。
- WLAN 强制加速开关；当前依赖原厂路由器路径。
- 跨机型、跨原厂软件版本和跨键盘硬件版本兼容。
- 压力、长时间运行和睡眠恢复的系统性测试。

本仓库发布整理后的结果与可复现测试代码，不发布包含设备标识和用户信息的完整运行日志。
