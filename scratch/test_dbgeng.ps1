#Requires -Version 7.4
# Smoke test for the DbgEng interop. Opens an existing .dmp and runs a few commands.
param(
    [string]$Dll = 'C:\MyProj\CrashDrive\src\CrashDrive\bin\Debug\net8.0\CrashDrive.dll',
    [string]$Target = 'C:\MyProj\CrashDrive\scratch\pwsh.dmp'
)

Add-Type -Path $Dll

Write-Host "Opening $Target via DbgEngSession..." -ForegroundColor Cyan
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$session = [CrashDrive.DbgEng.DbgEngSession]::Open($Target, $null)
$sw.Stop()
Write-Host "Open took: $($sw.ElapsedMilliseconds)ms" -ForegroundColor Green

try {
    Write-Host "`n=== vertarget ===" -ForegroundColor Cyan
    $session.Execute('vertarget')

    Write-Host "`n=== dx Debugger.Sessions.Count ===" -ForegroundColor Cyan
    $session.Dx('Debugger.Sessions.Count')

    Write-Host "`n=== dx @`$curprocess.Threads.Count ===" -ForegroundColor Cyan
    $session.Dx('@$curprocess.Threads.Count')

    Write-Host "`n=== lm brief ===" -ForegroundColor Cyan
    $session.Execute('lm').Substring(0, [Math]::Min(800, ($session.Execute('lm')).Length))
} finally {
    $session.Dispose()
}
