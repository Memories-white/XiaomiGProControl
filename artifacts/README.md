# 构建产物目录

- `latest/`：当前 0.11.1 EXE 安装包与构建报告（含 SHA-256）。
- `staging/Installer-0.11.1-20261106-191630/`：0.11.1 解压前提权修复，已完成安装验收。
- `staging/Installer-0.11.0-20261106-152139/`：0.11.0 已验收的完整程序与分类安装目录。
- `staging/Installer-0.10.0-20261106-145001/`：最终成功发布的完整程序与分类安装目录。
- `archive/0.2.0/`：旧版压缩包。
- `archive/build-intermediates/`：0.3.0 调试构建的中间输出。
- `archive/legacy-unpacked-output/`：早期错误输出路径产生的散落运行时文件，保留用于回溯。

当前安装包：`latest/GProControl-0.11.1-Setup-x64.exe`。

2026-11-06 已将 0.7–0.9 旧安装包、旧打包暂存及 LightingPreview/ModePreview
构建输出移入回收站。历史归档、逆向分析证据、Debug/Release 输出和最终安装包保留。
清理记录：`analysis-runtime/cleanup-result-20261106.json`。
