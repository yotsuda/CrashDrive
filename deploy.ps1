#Requires -Version 7.4
<#
.SYNOPSIS
    Build and deploy the CrashDrive module to the local PowerShell Modules directory.
    Builds both the main provider DLL and the startup-hook tracer DLL.
#>
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$ModulePath = "$env:ProgramFiles\PowerShell\7\Modules\CrashDrive"
)

$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$srcProject    = Join-Path $projectDir 'src\CrashDrive\CrashDrive.csproj'
$tracerProject = Join-Path $projectDir 'src\CrashDrive.Tracer.Startup\CrashDrive.Tracer.Startup.csproj'
$moduleSource = Join-Path $projectDir 'module'

Write-Host "Building CrashDrive ($Configuration)..." -ForegroundColor Cyan
dotnet build $srcProject -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Main build failed.' }

Write-Host "Building CrashDrive.Tracer.Startup ($Configuration)..." -ForegroundColor Cyan
dotnet build $tracerProject -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Tracer build failed.' }

if (Test-Path $ModulePath) {
    Remove-Item "$ModulePath\*" -Recurse -Force
} else {
    New-Item -Path $ModulePath -ItemType Directory -Force | Out-Null
}

Copy-Item (Join-Path $moduleSource 'CrashDrive.psd1')          $ModulePath
Copy-Item (Join-Path $moduleSource 'CrashDrive.Format.ps1xml') $ModulePath

$mainOutput   = Join-Path $projectDir "src\CrashDrive\bin\$Configuration\net8.0"
$tracerOutput = Join-Path $projectDir "src\CrashDrive.Tracer.Startup\bin\$Configuration\net6.0"

Copy-Item (Join-Path $mainOutput 'CrashDrive.dll') $ModulePath
# PDB too: managed-frame source resolution needs it next to the DLL when
# the compile-time path is gone (self-dump from another machine, etc.).
$pdb = Join-Path $mainOutput 'CrashDrive.pdb'
if (Test-Path $pdb) { Copy-Item $pdb $ModulePath }

# Dependencies for the main DLL.
@(
    'Microsoft.Diagnostics.Runtime.dll',
    'Microsoft.Diagnostics.NETCore.Client.dll'
) | ForEach-Object {
    $src = Join-Path $mainOutput $_
    if (Test-Path $src) { Copy-Item $src $ModulePath }
}

# Tracer DLL + its Harmony dep. Must live next to CrashDrive.dll because
# NewCrashDriveCmdlet locates the tracer via typeof(this).Assembly.Location.
Copy-Item (Join-Path $tracerOutput 'CrashDrive.Tracer.Startup.dll') $ModulePath
$tracerPdb = Join-Path $tracerOutput 'CrashDrive.Tracer.Startup.pdb'
if (Test-Path $tracerPdb) { Copy-Item $tracerPdb $ModulePath }
Copy-Item (Join-Path $tracerOutput '0Harmony.dll') $ModulePath
# System.Text.Json: the tracer's net6.0 build ships it. On net6.0+ targets
# the runtime provides System.Text.Json so we don't strictly need it, but
# copying avoids a potential TypeLoad when the target apps pins an older
# version in its deps.json. Small (~600 KB), worth the safety margin.
$stjDll = Join-Path $tracerOutput 'System.Text.Json.dll'
if (Test-Path $stjDll) { Copy-Item $stjDll $ModulePath }

Write-Host "Deployed to $ModulePath" -ForegroundColor Green
Get-ChildItem $ModulePath | Format-Table Name, Length -AutoSize
