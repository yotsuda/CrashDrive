# CrashDrive

Mount Windows post-mortem artifacts as PSDrives — `ls`, `cd`, `cat`
your way through crash dumps, Time-Travel Debugging recordings, and
execution traces.

## The Idea

A crash dump is a tree of information. A filesystem is the universal
tree interface. CrashDrive surfaces post-mortem data as paths:

```powershell
Import-Module CrashDrive
New-CrashDrive -Name dmp -Path .\crash.dmp

cd dmp:\threads\12\frames
Get-ChildItem | Format-Table Index, Method, SourceFile, Line
```

The same idioms humans already use (`Get-ChildItem`, `Get-Content`,
`cd`) work identically when an AI agent browses the same drive.

## Providers

| Provider | Opens                             | Backend              |
|----------|-----------------------------------|----------------------|
| `Trace`  | Python `sys.monitoring` JSONL     | direct JSON          |
| `Dump`   | Windows minidumps, .NET dumps     | ClrMD + dbgeng       |
| `Ttd`    | Time-Travel Debugging `.run`      | dbgeng + TTDAnalyze  |

The provider is picked automatically from the file.

## Path Tour

### Dump drive

```
dmp:\
├── summary.json         metadata (arch, CLR flavor, counts)
├── analyze.txt          !analyze -v output (cached; skipped on non-crash snapshots)
├── threads\<id>\
│   ├── info.json
│   ├── registers.txt
│   └── frames\<n>       stack frames with SourceFile + Line
├── modules\             every loaded module (native + managed)
└── heap\                GC heap types with InstanceCount + TotalBytes
```

### TTD drive

```
ttd:\
├── triage.md            answer-first overview
├── summary.json
├── timeline\
│   ├── events\          all events ordered by position
│   ├── exceptions\      Type matching Exception*
│   └── significant\     Module*/Thread* events
├── positions\
│   ├── start\           lifetime start
│   ├── end\             lifetime end
│   └── <major>_<minor>\ arbitrary time positions
│       └── threads\<id>\frames\<n>
├── ttd-events\          notable events during the recording
├── calls\<module>\<fn>\ every invocation of a named function
└── memory\<start>_<end>\
    ├── reads\           read accesses
    ├── writes\          write accesses
    ├── first-write.json first write in the range
    └── last-write-before\<pos>\
```

## Cmdlets

| Cmdlet                          | Purpose                                                  |
|---------------------------------|----------------------------------------------------------|
| `New-CrashDrive`                | Mount a trace, dump, or TTD recording as a PSDrive       |
| `Invoke-CrashCapture`           | Capture a Python `sys.monitoring` trace and mount it     |
| `Enable-CrashEditorFollow`      | `cd` into a frame/event → editor jumps to the line       |
| `Disable-CrashEditorFollow`     | Turn it off                                              |
| `Read-CrashMemory`              | Raw memory read through the shared dbgeng session        |
| `Get-CrashObject`               | Managed heap object inspection via ClrMD                 |
| `Get-CrashLocalVariable`        | Inspect locals at a frame (dbgeng `dv`)                  |

## Source Resolution

- **Native frames** resolve via dbgeng `ln`. Works when the module
  has private or source-indexed PDBs. Public Microsoft PDBs lack
  source info and return null.
- **Managed frames** resolve via ClrMD + portable PDB sequence
  points. Requires `DumpType.Full` for arbitrary JIT IPs; `WithHeap`
  may resolve stack-frame IPs but is not guaranteed. Only portable
  PDBs are supported (matches modern .NET defaults).

With `Enable-CrashEditorFollow`, `cd` into a frame or event jumps
VS Code straight to `SourceFile:Line`.

## Requirements

- Windows
- PowerShell 7.4+
- .NET 8 SDK (build only)
- **WinDbg Preview** from the Microsoft Store — required for the
  `Ttd` provider (System32 dbgeng cannot open `.run` files)
- Python 3.12+ — only for `Invoke-CrashCapture`

## Install

From source:

```powershell
.\deploy.ps1
Import-Module CrashDrive
```

PSGallery install will land at 1.0.

## Status

Pre-1.0 (0.9.x). Core providers are operational and used regularly
for real investigations; the API surface may still change before
the 1.0 cut.

## Related

- **[DebuggerDrive](https://github.com/yotsuda/DebuggerDrive)** —
  live DAP debugger as a PSDrive. CrashDrive's post-mortem sibling.

## License

[MIT](LICENSE).
