#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Installer)
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SetupManifestReader {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll", EntryPoint="FindResourceW", SetLastError=true)]
    public static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")] public static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] public static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")] public static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] public static extern bool FreeLibrary(IntPtr module);
}
'@
$path = (Resolve-Path -LiteralPath $Installer).Path
$module = [SetupManifestReader]::LoadLibraryEx($path, [IntPtr]::Zero, 2)
if ($module -eq [IntPtr]::Zero) { throw 'Unable to read setup resources.' }
try {
    $resource = [SetupManifestReader]::FindResource($module, [IntPtr]1, [IntPtr]24)
    if ($resource -eq [IntPtr]::Zero) { throw 'Setup has no embedded manifest.' }
    $size = [SetupManifestReader]::SizeofResource($module, $resource)
    $data = [SetupManifestReader]::LockResource([SetupManifestReader]::LoadResource($module, $resource))
    $bytes = New-Object byte[] $size
    [Runtime.InteropServices.Marshal]::Copy($data, $bytes, 0, $size)
    [xml]$manifest = [Text.Encoding]::UTF8.GetString($bytes).Trim([char]0)
    $level = $manifest.SelectSingleNode('//*[local-name()="requestedExecutionLevel"]').GetAttribute('level')
    if ($level -ne 'requireAdministrator') { throw "Setup elevates too late: manifest level=$level" }
    $payload = [SetupManifestReader]::FindResource($module, [IntPtr]101, [IntPtr]10)
    if ($payload -eq [IntPtr]::Zero) { throw 'Embedded installer payload is missing.' }
    Write-Output "Setup entry manifest verified: $level; embedded payload present."
} finally { [void][SetupManifestReader]::FreeLibrary($module) }
