#Requires -Version 5.1
<#
    构建 G Pro Control 并生成“安装包”：一个 zip，解压后运行 Install-GProControl.ps1。

    app\ 目录会被精简（去掉非 x64 运行时和多余的语言资源），使安装后的目录保持整洁。
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'GProControl\GProControl.csproj'
$framework = 'net10.0-windows10.0.19041.0'
$output = Join-Path $root "GProControl\bin\x64\Release\$framework"

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw '无法确定版本号。' }

Write-Host "版本: $Version"

if (-not $SkipBuild) {
    Write-Host '构建 Release x64…'
    & dotnet build $project -c Release -p:Platform=x64 --no-restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
}

if (-not (Test-Path (Join-Path $output 'GProControl.exe'))) {
    throw "找不到构建输出: $output"
}

$stage = Join-Path $root "artifacts\staging\GProControl-$Version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$app = Join-Path $stage 'app'
New-Item -ItemType Directory -Path $app -Force | Out-Null

Write-Host '复制程序文件…'
Copy-Item -Path (Join-Path $output '*') -Destination $app -Recurse -Force

# 只保留 x64 运行时，避免把 arm64/arm64ec/x86 的整套副本塞进安装目录。
$runtimes = Join-Path $app 'runtimes'
if (Test-Path $runtimes) {
    Get-ChildItem $runtimes -Directory |
        Where-Object { $_.Name -ne 'win-x64' } |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force }
}

# 只保留中英文语言资源；WinUI 会按系统语言回退到 en-US。
# 语言目录形如 zh-CN、az-Latn-AZ、sr-Cyrl-RS、en-us；-notcontains 默认不区分大小写。
$keepLocales = @('zh-CN', 'zh-TW', 'en-US')
$localePattern = '^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,}){1,3}$'
$removedLocales = 0
Get-ChildItem $app -Directory |
    Where-Object { $_.Name -match $localePattern -and $keepLocales -notcontains $_.Name } |
    ForEach-Object {
        Remove-Item $_.FullName -Recurse -Force
        $removedLocales++
    }

Get-ChildItem $app -Recurse -File -Include *.pdb | Remove-Item -Force -ErrorAction SilentlyContinue

Copy-Item -Path (Join-Path $root 'packaging\Install-GProControl.ps1') -Destination $stage -Force
Copy-Item -Path (Join-Path $root 'packaging\Uninstall-GProControl.ps1') -Destination $stage -Force
Copy-Item -Path (Join-Path $root 'packaging\安装说明.txt') -Destination $stage -Force

$latest = Join-Path $root 'artifacts\latest'
New-Item -ItemType Directory -Path $latest -Force | Out-Null
$zip = Join-Path $latest "GProControl-$Version-Setup.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

Write-Host '打包…'
& tar.exe -a -c -f $zip -C $stage .
if ($LASTEXITCODE -ne 0) { throw '打包失败。' }

$appSize = (Get-ChildItem $app -Recurse -File | Measure-Object Length -Sum).Sum
$appFiles = (Get-ChildItem $app -Recurse -File | Measure-Object).Count
$report = [pscustomobject]@{
    Version          = $Version
    Zip              = $zip
    ZipMB            = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    AppMB            = [math]::Round($appSize / 1MB, 1)
    AppFiles         = $appFiles
    RemovedLocales   = $removedLocales
    KeptLocales      = ($keepLocales -join ', ')
}
$report | Format-List
