#Requires -Version 7.4
<#
.SYNOPSIS
    Build and deploy the CrashDrive module to the local PowerShell Modules directory.

    Layout:
        <module>\
            CrashDrive.psd1
            CrashDrive.Format.ps1xml
            CrashDrive.dll              (RootModule)
            CrashDrive.pdb              (managed-source-resolution fallback)
            NOTICES
            bin\
                0Harmony.dll
                CrashDrive.Tracer.Startup.dll
                CrashDrive.Tracer.Startup.pdb
                Microsoft.Diagnostics.NETCore.Client.dll
                Microsoft.Diagnostics.Runtime.dll
                System.Text.Json.dll
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
$binPath = Join-Path $ModulePath 'bin'
New-Item -Path $binPath -ItemType Directory -Force | Out-Null

# ── Module root: manifest, format data, main DLL + PDB, NOTICES ────────
Copy-Item (Join-Path $moduleSource 'CrashDrive.psd1')          $ModulePath
Copy-Item (Join-Path $moduleSource 'CrashDrive.Format.ps1xml') $ModulePath

$notices = Join-Path $projectDir 'NOTICES'
if (Test-Path $notices) { Copy-Item $notices $ModulePath }

$mainOutput = Join-Path $projectDir "src\CrashDrive\bin\$Configuration\net8.0"
Copy-Item (Join-Path $mainOutput 'CrashDrive.dll') $ModulePath
# CrashDrive.pdb stays next to CrashDrive.dll because managed-frame source
# resolution falls back to Path.ChangeExtension(module.Name, ".pdb") when
# the compile-time PDB path is gone (self-dump from another machine).
$mainPdb = Join-Path $mainOutput 'CrashDrive.pdb'
if (Test-Path $mainPdb) { Copy-Item $mainPdb $ModulePath }

# ── bin\ : third-party DLLs + tracer ───────────────────────────────────
# Main DLL's runtime dependencies. Pre-loaded via psd1 RequiredAssemblies.
@(
    'Microsoft.Diagnostics.Runtime.dll',
    'Microsoft.Diagnostics.NETCore.Client.dll'
) | ForEach-Object {
    $src = Join-Path $mainOutput $_
    if (Test-Path $src) { Copy-Item $src $binPath }
}

# Tracer DLL + its Harmony dep. Located at runtime via
# Path.Combine(moduleDir, "bin", "CrashDrive.Tracer.Startup.dll") in the
# New-CrashDrive cmdlet. StartupHook's AssemblyResolve handler then loads
# 0Harmony.dll from the same bin\ directory when the target process runs.
$tracerOutput = Join-Path $projectDir "src\CrashDrive.Tracer.Startup\bin\$Configuration\net6.0"
Copy-Item (Join-Path $tracerOutput 'CrashDrive.Tracer.Startup.dll') $binPath
$tracerPdb = Join-Path $tracerOutput 'CrashDrive.Tracer.Startup.pdb'
if (Test-Path $tracerPdb) { Copy-Item $tracerPdb $binPath }
Copy-Item (Join-Path $tracerOutput '0Harmony.dll') $binPath
# System.Text.Json: safety margin in case the target app pins an older
# version; small (~600 KB).
$stjDll = Join-Path $tracerOutput 'System.Text.Json.dll'
if (Test-Path $stjDll) { Copy-Item $stjDll $binPath }

# ── Help: XML (Get-Help <cmdlet>) + about_ topics ──────────────────────
# PlatyPS emits the MAML XML into module/{en-US,ja-JP}/ and about_ topics
# are hand-maintained alongside. Both locales get the same files so
# $PSUICulture resolution lands on them regardless of OS locale.
foreach ($locale in 'en-US', 'ja-JP') {
    $src = Join-Path $moduleSource $locale
    if (-not (Test-Path $src)) { continue }
    $dst = Join-Path $ModulePath $locale
    New-Item -Path $dst -ItemType Directory -Force | Out-Null
    Copy-Item "$src\*" $dst -Force
}

Write-Host "Deployed to $ModulePath" -ForegroundColor Green
Write-Host "Root:"
Get-ChildItem $ModulePath -File | Format-Table Name, Length -AutoSize
Write-Host "bin\:"
Get-ChildItem $binPath | Format-Table Name, Length -AutoSize
