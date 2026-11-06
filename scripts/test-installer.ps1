#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param([string]$Installer)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content -LiteralPath (Join-Path $root 'GProControl\GProControl.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (!$Installer) { $Installer = Join-Path $root "artifacts\latest\GProControl-$version-Setup-x64.exe" }
& (Join-Path $PSScriptRoot 'test-setup-elevation.ps1') -Installer $Installer
$testRoot = Join-Path $root ('.tmp-tests\install-' + [Guid]::NewGuid().ToString('N'))
$resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
$allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $root '.tmp-tests')) + '\'
if (!$resolvedTestRoot.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test install path.' }
$reportPath = Join-Path $root 'analysis-runtime\installer-validation.json'
$log = Join-Path $root 'analysis-runtime\installer-test.log'
$settings = Join-Path $env:LOCALAPPDATA 'Memories_white\GProControl\settings.json'
$settingsHash = if (Test-Path -LiteralPath $settings) { (Get-FileHash -LiteralPath $settings).Hash } else { 'absent' }
$schedule = New-Object -ComObject Schedule.Service
$schedule.Connect()
$taskFolder = $schedule.GetFolder('\')
$taskBefore = try { $taskFolder.GetTask('GProControl Startup').Xml } catch { 'absent' }
$report = [ordered]@{Installer=$Installer; TestDirectory=$resolvedTestRoot; Passed=$false}
$launcher = $null
$child = $null
$cache = $null
$uninstallKeyName = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{C185DBE4-018F-45B8-9464-5474FCA1C537}_is1'
$registryRoot = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
$previousRegistration = $registryRoot.OpenSubKey($uninstallKeyName)
$registrationBackup = @()
if ($previousRegistration) {
    $registrationBackup = @($previousRegistration.GetValueNames() | Sort-Object | ForEach-Object {
        [pscustomobject]@{Name=$_; Value=$previousRegistration.GetValue($_); Kind=$previousRegistration.GetValueKind($_)}
    })
    $previousRegistration.Dispose()
}
try {
    if (Test-Path -LiteralPath $reportPath) {
        $previousReport = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        if (!$previousReport.Passed -and $previousReport.TestDirectory) {
            $previousTest = [IO.Path]::GetFullPath($previousReport.TestDirectory)
            if ($previousTest.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
                $previousUninstaller = Join-Path $previousTest 'Uninstall\unins000.exe'
                if (Test-Path -LiteralPath $previousUninstaller) {
                    $cleanup = Start-Process -FilePath $previousUninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -Wait -PassThru
                    if ($cleanup.ExitCode -ne 0) { throw 'Unable to clean previous failed test installation.' }
                }
            }
        }
    }
    $setup = Start-Process -FilePath $Installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',
        '/TASKS=',('/DIR="'+$resolvedTestRoot+'"'),('/LOG="'+$log+'"')) -WindowStyle Hidden -Wait -PassThru
    if ($setup.ExitCode -ne 0) { throw "Setup exited with code $($setup.ExitCode)" }
    $report.InstallExitCode = $setup.ExitCode
    $retired = Join-Path $resolvedTestRoot 'Libraries\Managed\Retired-Upgrade-Test.dll'
    [IO.File]::WriteAllText($retired, 'retired managed-file sentinel')
    Add-Content -LiteralPath (Join-Path $resolvedTestRoot 'Runtime\installed-files.txt') -Value 'Libraries\Managed\Retired-Upgrade-Test.dll'
    $extra = Join-Path $resolvedTestRoot 'user-notes.txt'
    [IO.File]::WriteAllText($extra, 'preserve unowned files')
    $beforeLayout = Join-Path $resolvedTestRoot 'Runtime\layout.tsv'
    $beforeCache = Join-Path $env:ProgramData ('GProControl\Runtime\' + (Get-FileHash $beforeLayout).Hash.ToLowerInvariant())
    $runningUpgrade = Start-Process -FilePath (Join-Path $resolvedTestRoot 'GProControl.exe') -ArgumentList '--package-smoke' -PassThru
    Start-Sleep -Seconds 3
    if ($runningUpgrade.HasExited) { throw 'The old instance must actually be running before upgrade.' }
    $upgrade = Start-Process -FilePath $Installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',
        '/TASKS=') -WindowStyle Hidden -Wait -PassThru
    if ($upgrade.ExitCode -ne 0) { throw 'In-place reinstall/upgrade validation failed.' }
    $report.ReinstallExitCode = $upgrade.ExitCode
    $report.RunningInstanceClosed = $runningUpgrade.WaitForExit(15000)
    if (!$report.RunningInstanceClosed) { throw 'Upgrade did not close the old installed instance.' }
    $entry = $registryRoot.OpenSubKey($uninstallKeyName)
    $report.PreviousDirectoryReused = $entry.GetValue('InstallLocation').TrimEnd('\') -eq $resolvedTestRoot.TrimEnd('\')
    $entry.Dispose()
    $report.RetiredFilesRemoved = !(Test-Path -LiteralPath $retired)
    $report.UnownedFilesPreserved = (Get-Content -LiteralPath $extra -Raw) -eq 'preserve unowned files'
    if (!$report.PreviousDirectoryReused -or !$report.RetiredFilesRemoved -or !$report.UnownedFilesPreserved) { throw 'Upgrade migration validation failed.' }
    $legacyFile = Join-Path $resolvedTestRoot 'Libraries\Managed\Retired-Legacy-Test.dll'
    [IO.File]::WriteAllText($legacyFile, 'retired legacy sentinel')
    $legacyHash = (Get-FileHash -LiteralPath $legacyFile).Hash.ToLowerInvariant()
    Add-Content -LiteralPath $beforeLayout -Value ("Retired-Legacy-Test.dll`tLibraries\Managed\Retired-Legacy-Test.dll`t" + $legacyHash)
    $legacyCache = Join-Path $env:ProgramData ('GProControl\Runtime\' + (Get-FileHash $beforeLayout).Hash.ToLowerInvariant())
    $prepareLegacy = Start-Process -FilePath (Join-Path $resolvedTestRoot 'GProControl.exe') -ArgumentList '--prepare-runtime' -Wait -PassThru
    if ($prepareLegacy.ExitCode -ne 0) { throw 'Unable to prepare legacy migration fixture.' }
    Remove-Item -LiteralPath (Join-Path $resolvedTestRoot 'Runtime\installed-files.txt')
    $legacyUpgrade = Start-Process -FilePath $Installer -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/TASKS=') -WindowStyle Hidden -Wait -PassThru
    $report.LegacyLayoutMigrated = $legacyUpgrade.ExitCode -eq 0 -and !(Test-Path $legacyFile) -and !(Test-Path $legacyCache)
    if (!$report.LegacyLayoutMigrated) { throw 'Legacy layout inventory migration failed.' }
    $report.RootEntries = @(Get-ChildItem -LiteralPath $resolvedTestRoot | Select-Object -ExpandProperty Name)
    if (@(Get-ChildItem -LiteralPath $resolvedTestRoot -File -Filter '*.dll').Count) { throw 'DLLs leaked into the install root.' }
    $report.LanguageFolders = @(Get-ChildItem -LiteralPath (Join-Path $resolvedTestRoot 'Languages') -Directory).Count
    if ($report.LanguageFolders -lt 3) { throw 'Language resources were not grouped.' }
    $layout = Join-Path $resolvedTestRoot 'Runtime\layout.tsv'
    $identity = (Get-FileHash -LiteralPath $layout).Hash.ToLowerInvariant()
    $cache = Join-Path $env:ProgramData ('GProControl\Runtime\' + $identity)
    if (!(Test-Path -LiteralPath (Join-Path $cache 'GProControl.exe'))) { throw 'Setup did not prepare the runtime cache.' }
    $report.RuntimeCache = $cache
    $report.CacheAcl = (Get-Acl -LiteralPath $cache).Sddl
    $records = @(Get-Content -LiteralPath $layout -Encoding UTF8 | Where-Object { $_ -and !$_.StartsWith('#') })
    foreach ($record in $records) {
        $fields = $record.Split("`t")
        $source = Join-Path $resolvedTestRoot $fields[1]
        $cachedFile = Join-Path $cache $fields[0]
        if ((Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant() -ne $fields[2]) { throw "Installed file mismatch: $source" }
        if ((Get-FileHash -LiteralPath $cachedFile).Hash.ToLowerInvariant() -ne $fields[2]) { throw "Runtime cache mismatch: $cachedFile" }
    }
    $report.VerifiedFiles = $records.Count
    $smokeStarted = Get-Date
    $launcher = Start-Process -FilePath (Join-Path $resolvedTestRoot 'GProControl.exe') -ArgumentList '--package-smoke' -PassThru
    Start-Sleep -Seconds 3
    $deadline = (Get-Date).AddSeconds(30)
    do {
        $candidate = Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq (Join-Path $cache 'GProControl.exe') } | Select-Object -First 1
        if ($candidate) {
            $child = Get-Process -Id $candidate.ProcessId -ErrorAction SilentlyContinue
            if ($child -and $child.MainWindowTitle -eq 'G Pro Control' -and $child.Responding) { break }
        }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    if (!$child -or $child.MainWindowTitle -ne 'G Pro Control' -or !$child.Responding) { throw 'Installed application did not expose a responsive window.' }
    $report.WindowTitle = $child.MainWindowTitle
    $report.WindowResponding = $child.Responding
    $report.ChildPid = $child.Id
    $report.LauncherPid = $launcher.Id
    $startupLog = Join-Path $env:LOCALAPPDATA 'GProControl\Logs\startup.log'
    $lifecycle = Get-Content -LiteralPath $startupLog -Tail 30 | Where-Object {
        $_ -match 'Background lifecycle smoke: hidden=True; restored=True' -and
        [DateTimeOffset]::Parse($_.Split('|')[0].Trim()).LocalDateTime -ge $smokeStarted.AddSeconds(-1)
    }
    $report.BackgroundLifecyclePassed = $null -ne $lifecycle
    if (!$report.BackgroundLifecyclePassed) { throw 'Notification icon / background hide-and-restore validation failed.' }
    $null = $child.CloseMainWindow()
    if (!$child.WaitForExit(7000)) { Stop-Process -Id $child.Id -Force }
    $child = $null
    if (!$launcher.WaitForExit(7000)) { Stop-Process -Id $launcher.Id -Force }
    $launcher = $null
    $uninstaller = Join-Path $resolvedTestRoot 'Uninstall\unins000.exe'
    $uninstallLog = Join-Path $root 'analysis-runtime\installer-uninstall-test.log'
    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="'+$uninstallLog+'"')) -WindowStyle Hidden -Wait -PassThru
    $report.UninstallExitCode = $uninstall.ExitCode
    if ($uninstall.ExitCode -ne 0) { throw 'Test uninstall failed.' }
    $deadline = (Get-Date).AddSeconds(10)
    while ((Test-Path -LiteralPath (Join-Path $resolvedTestRoot 'GProControl.exe')) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (Test-Path -LiteralPath (Join-Path $resolvedTestRoot 'GProControl.exe')) { throw 'Uninstaller left the launcher behind.' }
    if (Test-Path -LiteralPath $cache) { throw 'Uninstaller left this installation runtime cache behind.' }
    $settingsAfter = if (Test-Path -LiteralPath $settings) { (Get-FileHash -LiteralPath $settings).Hash } else { 'absent' }
    $report.UserSettingsPreserved = $settingsAfter -eq $settingsHash
    $taskAfter = try { $taskFolder.GetTask('GProControl Startup').Xml } catch { 'absent' }
    $report.ExistingStartupTaskPreserved = $taskBefore -eq $taskAfter
    if (!$report.UserSettingsPreserved -or !$report.ExistingStartupTaskPreserved) { throw 'Validation changed user settings or an unrelated startup registration.' }
    $report.Passed = $true
} catch {
    $report.Error = $_.Exception.Message
} finally {
    if ($child -and !$child.HasExited) { Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue }
    if ($launcher -and !$launcher.HasExited) { Stop-Process -Id $launcher.Id -Force -ErrorAction SilentlyContinue }
    if ($registrationBackup.Count) {
        $restoredKey = $registryRoot.CreateSubKey($uninstallKeyName)
        foreach ($name in $restoredKey.GetValueNames()) {
            if ($name -notin $registrationBackup.Name) { $restoredKey.DeleteValue($name) }
        }
        foreach ($value in $registrationBackup) { $restoredKey.SetValue($value.Name, $value.Value, $value.Kind) }
        $afterRegistration = @($restoredKey.GetValueNames() | Sort-Object | ForEach-Object {
            [pscustomobject]@{Name=$_; Value=$restoredKey.GetValue($_); Kind=$restoredKey.GetValueKind($_)}
        })
        $report.PriorRegistrationPreserved = ($afterRegistration | ConvertTo-Json -Depth 5 -Compress) -eq ($registrationBackup | ConvertTo-Json -Depth 5 -Compress)
        $restoredKey.Dispose()
    } else {
        $testKey = $registryRoot.OpenSubKey($uninstallKeyName)
        if ($testKey) {
            $testOwned = $testKey.GetValue('InstallLocation').TrimEnd('\') -eq $resolvedTestRoot.TrimEnd('\')
            $testKey.Dispose()
            if ($testOwned) { $registryRoot.DeleteSubKeyTree($uninstallKeyName, $false) }
        }
        $report.PriorRegistrationPreserved = $null -eq $registryRoot.OpenSubKey($uninstallKeyName)
    }
    if (!$report.PriorRegistrationPreserved) { $report.Passed = $false; $report.Error = 'Uninstall registration was not preserved.' }
    $registryRoot.Dispose()
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding UTF8
}
if (!$report.Passed) { throw "Installer validation failed: $($report.Error). See $reportPath" }
Write-Output "Installer validation passed: $reportPath"
