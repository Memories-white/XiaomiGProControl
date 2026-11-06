#Requires -Version 5.1
<#
    灯效 HID 抓帧助手。

    用法：先打开原厂「小米 G 控制中心」并停在灯效页，再运行：

        powershell -ExecutionPolicy Bypass -File .\capture-light-frames.ps1 -Label "亮度 2->1"

    脚本会记录当前日志位置，提示你去做那一步改动，按回车后只打印这一步新增的
    [HID] 报文，并把同命令的相邻帧做逐字节对比。把输出整段贴回给上游即可。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Label,
    [string]$LogPath = 'C:\ProgramData\MI\Xiaomi G Command Center\log\XGCC_log.txt',
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $LogPath)) { throw "找不到原厂日志：$LogPath" }

$transcriptStarted = $false
if ($OutputPath) {
    $outputDirectory = Split-Path -Parent $OutputPath
    if ($outputDirectory -and -not (Test-Path $outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
    Start-Transcript -Path $OutputPath -Force | Out-Null
    $transcriptStarted = $true
}

function Stop-CaptureTranscript {
    if ($script:transcriptStarted) {
        Stop-Transcript | Out-Null
        $script:transcriptStarted = $false
    }
}

function Read-From([long]$offset) {
    $share = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
    $stream = [System.IO.File]::Open($LogPath, 'Open', 'Read', $share)
    try {
        if ($offset -gt $stream.Length) { return $null }
        $stream.Seek($offset, 'Begin') | Out-Null
        $reader = New-Object System.IO.StreamReader($stream)
        return $reader.ReadToEnd()
    }
    finally {
        $stream.Dispose()
    }
}

$start = (Get-Item $LogPath).Length
Write-Host ''
Write-Host "=== 对照项：$Label ===" -ForegroundColor Cyan
Write-Host '请在原厂控制中心里只改这一项，改完（确保点了「应用」）再回到这里。'
Read-Host '按回车继续'

$text = Read-From $start
if ($null -eq $text) {
    Write-Warning '日志被重写过（长度变短）。请在原厂控制中心保持在灯效页，重新运行本脚本再做这一步。'
    Stop-CaptureTranscript
    exit 1
}

$pattern = '(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d,\d+) .*?\[HID\] (?:CMDTYPE (\d+) data: ([0-9A-Fa-f]+)|KeyBoard1ByteCmd return data: ([0-9A-Fa-f]+))'
$frames = @()
foreach ($line in ($text -split "`n")) {
    $m = [regex]::Match($line, $pattern)
    if (-not $m.Success) { continue }
    if ($m.Groups[2].Success) {
        $frames += [pscustomobject]@{
            Time = $m.Groups[1].Value; Cmd = [int]$m.Groups[2].Value
            Hex = $m.Groups[3].Value.ToUpperInvariant(); Direction = 'send'
        }
    }
    else {
        $frames += [pscustomobject]@{
            Time = $m.Groups[1].Value; Cmd = -1
            Hex = $m.Groups[4].Value.ToUpperInvariant(); Direction = 'reply'
        }
    }
}

if ($frames.Count -eq 0) {
    Write-Host ''
    Write-Host '这一步没有产生任何 HID 报文。' -ForegroundColor Yellow
    Write-Host '可能原因：没有点「应用」；或者该设置只写注册表、不发 HID。请重做一次。'
    Stop-CaptureTranscript
    exit 0
}

Write-Host ''
Write-Host ("新增 HID 报文 {0} 条（心跳 CMD 0 已保留，便于判断时序）" -f $frames.Count) -ForegroundColor Green

$lastByCmd = @{}
foreach ($frame in $frames) {
    $bytes = [byte[]]::new($frame.Hex.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $bytes[$i] = [Convert]::ToByte($frame.Hex.Substring($i * 2, 2), 16)
    }

    $tag = if ($frame.Cmd -ge 0) { "CMD $($frame.Cmd)" } else { 'REPLY' }
    Write-Host ''
    Write-Host ("{0}  {1}  len={2}" -f $frame.Time, $tag, $bytes.Length)
    Write-Host ("  {0}" -f $frame.Hex)

    $key = "$tag"
    if ($lastByCmd.ContainsKey($key)) {
        $previous = $lastByCmd[$key]
        if ($previous.Length -eq $bytes.Length) {
            $diff = @()
            for ($i = 0; $i -lt $bytes.Length; $i++) {
                if ($previous[$i] -ne $bytes[$i]) {
                    $diff += ("[{0}] {1:X2}->{2:X2}" -f $i, $previous[$i], $bytes[$i])
                }
            }
            if ($diff.Count -gt 0) {
                Write-Host ("  与上一条同命令帧的差异: {0}" -f ($diff -join '  ')) -ForegroundColor Yellow
            }
            else {
                Write-Host '  与上一条同命令帧完全相同' -ForegroundColor DarkGray
            }
        }
    }
    $lastByCmd[$key] = $bytes
}

Write-Host ''
Write-Host '把以上整段输出贴回给上游即可。' -ForegroundColor Cyan
Stop-CaptureTranscript
