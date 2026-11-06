# G Pro Control 0.10.0 安装包

正式入口是 `artifacts/latest/GProControl-0.10.0-Setup-x64.exe`。这是中文/英文向导 EXE，
默认安装到 `C:\Program Files\G Pro Control`，支持覆盖安装、快捷方式和卸载。
包含 x64 .NET 与 Windows App SDK 运行库，不需要另行安装 .NET。
发布者元数据使用 `Memories_white`；安装包没有 Authenticode 签名。

## 安装目录

```text
G Pro Control/
  GProControl.exe           原生启动器，带图标和管理员清单
  Libraries/
    Application/           程序宿主和宿主配置
    Managed/               托管 DLL
    Native/                原生 WinUI/Windows App SDK DLL
    DotNet/                CLR 核心与原生运行时组件
  Languages/               全部 86 个语言目录，共 172 个资源文件
  Resources/               XAML、PRI、图标、WinRT 元数据等
  Runtime/                 文件映射和目录说明
  Uninstall/               卸载程序及卸载信息
```

DLL 是否托管通过 PE 的 CLR 元数据头判断，不依赖文件名猜测。分类映射记录在
`Runtime/layout.tsv`，每个文件有原始路径、安装路径与 SHA-256。

## 为什么还有运行缓存

WinUI 原生语言 MUI、PRI 和运行库依赖原始相对路径；直接移动这些文件会破坏资源加载。
启动器因此在 `%ProgramData%\GProControl\Runtime\<清单 SHA-256>` 生成兼容运行目录。
安装器预先准备缓存，每次启动校验文件完整性，损坏的缓存从分类源文件重新生成。
缓存只有管理员/System 可写；普通用户只能读取/执行。

安装目录保持分类；缓存目录保留 WinUI 要求的运行布局。首次准备会额外占用一份运行库空间。
启动快捷方式与登录计划任务始终指向安装根目录启动器。不要直接运行
`Libraries/Application` 内部程序。

卸载只清理本安装对应的缓存，并且仅当登录任务指向本安装启动器时才删除它。
用户设置 `%LocalAppData%\Memories_white\GProControl\settings.json` 保留。

## 安装入口与权限

0.11.1 增加带 `requireAdministrator` 清单的原生安装入口，Windows 在运行入口代码前
先处理 UAC 提升，再将内嵌的 Inno 安装器写到 ProgramData 下随机命名的临时目录。
目录仅 Administrators/System 可访问，安装结束删除内嵌程序和临时目录，并传回安装退出码。

原因是 Inno 的外层 SetupLdr 使用 `asInvoker`：先创建用户临时目录，再由内部 Setup
根据 `PrivilegesRequired=admin` 提升。0.11.0 在普通权限无法写入 Temp 的环境会提前报错。
参考 [Inno 官方安全说明](https://jrsoftware.org/is6help/topic_securitymeasures.htm)。

`scripts/test-setup-elevation.ps1` 直接读取最终安装包的 PE 资源，确认最外层入口要求管理员
权限，且内嵌安装器存在；这项检查也包含在完整安装验收中。

## 构建

需要 .NET 10 SDK、MSVC x64 工具链和项目内 `.tools/InnoSetup/ISCC.exe`。
编译器为官方 Inno Setup 6.7.4，签名已验证，简体中文翻译使用同版本官方源码资源。

```powershell
pwsh -File scripts/build-installer.ps1
```

脚本每次使用新的 staging 目录，不整理/移动原始调试输出。
旧 `scripts/package-release.ps1` 和 ZIP/PowerShell 安装方式是历史发布流程。

## 验收

`scripts/test-installer.ps1` 需管理员权限，安装到项目 `.tmp-tests` 中的独立随机目录，
然后执行覆盖重装、527 文件哈希检查、窗口启动、托盘后台隐藏与恢复、卸载与设置保留检查。
`--package-smoke` 验证模式跳过注册开机任务和硬件设置恢复。

2026-11-06，0.11.0 实测通过：

- `InstallExitCode=0`，`ReinstallExitCode=0`，`UninstallExitCode=0`。
- 86 个语言目录均在 `Languages`，根目录无 DLL。
- 527 个安装文件与运行缓存文件 SHA-256 一致。
- `WindowTitle=G Pro Control`，`WindowResponding=True`。
- `BackgroundLifecyclePassed=True`：托盘图标注册后隐藏，再恢复窗口成功。
- `UserSettingsPreserved=True`，`ExistingStartupTaskPreserved=True`。
- 临时安装与本次缓存已卸载删除，用户设置未删除。

详细结果见 `analysis-runtime/installer-validation.json`。
