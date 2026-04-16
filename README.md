# CrashDrive

PowerShell provider that mounts **execution trace files** and **crash dumps**
as PSDrives. Post-mortem inspection of what a program actually did — via the
filesystem metaphor humans and AI both already know.

## Concept

When a program crashes (or just runs), it leaves a wake behind:
- an **execution trace** (function calls, returns, variables at each step)
- or a **crash dump** (frozen state at the moment of death)

Both are big, structured data. CrashDrive mounts each file as its own
PSDrive so you can `cd`, `ls`, and `cat` your way through the wreckage.

```powershell
# Capture a trace, mount it
$d = Invoke-CrashCapture -Program app.py -Name myapp

# Mount an existing trace or dump
$d = New-CrashDrive -Name bug42 -File C:\dumps\crash.dmp

# Browse
Get-ChildItem myapp:\exceptions
Get-ChildItem myapp:\by-function\compute
Get-Content   myapp:\exceptions\1\locals.json
```

## Family

- **[DebuggerDrive](https://github.com/yotsuda/DebuggerDrive)** — live DAP debugger as PSDrive (running process)
- **CrashDrive** — post-mortem traces + dumps as PSDrives (frozen execution)

Live vs frozen. Driving the debugger vs inspecting the crash site.

## Status

Early work in progress. Python trace (`sys.monitoring`) support first;
Windows crash dumps (via ClrMD) planned.

## Requirements

- .NET 8 SDK (build)
- PowerShell 7.4+
- Python 3.12+ (for capturing Python traces)

## Install

```powershell
.\deploy.ps1
Import-Module CrashDrive
```
