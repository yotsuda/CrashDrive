using CrashDrive.DbgEng;
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
        _analyzeOutput = new Lazy<string>(RunAnalyze,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool HasClr => _target.ClrVersions.Length > 0;

    public IReadOnlyList<DumpThreadInfo> Threads => _threads.Value;
    public IReadOnlyList<DumpModuleInfo> Modules => _modules.Value;
    public DumpSummary Summary => _summary.Value;

    /// <summary>Access the ClrMD runtime for callers that need direct heap /
    /// type / object inspection. Null if the dump has no CLR target.</summary>
    public ClrRuntime? ClrRuntime => _runtime.Value;

    // ─── Heap walker (lazy, single-pass) ──────────────────────────

    /// <summary>
    /// Returns per-type instance stats from the GC heap. Computed once via a
    /// full heap enumeration, then cached. Sorted by total bytes descending.
    /// </summary>
    public IReadOnlyList<DumpTypeStats> HeapTypes
    {
        get
        {
            // Lazy-init here since we want it depending on _runtime.Value
            if (_heapTypesCache != null) return _heapTypesCache;
            lock (_heapLock)
            {
                if (_heapTypesCache != null) return _heapTypesCache;
                _heapTypesCache = BuildHeapTypes();
                return _heapTypesCache;
            }
        }
    }

    private readonly object _heapLock = new();
    private IReadOnlyList<DumpTypeStats>? _heapTypesCache;

    // ─── !analyze -v (via dbgeng) ────────────────────────────────────
    //
    // Microsoft's crash-triage heuristics live in ext.dll / !analyze, which is
    // effectively unreplayable from scratch. We route through the shared
    // DbgEngSessionManager (see that class for the process-singleton rationale)
    // and cache the output — most users never ask for it.

    private readonly Lazy<string> _analyzeOutput;

    /// <summary>Run `!analyze -v` via dbgeng and return the captured text. Cached.</summary>
    public string AnalyzeOutput => _analyzeOutput.Value;

    // ─── Per-thread register state (via dbgeng) ──────────────────────
    //
    // ClrMD doesn't expose register context. We run '~<osid>s; r' on the
    // shared dbgeng session and cache per-thread output. The cache is valid
    // across session swaps: the dump file is immutable, so repeated reopens
    // produce identical register state.

    private readonly object _regCacheLock = new();
    private readonly Dictionary<uint, string> _regCache = new();

    /// <summary>Return a multi-line register dump for the thread identified by
    /// ClrMD's managed thread id, or an error string if the thread or register
    /// state can't be located.</summary>
    public string RenderRegistersForThread(int managedThreadId)
    {
        var t = Threads.FirstOrDefault(x => x.ManagedThreadId == managedThreadId);
        if (t == null) return $"Thread #{managedThreadId} not found.\n";
        return RenderRegistersByOsId(t.OSThreadId);
    }

    private string RenderRegistersByOsId(uint osThreadId)
    {
        lock (_regCacheLock)
            if (_regCache.TryGetValue(osThreadId, out var cached)) return cached;

        try
        {
            using var lease = DbgEngSessionManager.AcquireFor(FilePath, _symbolPath);
            // `~~[<hex>]s` switches current thread by OS id; `r` dumps
            // the general-purpose register file for the target arch.
            var output = lease.Session.Execute($"~~[0x{osThreadId:X}]s;r");
            lock (_regCacheLock) _regCache[osThreadId] = output;
            return output;
        }
        catch (Exception ex)
        {
            return $"Failed to read registers for OS thread 0x{osThreadId:X}: " +
                $"{ex.GetType().Name}: {ex.Message}\n";
        }
    }

    /// <summary>Run an arbitrary dbgeng command against this dump's shared session.
    /// Intended for cmdlets that need raw debugger access (memory reads, u/x/dt, etc.).</summary>
    public string ExecuteDbgCommand(string command)
    {
        using var lease = DbgEngSessionManager.AcquireFor(FilePath, _symbolPath);
        return lease.Session.Execute(command);
    }

    private string RunAnalyze()
    {
        try
        {
            using var lease = DbgEngSessionManager.AcquireFor(FilePath, _symbolPath);
            // Short-circuit on non-crash snapshots: .exr -1 dumps the last exception
            // record, or prints "ExceptionAddress: 0000000000000000" / similar when
            // none exists. !analyze -v on a snapshot still runs (slowly) but has no
            // useful output — prefer explicit messaging to a 10-minute symbol walk.
            var exr = lease.Session.Execute(".exr -1");
            if (string.IsNullOrWhiteSpace(exr)
                || exr.Contains("ExceptionAddress: 0000000000000000", StringComparison.OrdinalIgnoreCase)
                || exr.Contains("No exception record", StringComparison.OrdinalIgnoreCase)
                || exr.Contains("no stored exception", StringComparison.OrdinalIgnoreCase))
            {
                return "!analyze -v skipped: no exception record in this dump " +
                    "(likely a process snapshot rather than a crash).\n" +
                    "Run analysis manually via dbgeng if needed, or regenerate this " +
                    "dump from an actual crash.\n\n" +
                    "--- .exr -1 output ---\n" + exr;
            }
            return lease.Session.Execute("!analyze -v");
        }
        catch (Exception ex)
        {
            return $"!analyze -v failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private IReadOnlyList<DumpTypeStats> BuildHeapTypes()
    {
        var runtime = _runtime.Value;
        if (runtime == null) return [];

        var agg = new Dictionary<string, (int Count, long Bytes)>(StringComparer.Ordinal);
        try
        {
            foreach (var obj in runtime.Heap.EnumerateObjects())
            {
                var name = obj.Type?.Name ?? "<unknown>";
                var size = (long)obj.Size;
                if (agg.TryGetValue(name, out var v))
                    agg[name] = (v.Count + 1, v.Bytes + size);
                else
                    agg[name] = (1, size);
            }
        }
        catch
        {
            // Heap walk can fail on partial dumps — return what we got.
        }

        return agg
            .Select(kv => new DumpTypeStats
            {
                TypeName = kv.Key,
                InstanceCount = kv.Value.Count,
                TotalBytes = kv.Value.Bytes,
            })
            .OrderByDescending(s => s.TotalBytes)
            .ToList();
    }

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
        // Managed modules come from ClrRuntime (rich: assembly name, dynamic flag).
        // Native modules come from the raw data reader (lm-equivalent — ntdll, the
        // EXE itself, kernel32, etc.). We union both; managed info takes priority
        // when the same ImageBase appears in both enumerations.
        var byBase = new Dictionary<ulong, DumpModuleInfo>();

        // Native first: every loaded PE as seen by the OS loader.
        foreach (var m in _target.DataReader.EnumerateModules())
        {
            byBase[m.ImageBase] = new DumpModuleInfo
            {
                Name = m.FileName ?? "<unknown>",
                FileName = Path.GetFileName(m.FileName ?? ""),
                Size = m.ImageSize,
                ImageBase = m.ImageBase,
                IsManaged = false,
                IsPEFile = true,
            };
        }

        // Managed: enrich matching entries, add any managed-only (e.g. dynamic assemblies).
        var runtime = _runtime.Value;
        if (runtime != null)
        {
            foreach (var m in runtime.EnumerateModules())
            {
                var entry = new DumpModuleInfo
                {
                    Name = m.Name ?? "<unknown>",
                    AssemblyName = m.AssemblyName,
                    FileName = Path.GetFileName(m.Name ?? ""),
                    Size = (long)m.Size,
                    ImageBase = m.ImageBase,
                    IsDynamic = m.IsDynamic,
                    IsPEFile = m.IsPEFile,
                    IsManaged = true,
                };
                byBase[m.ImageBase] = entry;
            }
        }

        return byBase.Values.OrderBy(m => m.FileName).ToList();
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
            ModuleCount = Modules.Count,
        };
    }

    public void Dispose()
    {
        if (_runtime.IsValueCreated)
        {
            try { _runtime.Value?.Dispose(); } catch { }
        }
        lock (_regCacheLock) _regCache.Clear();
        _target.Dispose();
        DbgEngSessionManager.CloseIf(FilePath);
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
    public bool IsManaged { get; set; }
}

public sealed class DumpExceptionInfo
{
    public string TypeName { get; set; } = "";
    public string Message { get; set; } = "";
    public int HResult { get; set; }
}

public sealed class DumpTypeStats
{
    public string TypeName { get; set; } = "";
    public int InstanceCount { get; set; }
    public long TotalBytes { get; set; }
}
