# CrashDrive

Mount Windows post-mortem artifacts as PSDrives — `ls`, `cd`, `cat`
your way through crash dumps, Time-Travel Debugging recordings, and
execution traces.

## The Idea

A crash dump is a tree of information. A filesystem is the universal
tree interface. CrashDrive surfaces post-mortem data as paths:

```powershell
Import-Module CrashDrive
New-CrashDrive dmp .\crash.dmp

cd dmp:\threads\12\frames
Get-ChildItem | Format-Table Index, Method, SourceFile, Line
```

The same idioms humans already use (`Get-ChildItem`, `Get-Content`,
`cd`) work identically when an AI agent browses the same drive.

## Providers

| Provider | Opens                             | Backend              |
|----------|-----------------------------------|----------------------|
| `Trace`  | Python `sys.monitoring` JSONL or `.NET` Harmony trace | direct JSON |
| `Dump`   | Windows minidumps, .NET dumps     | ClrMD + dbgeng       |
| `Ttd`    | Time-Travel Debugging `.run`      | dbgeng + TTDAnalyze  |

Provider is picked automatically from the file.

## Two Modes of Creating a Drive

`New-CrashDrive` has two parameter sets:

**Mount** — open an existing artifact:

```powershell
New-CrashDrive foo .\crash.dmp             # positional: Name, Path
New-CrashDrive tt  .\recording.run
New-CrashDrive tr  .\python-trace.jsonl
```

**Capture** — launch a program under a tracer, then mount the result:

```powershell
New-CrashDrive app -ExecutablePath .\script.py         # Python tracer
New-CrashDrive app -ExecutablePath .\MyApp.exe         # .NET tracer
New-CrashDrive app -ExecutablePath .\MyApp.exe `
                   -Include 'MyApp*' `
                   -ExecutableArgs @('--flag', 'value')
```

`-ExecutablePath` is deliberately non-positional so "read an artifact"
vs "execute a program" is unambiguous at the call site — one is safe
reading, the other spawns a process.

`-Language` is auto-detected from the extension (`.py`/`.pyw` → python,
`.exe`/`.dll` → dotnet) and overridable.

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

### Trace drive

```
tr:\
├── summary.json         total events, types, unique functions
├── events\<n>           every event in sequence order
├── by-type\<type>\      events grouped by type (call/return/exception)
├── by-function\<fn>\    events grouped by function
└── exceptions\          exception occurrences with context
```

## Cmdlets

| Cmdlet                          | Purpose                                                  |
|---------------------------------|----------------------------------------------------------|
| `New-CrashDrive`                | Mount a trace/dump/TTD, or capture a new trace + mount   |
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

## .NET Tracer Notes

The `-Language dotnet` path runs the target under a
`DOTNET_STARTUP_HOOKS` loaded assembly that uses
[Harmony](https://github.com/pardeike/Harmony) to patch every concrete
method in the user assemblies — no changes required to the target
program.

Two caveats worth knowing:

- **Requires .NET 6+** targets (the `DOTNET_STARTUP_HOOKS` env var
  is supported from .NET Core 3.1 but the tracer is built against
  net6.0 for ease).
- **JIT inlining**. Harmony patches a method's stub; if the JIT has
  inlined the method into its caller, calls through that inlined
  copy won't be intercepted. For trivial one-liner methods (like
  `int Add(int a, int b) => a + b`), the JIT will usually inline
  them and Harmony can't see the calls. In real-world code where
  methods are non-trivial, inlining is much less aggressive and the
  tracer sees most calls. If you hit this for a specific method,
  annotate it with `[MethodImpl(MethodImplOptions.NoInlining)]`.

Use `-Include 'MyApp*'` to restrict patching to specific assemblies
(by name glob); otherwise the default filter patches user-authored
assemblies and skips BCL / runtime / CrashDrive itself.

## Requirements

- Windows
- PowerShell 7.4+
- .NET 8 SDK (build only)
- **WinDbg Preview** from the Microsoft Store — required for the
  `Ttd` provider (System32 dbgeng cannot open `.run` files)
- Python 3.12+ — only for capturing Python traces
- .NET 6+ target — only for capturing .NET traces

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
