#Requires -Version 5.1
<#
    G Pro Control 安装脚本（免管理员）。

    把 app\ 目录复制到 %LOCALAPPDATA%\Programs\GProControl，创建开始菜单快捷方式，
    并在 HKCU 下登记卸载信息。不写入系统目录，不需要提升权限。
#>
[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\GProControl'),
    [switch]$NoDesktopShortcut
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'app'
$executable = Join-Path $source 'GProControl.exe'
if (-not (Test-Path $executable)) {
    throw "找不到 app\GProControl.exe，请在解压后的目录里运行本脚本。"
}

Write-Host "安装到: $InstallDirectory"
if (Test-Path $InstallDirectory) {
    Write-Host '检测到旧版本，正在覆盖文件…'
    Get-ChildItem $InstallDirectory -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
}

Copy-Item -Path (Join-Path $source '*') -Destination $InstallDirectory -Recurse -Force

$target = Join-Path $InstallDirectory 'GProControl.exe'

# 程序文件已经就位。快捷方式和卸载登记失败不应该让整个安装失败。
try {
    $shell = New-Object -ComObject WScript.Shell
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\G Pro Control.lnk'
    $shortcut = $shell.CreateShortcut($startMenu)
    $shortcut.TargetPath = $target
    $shortcut.WorkingDirectory = $InstallDirectory
    $shortcut.IconLocation = "$target,0"
    $shortcut.Description = 'G Pro Control'
    $shortcut.Save()
    Write-Host "开始菜单快捷方式: $startMenu"

    if (-not $NoDesktopShortcut) {
        $desktopLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'G Pro Control.lnk'
        $shortcut = $shell.CreateShortcut($desktopLink)
        $shortcut.TargetPath = $target
        $shortcut.WorkingDirectory = $InstallDirectory
        $shortcut.IconLocation = "$target,0"
        $shortcut.Description = 'G Pro Control'
        $shortcut.Save()
        Write-Host "桌面快捷方式: $desktopLink"
    }
}
catch {
    Write-Warning "快捷方式创建失败（程序已安装，可直接运行 $target）：$($_.Exception.Message)"
}

try {
    $uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GProControl'
    New-Item -Path $uninstallKey -Force | Out-Null
    $uninstallCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $PSScriptRoot 'Uninstall-GProControl.ps1')`" -InstallDirectory `"$InstallDirectory`""
    Set-ItemProperty -Path $uninstallKey -Name 'DisplayName' -Value 'G Pro Control'
    Set-ItemProperty -Path $uninstallKey -Name 'DisplayIcon' -Value $target
    Set-ItemProperty -Path $uninstallKey -Name 'InstallLocation' -Value $InstallDirectory
    Set-ItemProperty -Path $uninstallKey -Name 'Publisher' -Value 'G Pro Control'
    Set-ItemProperty -Path $uninstallKey -Name 'UninstallString' -Value $uninstallCommand
    Set-ItemProperty -Path $uninstallKey -Name 'NoModify' -Type DWord -Value 1
    Set-ItemProperty -Path $uninstallKey -Name 'NoRepair' -Type DWord -Value 1
}
catch {
    Write-Warning "卸载登记写入失败（不影响运行）：$($_.Exception.Message)"
}

Write-Host ''
Write-Host "安装完成：$target" -ForegroundColor Green
Write-Host '提示：原厂服务管道可能要求管理员权限；若设备控制无法连接，请右键“以管理员身份运行”。'
