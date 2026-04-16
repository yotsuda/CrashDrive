using CrashDrive.Store;
using Microsoft.Diagnostics.Runtime;

namespace CrashDrive.Dump;

/// <summary>
/// Opens a Windows minidump / .NET crash dump via ClrMD. DataTarget is loaded
/// eagerly (it's a mmap operation — fast). Per-subsystem data (threads, modules,
/// heap) is exposed via lazy properties so the first ls of the drive returns
/// instantly.
/// </summary>
public sealed class DumpStore : IStore
{
    private readonly string? _symbolPath;
    private readonly DataTarget _target;
    private readonly Lazy<ClrRuntime?> _runtime;
    private readonly Lazy<IReadOnlyList<DumpThreadInfo>> _threads;
    private readonly Lazy<IReadOnlyList<DumpModuleInfo>> _modules;
    private readonly Lazy<DumpSummary> _summary;

    public string FilePath { get; }
    public long FileSizeBytes { get; }
    public DateTime LastWriteTime { get; }
    public StoreKind Kind => StoreKind.Dump;

    public bool IsLoaded => _runtime.IsValueCreated;

    public DumpStore(string path, string? symbolPath = null)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Dump file not found: {path}");

        FilePath = path;
        _symbolPath = symbolPath;
        var info = new FileInfo(path);
        FileSizeBytes = info.Length;
        LastWriteTime = info.LastWriteTimeUtc;

        _target = DataTarget.LoadDump(path);
        if (!string.IsNullOrEmpty(symbolPath))
        {
            _target.SetSymbolPath(symbolPath);
        }

        _runtime = new Lazy<ClrRuntime?>(CreateRuntime,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _threads = new Lazy<IReadOnlyList<DumpThreadInfo>>(BuildThreads,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _modules = new Lazy<IReadOnlyList<DumpModuleInfo>>(BuildModules,
            LazyThreadSafetyMode.ExecutionAndPublication);
        _summary = new Lazy<DumpSummary>(BuildSummary,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool HasClr => _target.ClrVersions.Length > 0;

    public IReadOnlyList<DumpThreadInfo> Threads => _threads.Value;
    public IReadOnlyList<DumpModuleInfo> Modules => _modules.Value;
    public DumpSummary Summary => _summary.Value;

    private ClrRuntime? CreateRuntime()
    {
        var clr = _target.ClrVersions.FirstOrDefault();
        return clr?.CreateRuntime();
    }

    private IReadOnlyList<DumpThreadInfo> BuildThreads()
    {
        var runtime = _runtime.Value;
        if (runtime == null) return [];

        var list = new List<DumpThreadInfo>();
        foreach (var t in runtime.Threads)
        {
            var frames = t.EnumerateStackTrace().Take(64).Select(f => new DumpFrameInfo
            {
                InstructionPointer = f.InstructionPointer,
                StackPointer = f.StackPointer,
                Method = f.Method?.Signature ?? f.Method?.Name,
                Module = f.Method?.Type?.Module?.Name,
                Kind = f.Kind.ToString(),
            }).ToList();

            list.Add(new DumpThreadInfo
            {
                ManagedThreadId = t.ManagedThreadId,
                OSThreadId = t.OSThreadId,
                GCMode = t.GCMode.ToString(),
                IsAlive = t.IsAlive,
                IsFinalizer = t.IsFinalizer,
                IsGC = t.IsGc,
                CurrentException = t.CurrentException != null
                    ? new DumpExceptionInfo
                    {
                        TypeName = t.CurrentException.Type?.Name ?? "",
                        Message = t.CurrentException.Message ?? "",
                        HResult = t.CurrentException.HResult,
                    }
                    : null,
                Frames = frames,
            });
        }
        return list;
    }

    private IReadOnlyList<DumpModuleInfo> BuildModules()
    {
        var runtime = _runtime.Value;
        if (runtime == null) return [];

        return runtime.EnumerateModules()
            .Select(m => new DumpModuleInfo
            {
                Name = m.Name ?? "<unknown>",
                AssemblyName = m.AssemblyName,
                FileName = Path.GetFileName(m.Name ?? ""),
                Size = (long)m.Size,
                ImageBase = m.ImageBase,
                IsDynamic = m.IsDynamic,
                IsPEFile = m.IsPEFile,
            })
            .OrderBy(m => m.FileName)
            .ToList();
    }

    private DumpSummary BuildSummary()
    {
        var runtime = _runtime.Value;
        var clr = _target.ClrVersions.FirstOrDefault();
        return new DumpSummary
        {
            FilePath = FilePath,
            FileSizeBytes = FileSizeBytes,
            Architecture = _target.DataReader.Architecture.ToString(),
            IsMinidump = _target.DataReader.IsThreadSafe,     // approximation; dumpflags not directly exposed
            ClrVersion = clr?.Version.ToString(),
            ClrFlavor = clr?.Flavor.ToString(),
            ThreadCount = runtime?.Threads.Length ?? 0,
            ModuleCount = runtime?.EnumerateModules().Count() ?? 0,
        };
    }

    public void Dispose()
    {
        if (_runtime.IsValueCreated)
        {
            try { _runtime.Value?.Dispose(); } catch { }
        }
        _target.Dispose();
    }
}

// ─── DTOs exposed to the provider ──────────────────────────────────

public sealed class DumpSummary
{
    public string FilePath { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public string Architecture { get; set; } = "";
    public bool IsMinidump { get; set; }
    public string? ClrVersion { get; set; }
    public string? ClrFlavor { get; set; }
    public int ThreadCount { get; set; }
    public int ModuleCount { get; set; }
}

public sealed class DumpThreadInfo
{
    public int ManagedThreadId { get; set; }
    public uint OSThreadId { get; set; }
    public string GCMode { get; set; } = "";
    public bool IsAlive { get; set; }
    public bool IsFinalizer { get; set; }
    public bool IsGC { get; set; }
    public DumpExceptionInfo? CurrentException { get; set; }
    public IReadOnlyList<DumpFrameInfo> Frames { get; set; } = [];
}

public sealed class DumpFrameInfo
{
    public ulong InstructionPointer { get; set; }
    public ulong StackPointer { get; set; }
    public string? Method { get; set; }
    public string? Module { get; set; }
    public string Kind { get; set; } = "";
}

public sealed class DumpModuleInfo
{
    public string Name { get; set; } = "";
    public string? AssemblyName { get; set; }
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public ulong ImageBase { get; set; }
    public bool IsDynamic { get; set; }
    public bool IsPEFile { get; set; }
}

public sealed class DumpExceptionInfo
{
    public string TypeName { get; set; } = "";
    public string Message { get; set; } = "";
    public int HResult { get; set; }
}
