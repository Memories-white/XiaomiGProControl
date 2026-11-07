#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$InnoCompiler,
    [string]$PublishSource,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'GProControl\GProControl.csproj'
$version = ([xml](Get-Content -LiteralPath $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stage = Join-Path $root "artifacts\staging\Installer-$version-$stamp"
$raw = Join-Path $stage 'publish'
$app = Join-Path $stage 'classified'
$nativeOutput = Join-Path $stage 'launcher'
$latest = if ($OutputDirectory) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory) }
          else { Join-Path $root 'artifacts\latest' }
New-Item -ItemType Directory -Path $raw,$app,$nativeOutput,$latest -Force | Out-Null

if (!$PublishSource) {
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:PublishTrimmed=false -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $raw
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }
} else {
    Copy-Item -Path (Join-Path $PublishSource '*') -Destination $raw -Recurse
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$visualStudio) { throw 'MSVC x64 C++ tools are required to build the native launcher.' }
& $env:ComSpec /c (Join-Path $root 'packaging\launcher\build-launcher.cmd') $visualStudio $nativeOutput
if ($LASTEXITCODE -ne 0) { throw 'Native launcher build failed.' }
Copy-Item -LiteralPath (Join-Path $nativeOutput 'GProControl.exe') -Destination $app

$dotnetNative = @('coreclr.dll','clrjit.dll','clrgc.dll','clrgcexp.dll','hostfxr.dll','hostpolicy.dll',
    'mscordaccore.dll','mscordbi.dll','Microsoft.DiaSymReader.Native.amd64.dll',
    'System.IO.Compression.Native.dll','System.Globalization.Native.dll')
$manifest = [System.Collections.Generic.List[string]]::new()
$manifest.Add('# GProControl classified runtime layout v1')
$manifest.Add("# version=$version")
$manifest.Add('# original-relative-path<TAB>installed-relative-path<TAB>sha256')
$counts = @{}
Add-Type -AssemblyName System.Reflection.Metadata
foreach ($file in Get-ChildItem -LiteralPath $raw -File -Recurse | Sort-Object FullName) {
    $relative = [System.IO.Path]::GetRelativePath($raw, $file.FullName)
    if ($file.Extension -eq '.pdb') { continue }
    if ($relative -match '^runtimes\\([^\\]+)\\' -and $Matches[1] -notin @('win-x64','win')) { continue }
    $firstSegment = ($relative -split '[\\/]')[0]
    if ($firstSegment -match '^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,}){1,3}$') {
        $category = 'Languages'
    } elseif ($file.Extension -eq '.dll') {
        if ($file.Name -in $dotnetNative) {
            $category = 'Libraries\DotNet'
        } else {
            $stream = [System.IO.File]::OpenRead($file.FullName)
            $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
            try {
                $category = if ($null -ne $pe.PEHeaders.CorHeader) { 'Libraries\Managed' } else { 'Libraries\Native' }
            } finally { $pe.Dispose(); $stream.Dispose() }
        }
    } elseif ($file.Extension -eq '.exe' -or $file.Name -match '^GProControl\.(deps|runtimeconfig)\.json$') {
        $category = 'Libraries\Application'
    } else {
        $category = 'Resources'
    }
    $destinationRelative = Join-Path $category $relative
    $destination = Join-Path $app $destinationRelative
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
    $hash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest.Add("$relative`t$destinationRelative`t$hash")
    $counts[$category] = 1 + $counts[$category]
}

$runtime = Join-Path $app 'Runtime'
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
[System.IO.File]::WriteAllLines((Join-Path $runtime 'layout.tsv'), $manifest, [System.Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $root 'packaging\Directory-Layout.txt') -Destination (Join-Path $runtime 'Directory-Layout.txt')
$owned = @(Get-ChildItem -LiteralPath $app -File -Recurse | ForEach-Object { [IO.Path]::GetRelativePath($app, $_.FullName) })
$owned += 'Runtime\installed-files.txt'
[IO.File]::WriteAllLines((Join-Path $runtime 'installed-files.txt'), $owned, [Text.UTF8Encoding]::new($false))
if (!$InnoCompiler) { $InnoCompiler = Join-Path $root '.tools\InnoSetup\ISCC.exe' }
if (!(Test-Path -LiteralPath $InnoCompiler)) { throw "Inno Setup compiler missing: $InnoCompiler" }
$innoOutput = Join-Path $stage 'inno'
New-Item -ItemType Directory -Path $innoOutput -Force | Out-Null
& $InnoCompiler /Qp "/DAppVersion=$version" "/DSourceRoot=$app" "/DInstallerOutput=$innoOutput" (Join-Path $root 'packaging\GProControl.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$installer = Join-Path $latest "GProControl-$version-Setup-x64.exe"
$embeddedSetup = Join-Path $innoOutput "GProControl-$version-Setup-x64.exe"
$resourcePath = { param($value) $value.Replace('\','\\') }
$bootstrapResource = @"
#include <windows.h>
1 RT_MANIFEST "$( & $resourcePath (Join-Path $root 'GProControl\app.manifest'))"
1 ICON "$( & $resourcePath (Join-Path $root 'GProControl\Assets\GProControl.ico'))"
101 RCDATA "$( & $resourcePath $embeddedSetup)"
"@
$bootstrapResource += "`r`n" + ((Get-Content -LiteralPath (Join-Path $root 'packaging\launcher\Launcher.rc') | Select-Object -Skip 3) -join "`r`n").Replace('G Pro Control Launcher','G Pro Control Setup').Replace('GProControl.exe','GProControl-Setup.exe')
[IO.File]::WriteAllText((Join-Path $nativeOutput 'SetupBootstrap.rc'), $bootstrapResource, [Text.UTF8Encoding]::new($false))
$bootstrapOutput = Join-Path $nativeOutput 'GProControl-Setup.exe'
& $env:ComSpec /c (Join-Path $root 'packaging\launcher\build-setup.cmd') $visualStudio $nativeOutput $embeddedSetup $bootstrapOutput
if ($LASTEXITCODE -ne 0) { throw 'Elevated setup bootstrapper build failed.' }
& (Join-Path $PSScriptRoot 'test-setup-elevation.ps1') -Installer $bootstrapOutput
[IO.File]::Move($bootstrapOutput, $installer, $true)
$report = [pscustomobject]@{
    Version=$version
    Installer=$installer
    SizeMB=[Math]::Round((Get-Item -LiteralPath $installer).Length / 1MB, 2)
    SHA256=(Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    ClassifiedRoot=$app
    RawPublish=$raw
    Categories=$counts
}
$report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $latest "GProControl-$version-build-report.json") -Encoding utf8
$report | Format-List
