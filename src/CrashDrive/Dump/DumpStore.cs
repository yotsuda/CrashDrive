using CrashDrive.DbgEng;
using CrashDrive.Store;
using Microsoft.Diagnostics.Runtime;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

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
        _stackFrameMethods = new Lazy<Dictionary<ulong, ClrMethod>>(BuildStackFrameMethodCache,
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
            EnsureHeapBuilt();
            return _heapTypesCache ?? (IReadOnlyList<DumpTypeStats>)Array.Empty<DumpTypeStats>();
        }
    }

    /// <summary>Per-generation breakdown computed in the same heap walk as
    /// <see cref="HeapTypes"/>. Keys are "gen0", "gen1", "gen2", "loh",
    /// "pinned", "frozen". Absent keys mean the walk found zero objects in
    /// that generation. Values sorted by total bytes desc.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<DumpTypeStats>> HeapTypesByGeneration
    {
        get
        {
            EnsureHeapBuilt();
            return _heapByGenCache
                ?? (IReadOnlyDictionary<string, IReadOnlyList<DumpTypeStats>>)
                   new Dictionary<string, IReadOnlyList<DumpTypeStats>>();
        }
    }

    private readonly object _heapLock = new();
    private IReadOnlyList<DumpTypeStats>? _heapTypesCache;
    private IReadOnlyDictionary<string, IReadOnlyList<DumpTypeStats>>? _heapByGenCache;

    private void EnsureHeapBuilt()
    {
        if (_heapTypesCache != null) return;
        lock (_heapLock)
        {
            if (_heapTypesCache != null) return;
            BuildHeap(out _heapTypesCache, out _heapByGenCache);
        }
    }

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

    // ─── Source location lookup ──────────────────────────────────────
    //
    // Resolving (ip → file, line) is a per-frame operation that editor-follow
    // relies on. Two backends:
    //   • Managed frames: ClrMD maps IP → ClrMethod → IL offset, and a portable
    //     PDB sequence-point scan maps IL offset → (file, line). dbgeng's `ln`
    //     doesn't resolve JIT'd IPs, which is why this path is needed.
    //   • Native frames: dbgeng `ln 0x<ip>`. Returns null for public Microsoft
    //     PDBs (no source info); private or source-indexed PDBs resolve.
    //
    // Cache by IP since frames from different threads often share the same IP
    // (e.g. many threads parked in NtWaitForSingleObject). Dump state is
    // immutable so the cache is valid across session swaps.

    private readonly System.Collections.Concurrent.ConcurrentDictionary<ulong, SourceLocation?>
        _sourceCache = new();

    public SourceLocation? GetSourceLocation(ulong ip)
    {
        if (ip == 0) return null;
        if (_sourceCache.TryGetValue(ip, out var cached)) return cached;
        var result = TryManagedSourceLocation(ip) ?? TryNativeSourceLocation(ip);
        _sourceCache[ip] = result;
        return result;
    }

    private SourceLocation? TryNativeSourceLocation(ulong ip)
    {
        try
        {
            using var lease = DbgEngSessionManager.AcquireFor(FilePath, _symbolPath);
            var hit = lease.Session.GetSourceLocation(ip);
            return hit is { } v ? new SourceLocation(v.File, v.Line) : null;
        }
        catch { return null; }
    }

    // On DumpType.Full the code-heap index is present and
    // GetMethodByInstructionPointer resolves arbitrary JIT'd IPs. On WithHeap /
    // Normal dumps the index is absent, so we fall back to a cache built from
    // the ClrMD stack walker — for stack-frame IPs specifically it can recover
    // a ClrMethod even when the heap-index lookup returns null. Non-stack IPs
    // (e.g. arbitrary addresses fed from dbgeng) can't be recovered this way
    // and will still return null on non-Full dumps.
    //
    // Source line resolution is a separate constraint: ClrMethod.ILOffsetMap is
    // only populated when JIT code-heap metadata is available. So even with a
    // Method in hand, a Normal dump typically produces "method but no line",
    // which we surface as null (native backend then has its shot).

    private readonly Lazy<Dictionary<ulong, ClrMethod>> _stackFrameMethods;

    private Dictionary<ulong, ClrMethod> BuildStackFrameMethodCache()
    {
        var dict = new Dictionary<ulong, ClrMethod>();
        var runtime = _runtime.Value;
        if (runtime == null) return dict;
        try
        {
            foreach (var t in runtime.Threads)
            {
                foreach (var f in t.EnumerateStackTrace().Take(64))
                {
                    if (f.InstructionPointer == 0 || f.Method == null) continue;
                    dict[f.InstructionPointer] = f.Method;
                }
            }
        }
        catch { }
        return dict;
    }

    private SourceLocation? TryManagedSourceLocation(ulong ip)
    {
        var runtime = _runtime.Value;
        if (runtime == null) return null;

        ClrMethod? method = null;
        try { method = runtime.GetMethodByInstructionPointer(ip); }
        catch { }
        method ??= _stackFrameMethods.Value.TryGetValue(ip, out var cached) ? cached : null;
        if (method == null) return null;

        // ILOffsetMap entries associate native IP ranges with IL offsets.
        // Negative IL offsets are sentinels (no-mapping / prolog / epilog) and
        // don't correspond to a source line.
        int ilOffset = -1;
        foreach (var m in method.ILOffsetMap)
        {
            if (ip >= m.StartAddress && ip < m.EndAddress)
            {
                ilOffset = m.ILOffset;
                break;
            }
        }
        if (ilOffset < 0) return null;

        var module = method.Type?.Module;
        if (module == null) return null;

        var reader = GetPdbReader(module);
        if (reader == null) return null;

        int rowNumber = method.MetadataToken & 0x00FFFFFF;
        if (rowNumber == 0) return null;
        var handle = MetadataTokens.MethodDebugInformationHandle(rowNumber);

        MethodDebugInformation mdi;
        try { mdi = reader.GetMethodDebugInformation(handle); }
        catch { return null; }

        // Within a method, sequence points are emitted in IL order. Pick the
        // last non-hidden point whose offset ≤ target IL offset — that's the
        // statement whose native code the IP is executing.
        string? file = null;
        int line = 0;
        try
        {
            foreach (var sp in mdi.GetSequencePoints())
            {
                if (sp.IsHidden) continue;
                if (sp.Offset > ilOffset) break;
                var doc = reader.GetDocument(sp.Document);
                file = reader.GetString(doc.Name);
                line = sp.StartLine;
            }
        }
        catch { return null; }

        return file == null ? null : new SourceLocation(file, line);
    }

    // Portable PDB readers, kept alive for the store's lifetime because the
    // provider owns the backing stream — disposing it invalidates any
    // MetadataReader handed out. Keyed by module image base.
    private readonly object _pdbLock = new();
    private readonly Dictionary<ulong, MetadataReaderProvider?> _pdbReaders = new();

    private MetadataReader? GetPdbReader(ClrModule module)
    {
        lock (_pdbLock)
        {
            if (_pdbReaders.TryGetValue(module.ImageBase, out var cached))
                return cached?.GetMetadataReader();
            var provider = TryOpenPortablePdb(module);
            _pdbReaders[module.ImageBase] = provider;
            return provider?.GetMetadataReader();
        }
    }

    private static MetadataReaderProvider? TryOpenPortablePdb(ClrModule module)
    {
        // Two candidate paths: the compile-time path in the module's debug
        // directory (usually points at the build output), and a next-to-DLL
        // fallback for deployed scenarios. Windows PDBs (MSF container) are
        // not readable via System.Reflection.Metadata — we only handle
        // portable PDBs and leave Windows-PDB managed modules unresolved.
        var candidates = new List<string>();
        var pdb = module.Pdb;
        if (pdb != null && !string.IsNullOrWhiteSpace(pdb.Path))
            candidates.Add(pdb.Path);
        if (!string.IsNullOrWhiteSpace(module.Name))
        {
            var guess = Path.ChangeExtension(module.Name, ".pdb");
            if (!candidates.Contains(guess, StringComparer.OrdinalIgnoreCase))
                candidates.Add(guess);
        }

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                // Portable PDBs begin with the ECMA-335 "BSJB" metadata
                // signature (0x424A5342). Windows PDBs start with the MSF
                // header "Microsoft C/C++ MSF 7.00\r\n\u001aDS".
                using (var peek = File.OpenRead(path))
                {
                    Span<byte> header = stackalloc byte[4];
                    if (peek.Read(header) != 4) continue;
                    if (header[0] != 0x42 || header[1] != 0x53 ||
                        header[2] != 0x4A || header[3] != 0x42)
                        continue;
                }
                var stream = File.OpenRead(path);
                return MetadataReaderProvider.FromPortablePdbStream(stream);
            }
            catch { }
        }
        return null;
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

    private void BuildHeap(
        out IReadOnlyList<DumpTypeStats> overall,
        out IReadOnlyDictionary<string, IReadOnlyList<DumpTypeStats>> byGen)
    {
        overall = Array.Empty<DumpTypeStats>();
        byGen = new Dictionary<string, IReadOnlyList<DumpTypeStats>>();

        var runtime = _runtime.Value;
        if (runtime == null) return;

        var all = new Dictionary<string, (int Count, long Bytes)>(StringComparer.Ordinal);
        // Per-generation buckets keyed by GenerationKey(obj.Generation).
        var perGen = new Dictionary<string, Dictionary<string, (int Count, long Bytes)>>(StringComparer.Ordinal);

        try
        {
            foreach (var obj in runtime.Heap.EnumerateObjects())
            {
                var name = obj.Type?.Name ?? "<unknown>";
                var size = (long)obj.Size;

                if (all.TryGetValue(name, out var v))
                    all[name] = (v.Count + 1, v.Bytes + size);
                else
                    all[name] = (1, size);

                // Generation lookup: find the GC segment containing the
                // object, then ask it for the object's generation. On a
                // truncated / partial dump segment lookup can return null or
                // throw — fall through to "unknown" silently.
                string genKey;
                try
                {
                    var seg = runtime.Heap.GetSegmentByAddress(obj.Address);
                    genKey = GenerationKey(seg?.GetGeneration(obj.Address));
                }
                catch { genKey = "unknown"; }

                if (!perGen.TryGetValue(genKey, out var bucket))
                {
                    bucket = new Dictionary<string, (int Count, long Bytes)>(StringComparer.Ordinal);
                    perGen[genKey] = bucket;
                }
                if (bucket.TryGetValue(name, out var bv))
                    bucket[name] = (bv.Count + 1, bv.Bytes + size);
                else
                    bucket[name] = (1, size);
            }
        }
        catch
        {
            // Heap walk can fail partway on truncated dumps — surface what
            // we got rather than nothing.
        }

        overall = SortDesc(all);
        var result = new Dictionary<string, IReadOnlyList<DumpTypeStats>>(StringComparer.Ordinal);
        foreach (var (k, v) in perGen) result[k] = SortDesc(v);
        byGen = result;

        static IReadOnlyList<DumpTypeStats> SortDesc(Dictionary<string, (int Count, long Bytes)> d) =>
            d.Select(kv => new DumpTypeStats
             {
                 TypeName = kv.Key,
                 InstanceCount = kv.Value.Count,
                 TotalBytes = kv.Value.Bytes,
             })
             .OrderByDescending(s => s.TotalBytes)
             .ToList();
    }

    private static string GenerationKey(Microsoft.Diagnostics.Runtime.Generation? gen) => gen switch
    {
        Microsoft.Diagnostics.Runtime.Generation.Generation0 => "gen0",
        Microsoft.Diagnostics.Runtime.Generation.Generation1 => "gen1",
        Microsoft.Diagnostics.Runtime.Generation.Generation2 => "gen2",
        Microsoft.Diagnostics.Runtime.Generation.Large       => "loh",
        Microsoft.Diagnostics.Runtime.Generation.Pinned      => "pinned",
        Microsoft.Diagnostics.Runtime.Generation.Frozen      => "frozen",
        _ => "unknown",
    };

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
        lock (_pdbLock)
        {
            foreach (var provider in _pdbReaders.Values)
            {
                try { provider?.Dispose(); } catch { }
            }
            _pdbReaders.Clear();
        }
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

public sealed record SourceLocation(string File, int Line);
