#Requires -Version 5.1
<#
    G Pro Control 卸载脚本。删除程序目录、开始菜单/桌面快捷方式和 HKCU 卸载登记。
    不需要管理员权限。
#>
[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\GProControl')
)

$ErrorActionPreference = 'Stop'

$processes = Get-Process -Name 'GProControl' -ErrorAction SilentlyContinue
if ($processes) {
    Write-Host '正在关闭 G Pro Control…'
    $processes | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

if (Test-Path $InstallDirectory) {
    Remove-Item -Path $InstallDirectory -Recurse -Force
    Write-Host "已删除: $InstallDirectory"
}

foreach ($link in @(
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\G Pro Control.lnk'),
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'G Pro Control.lnk'))) {
    if (Test-Path $link) {
        Remove-Item $link -Force
        Write-Host "已删除快捷方式: $link"
    }
}

$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GProControl'
if (Test-Path $uninstallKey) {
    Remove-Item $uninstallKey -Recurse -Force
    Write-Host '已移除卸载登记。'
}

Write-Host ''
Write-Host '卸载完成。' -ForegroundColor Green
