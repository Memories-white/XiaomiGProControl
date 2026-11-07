# 我的服务与详细参数

## 原厂内容核对

静态读取原厂 2.1.1.418 的 `dist/static/js/main.js`，先解析 Unicode 转义，再定位菜单与配置对象。
原厂“详细参数”呈现整机/序列号/BIOS、CPU、显卡、硬盘、内存及屏幕；“我的服务”分为
服务与反馈、用户中心、关于。服务链接包括手册、售后政策、服务网点、提交反馈和在线客服。
输入 SHA-256 与灯效分析文档相同；这里只复用事实参数和公共链接，没有打包原厂前端。

## 本项目实现

| 内容 | 数据来源 / 行为 |
| --- | --- |
| 机型、制造商、序列号 | `Win32_ComputerSystemProduct` |
| BIOS | `Win32_BIOS`，版本、厂商、发布日期 |
| CPU | `Win32_Processor`，型号、核心/线程、WMI 标称最高频率 |
| 显卡 | `Win32_VideoController`，型号/驱动/当前输出；独立显存由 NVML 提供 |
| 内存 | `Win32_PhysicalMemory`，逐模块插槽、型号、容量、类型、配置速率 |
| 磁盘 | `Win32_DiskDrive`，逐物理磁盘型号/容量/接口/介质 |
| 屏幕 | WMI 当前输出及 `WmiMonitorBasicDisplayParams` 的 EDID 物理尺寸 |
| 系统 | `Win32_OperatingSystem` |
| 官方服务 | 默认浏览器打开手册、售后政策、网点、服务中心、B21 驱动页 |
| 用户中心 | 小米账号官网登录；软件不保存小米凭据，不伪造登录或保修信息 |
| 软件反馈 | 打开项目 GitHub 反馈页，由用户填写并提交 |
| 诊断摘要 | 复制软件版本、组件状态与参数报告，不自动发送 |

参数在后台读取并缓存两分钟，刷新按钮可强制重读。单项 WMI 不可用时显示无法读取。
CPU 频率不是实时睿频；磁盘的 WMI 接口名称不能据此判定 NVMe 代数；未提供的 EDID 尺寸不推算。

设备序列号默认隐藏，离开页面后重新隐藏；参数报告和诊断摘要均排除设备序列号。
“用户中心”不等于原厂应用内部账号会话，订单、保修和客服结果由官网提供。

## 复现与验收

```powershell
dotnet run --project tests/GProControl.DetailsSmokeTests -c Release
dotnet run --project tests/GProControl.DetailsSmokeTests -c Release -- --hardware
GProControl.exe --package-smoke --support-details-smoke
```

本机读取 15 个参数分组，缓存与报告序列号排除检查通过。真实 WinUI 检查：
`sections=15; links=6; serialHidden=True; rootBackHidden=True; navigation=True`。
链接验证检查地址结构与域名，未自动提交客服消息或反馈。
