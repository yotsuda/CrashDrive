using System.Management.Automation;
using System.Management.Automation.Provider;
using System.Text;
using System.Text.Json;
using CrashDrive.Dump;
using CrashDrive.Models;
using CrashDrive.Trace;

namespace CrashDrive.Provider;

/// <summary>
/// PSProvider "Dump" — mounts a Windows crash dump (.dmp) as a navigable
/// filesystem via ClrMD.
///
/// Hierarchy:
/// <code>
///   &lt;drive&gt;:\
///     summary.json
///     info.json
///     threads\&lt;id&gt;\{info.json, stack.txt, frames\&lt;n&gt;.json}
///     modules\&lt;filename&gt;.json
/// </code>
/// </summary>
[CmdletProvider("Dump", ProviderCapabilities.None)]
[OutputType(typeof(FolderItem), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
[OutputType(typeof(ThreadItem), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
[OutputType(typeof(FrameItem), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
[OutputType(typeof(ModuleItem), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
public sealed class DumpProvider : ProviderBase
{
    private DumpDriveInfo Drive => (DumpDriveInfo)PSDriveInfo;
    private DumpStore Store => Drive.Store;

    // === Drive lifecycle ===

    protected override object NewDriveDynamicParameters() => new NewCrashDriveDynamicParameters();

    protected override PSDriveInfo NewDrive(PSDriveInfo drive)
    {
        if (drive is DumpDriveInfo existing) return existing;

        var dyn = DynamicParameters as NewCrashDriveDynamicParameters
            ?? throw new PSArgumentException(
                "Dump provider requires -File. Use: New-PSDrive -PSProvider Dump -Name <name> -Root \\ -File <dmp-path>");

        var absFile = Path.GetFullPath(dyn.File);
        if (!File.Exists(absFile))
            throw new PSArgumentException($"File not found: {absFile}");

        var inner = new PSDriveInfo(drive.Name, drive.Provider, @"\",
            drive.Description ?? "", drive.Credential);
        return new DumpDriveInfo(inner, absFile, dyn.SymbolPath);
    }

    protected override PSDriveInfo RemoveDrive(PSDriveInfo drive)
    {
        if (drive is DumpDriveInfo info)
            try { info.Store.Dispose(); } catch { }
        return drive;
    }

    // === Path parsing ===

    private enum PathKind
    {
        Root, Summary, Analyze, Triage,
        ThreadsFolder, ThreadFolder, ThreadInfo, ThreadStack, ThreadException, ThreadRegisters,
        ThreadFramesFolder, FrameFile,
        ThreadsWithExceptionFolder,                 // threads/with-exception/
        ThreadsByStateFolder,                       // threads/by-state/
        ThreadsByStateCategoryFolder,               // threads/by-state/<state>/
        ModulesFolder, ModuleFile,
        ModulesByKindFolder,                        // modules/by-kind/
        ModulesByKindCategoryFolder,                // modules/by-kind/<kind>/ where kind ∈ {native,managed}
        HeapFolder, HeapTypesFolder, HeapTypeFile,
        HeapByGenerationFolder,                     // heap/by-generation/
        HeapByGenerationTypesFolder,                // heap/by-generation/<gen>/
        HeapByGenerationTypeFile,                   // heap/by-generation/<gen>/<type>.json
        Invalid,
    }

    private readonly record struct ParsedPath(
        PathKind Kind,
        string[] Segments,
        int? ThreadId = null,
        int? FrameIndex = null,
        string? ModuleFile = null,
        string? TypeName = null,
        string? State = null,
        string? ModuleKind = null,
        string? Generation = null);

    // Thread classification states exposed under threads\by-state\. The set is
    // deliberately narrow — things that can be computed from existing
    // DumpThreadInfo flags without walking frames.
    private static readonly string[] ThreadStates = { "finalizer", "gc", "dead" };

    // Module kinds exposed under modules\by-kind\.
    private static readonly string[] ModuleKinds = { "native", "managed" };

    // GC generation keys matching DumpStore.HeapTypesByGeneration. The visible
    // order under heap\by-generation\ follows the canonical lifecycle order:
    // young → old → large/pinned/frozen/unknown.
    private static readonly string[] GenerationOrder =
        { "gen0", "gen1", "gen2", "loh", "pinned", "frozen", "unknown" };

    private static bool MatchesState(DumpThreadInfo t, string state) => state switch
    {
        "finalizer" => t.IsFinalizer,
        "gc"        => t.IsGC,
        "dead"      => !t.IsAlive,
        _ => false,
    };

    private ParsedPath Parse(string path)
    {
        var segs = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0) return new(PathKind.Root, segs);

        var head = segs[0].ToLowerInvariant();
        if (segs.Length == 1)
        {
            return head switch
            {
                "summary.json" => new(PathKind.Summary, segs),
                "analyze.txt" => new(PathKind.Analyze, segs),
                "triage.md" => new(PathKind.Triage, segs),
                "threads" => new(PathKind.ThreadsFolder, segs),
                "modules" => new(PathKind.ModulesFolder, segs),
                "heap" => new(PathKind.HeapFolder, segs),
                _ => new(PathKind.Invalid, segs),
            };
        }

        if (head == "threads")
        {
            // Classifier paths: resolve to the direct threads\<id>\… form via
            // a synthetic re-parse. Kept here instead of in each leaf's Parse
            // case so the rewrite is one place.
            if (segs.Length >= 2 && segs[1].Equals("with-exception", StringComparison.OrdinalIgnoreCase))
            {
                if (segs.Length == 2) return new(PathKind.ThreadsWithExceptionFolder, segs);
                if (int.TryParse(segs[2], out var wxTid)
                    && Store.Threads.FirstOrDefault(t => t.ManagedThreadId == wxTid)?.CurrentException != null)
                {
                    var synthetic = "threads/" + string.Join('/', segs.Skip(2));
                    var resolved = Parse(synthetic);
                    return resolved.Kind == PathKind.Invalid
                        ? resolved
                        : resolved with { Segments = segs };
                }
                return new(PathKind.Invalid, segs);
            }
            if (segs.Length >= 2 && segs[1].Equals("by-state", StringComparison.OrdinalIgnoreCase))
            {
                if (segs.Length == 2) return new(PathKind.ThreadsByStateFolder, segs);
                var state = segs[2].ToLowerInvariant();
                if (!ThreadStates.Contains(state)) return new(PathKind.Invalid, segs);
                if (segs.Length == 3) return new(PathKind.ThreadsByStateCategoryFolder, segs, State: state);
                if (int.TryParse(segs[3], out var bsTid)
                    && Store.Threads.FirstOrDefault(t => t.ManagedThreadId == bsTid) is { } matchT
                    && MatchesState(matchT, state))
                {
                    var synthetic = "threads/" + string.Join('/', segs.Skip(3));
                    var resolved = Parse(synthetic);
                    return resolved.Kind == PathKind.Invalid
                        ? resolved
                        : resolved with { Segments = segs };
                }
                return new(PathKind.Invalid, segs);
            }

            if (!int.TryParse(segs[1], out var tid)) return new(PathKind.Invalid, segs);
            if (segs.Length == 2) return new(PathKind.ThreadFolder, segs, ThreadId: tid);
            var sub = segs[2].ToLowerInvariant();
            if (segs.Length == 3)
                return sub switch
                {
                    "info.json" => new(PathKind.ThreadInfo, segs, ThreadId: tid),
                    "stack.txt" => new(PathKind.ThreadStack, segs, ThreadId: tid),
                    "registers.txt" => new(PathKind.ThreadRegisters, segs, ThreadId: tid),
                    "exception.json" => new(PathKind.ThreadException, segs, ThreadId: tid),
                    "frames" => new(PathKind.ThreadFramesFolder, segs, ThreadId: tid),
                    _ => new(PathKind.Invalid, segs),
                };
            if (segs.Length == 4 && sub == "frames"
                && segs[3].EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(segs[3][..^5], out var fi))
                return new(PathKind.FrameFile, segs, ThreadId: tid, FrameIndex: fi);
        }
        if (head == "modules")
        {
            // modules\by-kind\<kind>\<file>.json — filter on IsManaged, translate
            // the leaf to the canonical modules\<file>.json lookup.
            if (segs.Length >= 2 && segs[1].Equals("by-kind", StringComparison.OrdinalIgnoreCase))
            {
                if (segs.Length == 2) return new(PathKind.ModulesByKindFolder, segs);
                var kind = segs[2].ToLowerInvariant();
                if (!ModuleKinds.Contains(kind)) return new(PathKind.Invalid, segs);
                if (segs.Length == 3)
                    return new(PathKind.ModulesByKindCategoryFolder, segs, ModuleKind: kind);
                if (segs.Length == 4
                    && segs[3].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    var file = segs[3][..^5];
                    var wantManaged = kind == "managed";
                    if (Store.Modules.Any(m => Sanitize(m.FileName)
                            .Equals(file, StringComparison.OrdinalIgnoreCase)
                            && m.IsManaged == wantManaged))
                        return new(PathKind.ModuleFile, segs, ModuleFile: file);
                    return new(PathKind.Invalid, segs);
                }
                return new(PathKind.Invalid, segs);
            }
            if (segs.Length == 2
                && segs[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                return new(PathKind.ModuleFile, segs, ModuleFile: segs[1][..^5]);
        }

        if (head == "heap")
        {
            if (segs.Length == 2 && segs[1] == "types")
                return new(PathKind.HeapTypesFolder, segs);
            if (segs.Length == 3 && segs[1] == "types"
                && segs[2].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                return new(PathKind.HeapTypeFile, segs, TypeName: segs[2][..^5]);

            // heap\by-generation\<gen>\<type>.json — type stats filtered to one
            // GC generation. Leaf item carries the same shape as heap\types\.
            if (segs.Length >= 2 && segs[1].Equals("by-generation", StringComparison.OrdinalIgnoreCase))
            {
                if (segs.Length == 2) return new(PathKind.HeapByGenerationFolder, segs);
                var gen = segs[2].ToLowerInvariant();
                if (!GenerationOrder.Contains(gen)) return new(PathKind.Invalid, segs);
                if (segs.Length == 3)
                    return new(PathKind.HeapByGenerationTypesFolder, segs, Generation: gen);
                if (segs.Length == 4
                    && segs[3].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    return new(PathKind.HeapByGenerationTypeFile, segs,
                        Generation: gen, TypeName: segs[3][..^5]);
            }
        }

        return new(PathKind.Invalid, segs);
    }

    // === Path operations ===

    protected override bool ItemExists(string path)
    {
        var info = Parse(NormalizePath(path));
        return info.Kind switch
        {
            PathKind.Invalid => false,
            PathKind.ThreadFolder or PathKind.ThreadInfo or PathKind.ThreadStack
                or PathKind.ThreadException or PathKind.ThreadRegisters or PathKind.ThreadFramesFolder
                => info.ThreadId is int tid && Store.Threads.Any(t => t.ManagedThreadId == tid),
            PathKind.FrameFile
                => info.ThreadId is int tid && info.FrameIndex is int fi
                   && Store.Threads.FirstOrDefault(t => t.ManagedThreadId == tid) is { } th
                   && fi >= 0 && fi < th.Frames.Count,
            PathKind.ModuleFile
                => info.ModuleFile != null && Store.Modules.Any(m =>
                    Sanitize(m.FileName).Equals(info.ModuleFile, StringComparison.OrdinalIgnoreCase)),
            PathKind.HeapTypeFile
                => info.TypeName != null && Store.HeapTypes.Any(t =>
                    SanitizeFull(t.TypeName).Equals(info.TypeName, StringComparison.Ordinal)),
            PathKind.HeapByGenerationTypesFolder when info.Generation != null
                => Store.HeapTypesByGeneration.ContainsKey(info.Generation),
            PathKind.HeapByGenerationTypeFile when info.Generation != null && info.TypeName != null
                => Store.HeapTypesByGeneration.TryGetValue(info.Generation, out var lst)
                    && lst.Any(t => SanitizeFull(t.TypeName)
                        .Equals(info.TypeName, StringComparison.Ordinal)),
            _ => true,
        };
    }

    protected override bool IsItemContainer(string path)
    {
        return Parse(NormalizePath(path)).Kind switch
        {
            PathKind.Root or PathKind.ThreadsFolder or PathKind.ThreadFolder or
            PathKind.ThreadFramesFolder or PathKind.ModulesFolder or
            PathKind.HeapFolder or PathKind.HeapTypesFolder or
            PathKind.ThreadsWithExceptionFolder or PathKind.ThreadsByStateFolder or
            PathKind.ThreadsByStateCategoryFolder or
            PathKind.ModulesByKindFolder or PathKind.ModulesByKindCategoryFolder or
            PathKind.HeapByGenerationFolder or PathKind.HeapByGenerationTypesFolder => true,
            _ => false,
        };
    }

    protected override bool HasChildItems(string path) => IsItemContainer(path);

    protected override void GetItem(string path)
    {
        var info = Parse(NormalizePath(path));
        var parent = GetParentPath(path, null);
        var dir = EnsureDrivePrefix(parent);
        var name = Path.GetFileName(path);

        switch (info.Kind)
        {
            case PathKind.ThreadFolder when info.ThreadId is int tid:
                var t = Store.Threads.FirstOrDefault(x => x.ManagedThreadId == tid);
                if (t != null)
                {
                    WriteItemObject(new Models.ThreadItem
                    {
                        ManagedThreadId = t.ManagedThreadId,
                        OSThreadId = t.OSThreadId,
                        GCMode = t.GCMode,
                        IsAlive = t.IsAlive,
                        IsFinalizer = t.IsFinalizer,
                        FrameCount = t.Frames.Count,
                        ExceptionSummary = t.CurrentException != null
                            ? $"{t.CurrentException.TypeName}: {t.CurrentException.Message}" : null,
                        Path = EnsureDrivePrefix(path),
                        Directory = dir,
                    }, path, isContainer: true);
                }
                break;

            case PathKind.FrameFile when info.ThreadId is int ftid && info.FrameIndex is int fi:
                var ft = Store.Threads.FirstOrDefault(x => x.ManagedThreadId == ftid);
                if (ft != null && fi >= 0 && fi < ft.Frames.Count)
                {
                    var f = ft.Frames[fi];
                    var src = Store.GetSourceLocation(f.InstructionPointer);
                    WriteItemObject(new Models.FrameItem
                    {
                        Index = fi,
                        Method = f.Method ?? "<unknown>",
                        Module = f.Module,
                        Kind = f.Kind,
                        IpHex = $"0x{f.InstructionPointer:X16}",
                        SourceFile = src?.File,
                        Line = src?.Line,
                        Path = EnsureDrivePrefix(path),
                        Directory = dir,
                    }, path, isContainer: false);
                }
                break;

            case PathKind.ModuleFile when info.ModuleFile != null:
                var m = Store.Modules.FirstOrDefault(x => Sanitize(x.FileName)
                    .Equals(info.ModuleFile, StringComparison.OrdinalIgnoreCase));
                if (m != null)
                {
                    WriteItemObject(new Models.ModuleItem
                    {
                        Name = m.Name, FileName = m.FileName,
                        Size = m.Size, ImageBaseHex = $"0x{m.ImageBase:X16}",
                        IsDynamic = m.IsDynamic,
                        IsManaged = m.IsManaged,
                        Path = EnsureDrivePrefix(path),
                        Directory = dir,
                    }, path, isContainer: false);
                }
                break;

            case PathKind.HeapTypeFile when info.TypeName != null:
                var stats = Store.HeapTypes.FirstOrDefault(s =>
                    SanitizeFull(s.TypeName).Equals(info.TypeName, StringComparison.Ordinal));
                if (stats != null)
                {
                    WriteItemObject(new Models.HeapTypeItem
                    {
                        Name = name,
                        TypeName = stats.TypeName,
                        InstanceCount = stats.InstanceCount,
                        TotalBytes = stats.TotalBytes,
                        Path = EnsureDrivePrefix(path),
                        Directory = dir,
                    }, path, isContainer: false);
                }
                break;

            case PathKind.HeapByGenerationTypeFile when info.Generation != null && info.TypeName != null:
                if (Store.HeapTypesByGeneration.TryGetValue(info.Generation, out var gtypes))
                {
                    var gstats = gtypes.FirstOrDefault(s =>
                        SanitizeFull(s.TypeName).Equals(info.TypeName, StringComparison.Ordinal));
                    if (gstats != null)
                        WriteItemObject(new Models.HeapTypeItem
                        {
                            Name = name,
                            TypeName = gstats.TypeName,
                            InstanceCount = gstats.InstanceCount,
                            TotalBytes = gstats.TotalBytes,
                            Path = EnsureDrivePrefix(path),
                            Directory = dir,
                        }, path, isContainer: false);
                }
                break;

            case PathKind.Summary or PathKind.Analyze or PathKind.Triage
                or PathKind.ThreadInfo or PathKind.ThreadStack
                or PathKind.ThreadException or PathKind.ThreadRegisters:
                WriteFile(name, path, dir);
                break;

            default:
                WriteFolder(name, path, dir, "", null);
                break;
        }
    }

    // === Children ===

    protected override void GetChildItems(string path, bool recurse)
        => GetChildItems(path, recurse, uint.MaxValue);

    protected override void GetChildItems(string path, bool recurse, uint depth)
    {
        try { WriteChildrenRecursive(path, recurse, depth); }
        catch (Exception ex) when (ex is not OperationCanceledException
                                   and not PipelineStoppedException)
        {
            WriteError(new ErrorRecord(ex, "DumpGetChildError", ErrorCategory.NotSpecified, path));
        }
    }

    private void WriteChildrenRecursive(string path, bool recurse, uint depth)
    {
        var info = Parse(NormalizePath(path));
        WriteChildren(info, path);
        if (!recurse || depth == 0) return;
        foreach (var (name, isContainer) in Enumerate(info))
        {
            if (Stopping) return;
            if (!isContainer) continue;
            WriteChildrenRecursive(MakePath(path, name), recurse: true, depth - 1);
        }
    }

    protected override void GetChildNames(string path, ReturnContainers returnContainers)
    {
        var info = Parse(NormalizePath(path));
        foreach (var (name, isContainer) in Enumerate(info))
        {
            if (Stopping) return;
            var escaped = WildcardPattern.ContainsWildcardCharacters(name)
                ? WildcardPattern.Escape(name) : name;
            WriteItemObject(escaped, MakePath(path, name), isContainer);
        }
    }

    private IEnumerable<(string name, bool isContainer)> Enumerate(ParsedPath info)
    {
        switch (info.Kind)
        {
            case PathKind.Root:
                yield return ("triage.md", false);
                yield return ("summary.json", false);
                yield return ("analyze.txt", false);
                yield return ("threads", true);
                yield return ("modules", true);
                yield return ("heap", true);
                break;
            case PathKind.HeapFolder:
                yield return ("types", true);
                if (Store.HeapTypesByGeneration.Count > 0)
                    yield return ("by-generation", true);
                break;
            case PathKind.HeapTypesFolder:
                foreach (var t in Store.HeapTypes)
                    yield return ($"{SanitizeFull(t.TypeName)}.json", false);
                break;
            case PathKind.ThreadsFolder:
                // Classifier containers come first so ls shows them above the
                // numeric IDs. Omit ones with zero matches to avoid dead ends.
                if (Store.Threads.Any(t => t.CurrentException != null))
                    yield return ("with-exception", true);
                if (ThreadStates.Any(s => Store.Threads.Any(t => MatchesState(t, s))))
                    yield return ("by-state", true);
                foreach (var t in Store.Threads)
                    yield return (t.ManagedThreadId.ToString(), true);
                break;

            case PathKind.ThreadsWithExceptionFolder:
                foreach (var t in Store.Threads.Where(x => x.CurrentException != null))
                    yield return (t.ManagedThreadId.ToString(), true);
                break;

            case PathKind.ThreadsByStateFolder:
                foreach (var st in ThreadStates)
                    if (Store.Threads.Any(t => MatchesState(t, st)))
                        yield return (st, true);
                break;

            case PathKind.ThreadsByStateCategoryFolder when info.State != null:
                foreach (var t in Store.Threads.Where(x => MatchesState(x, info.State)))
                    yield return (t.ManagedThreadId.ToString(), true);
                break;
            case PathKind.ThreadFolder:
                yield return ("info.json", false);
                yield return ("stack.txt", false);
                yield return ("registers.txt", false);
                if (info.ThreadId is int tidEx
                    && Store.Threads.FirstOrDefault(x => x.ManagedThreadId == tidEx)?.CurrentException != null)
                    yield return ("exception.json", false);
                yield return ("frames", true);
                break;
            case PathKind.ThreadFramesFolder:
                if (info.ThreadId is int tid)
                {
                    var t = Store.Threads.FirstOrDefault(x => x.ManagedThreadId == tid);
                    if (t != null)
                        for (int i = 0; i < t.Frames.Count; i++)
                            yield return ($"{i}.json", false);
                }
                break;
            case PathKind.ModulesFolder:
                // by-kind first so it's visible above the long module list.
                if (Store.Modules.Any(m => m.IsManaged) || Store.Modules.Any(m => !m.IsManaged))
                    yield return ("by-kind", true);
                foreach (var m in Store.Modules)
                    yield return ($"{Sanitize(m.FileName)}.json", false);
                break;

            case PathKind.ModulesByKindFolder:
                foreach (var kind in ModuleKinds)
                {
                    var wantManaged = kind == "managed";
                    if (Store.Modules.Any(m => m.IsManaged == wantManaged))
                        yield return (kind, true);
                }
                break;

            case PathKind.ModulesByKindCategoryFolder when info.ModuleKind != null:
                {
                    var wantManaged = info.ModuleKind == "managed";
                    foreach (var m in Store.Modules.Where(x => x.IsManaged == wantManaged))
                        yield return ($"{Sanitize(m.FileName)}.json", false);
                }
                break;

            case PathKind.HeapByGenerationFolder:
                {
                    var byGen = Store.HeapTypesByGeneration;
                    foreach (var gen in GenerationOrder)
                        if (byGen.ContainsKey(gen))
                            yield return (gen, true);
                }
                break;

            case PathKind.HeapByGenerationTypesFolder when info.Generation != null:
                if (Store.HeapTypesByGeneration.TryGetValue(info.Generation, out var gtypes))
                    foreach (var t in gtypes)
                        yield return ($"{SanitizeFull(t.TypeName)}.json", false);
                break;
        }
    }

    private void WriteChildren(ParsedPath info, string path)
    {
        var dir = EnsureDrivePrefix(path);
        switch (info.Kind)
        {
            case PathKind.Root:
                WriteFile("triage.md", MakePath(path, "triage.md"), dir);
                WriteFile("summary.json", MakePath(path, "summary.json"), dir);
                WriteFile("analyze.txt", MakePath(path, "analyze.txt"), dir);
                WriteFolder("threads", MakePath(path, "threads"), dir,
                    "threads at time of dump", Store.Summary.ThreadCount);
                WriteFolder("modules", MakePath(path, "modules"), dir,
                    "loaded modules", Store.Summary.ModuleCount);
                WriteFolder("heap", MakePath(path, "heap"), dir,
                    "GC heap analysis", null);
                break;
            case PathKind.HeapFolder:
                WriteFolder("types", MakePath(path, "types"), dir,
                    "instance counts and bytes per type, sorted by total bytes",
                    null);
                if (Store.HeapTypesByGeneration.Count > 0)
                    WriteFolder("by-generation", MakePath(path, "by-generation"), dir,
                        "heap types bucketed per GC generation (gen0/gen1/gen2/loh/…)",
                        Store.HeapTypesByGeneration.Count);
                break;
            case PathKind.HeapTypesFolder:
                foreach (var t in Store.HeapTypes)
                {
                    if (Stopping) return;
                    var tName = $"{SanitizeFull(t.TypeName)}.json";
                    var tPath = MakePath(path, tName);
                    WriteItemObject(new HeapTypeItem
                    {
                        Name = tName,
                        TypeName = t.TypeName,
                        InstanceCount = t.InstanceCount,
                        TotalBytes = t.TotalBytes,
                        Path = EnsureDrivePrefix(tPath),
                        Directory = dir,
                    }, tPath, isContainer: false);
                }
                break;
            case PathKind.ThreadsFolder:
                if (Store.Threads.Any(x => x.CurrentException != null))
                {
                    var cnt = Store.Threads.Count(x => x.CurrentException != null);
                    WriteFolder("with-exception", MakePath(path, "with-exception"), dir,
                        "threads with CurrentException != null", cnt);
                }
                if (ThreadStates.Any(s => Store.Threads.Any(x => MatchesState(x, s))))
                    WriteFolder("by-state", MakePath(path, "by-state"), dir,
                        "synthetic classifiers: finalizer, gc, dead", null);
                foreach (var t in Store.Threads)
                {
                    if (Stopping) return;
                    WriteThreadItem(t, path, dir);
                }
                break;

            case PathKind.ThreadsWithExceptionFolder:
                foreach (var t in Store.Threads.Where(x => x.CurrentException != null))
                {
                    if (Stopping) return;
                    WriteThreadItem(t, path, dir);
                }
                break;

            case PathKind.ThreadsByStateFolder:
                foreach (var st in ThreadStates)
                {
                    if (Stopping) return;
                    var cnt = Store.Threads.Count(t => MatchesState(t, st));
                    if (cnt == 0) continue;
                    WriteFolder(st, MakePath(path, st), dir,
                        $"threads matching '{st}'", cnt);
                }
                break;

            case PathKind.ThreadsByStateCategoryFolder when info.State != null:
                foreach (var t in Store.Threads.Where(x => MatchesState(x, info.State)))
                {
                    if (Stopping) return;
                    WriteThreadItem(t, path, dir);
                }
                break;
            case PathKind.ThreadFolder:
                WriteFile("info.json", MakePath(path, "info.json"), dir);
                WriteFile("stack.txt", MakePath(path, "stack.txt"), dir);
                WriteFile("registers.txt", MakePath(path, "registers.txt"), dir);
                if (info.ThreadId is int tidExWrite
                    && Store.Threads.FirstOrDefault(x => x.ManagedThreadId == tidExWrite)?.CurrentException != null)
                    WriteFile("exception.json", MakePath(path, "exception.json"), dir);
                WriteFolder("frames", MakePath(path, "frames"), dir, "stack frames", null);
                break;
            case PathKind.ThreadFramesFolder:
                if (info.ThreadId is int tid
                    && Store.Threads.FirstOrDefault(x => x.ManagedThreadId == tid) is { } thread)
                {
                    for (int i = 0; i < thread.Frames.Count; i++)
                    {
                        if (Stopping) return;
                        var f = thread.Frames[i];
                        var fPath = MakePath(path, $"{i}.json");
                        var src = Store.GetSourceLocation(f.InstructionPointer);
                        WriteItemObject(new FrameItem
                        {
                            Index = i,
                            Method = f.Method ?? "<unknown>",
                            Module = f.Module,
                            Kind = f.Kind,
                            IpHex = $"0x{f.InstructionPointer:X16}",
                            SourceFile = src?.File,
                            Line = src?.Line,
                            Path = EnsureDrivePrefix(fPath),
                            Directory = dir,
                        }, fPath, isContainer: false);
                    }
                }
                break;
            case PathKind.ModulesFolder:
                if (Store.Modules.Any())
                {
                    var native = Store.Modules.Count(x => !x.IsManaged);
                    var managed = Store.Modules.Count(x => x.IsManaged);
                    WriteFolder("by-kind", MakePath(path, "by-kind"), dir,
                        $"native={native}, managed={managed}", native + managed);
                }
                foreach (var m in Store.Modules)
                {
                    if (Stopping) return;
                    WriteModuleItem(m, MakePath(path, $"{Sanitize(m.FileName)}.json"), dir);
                }
                break;

            case PathKind.ModulesByKindFolder:
                foreach (var kind in ModuleKinds)
                {
                    if (Stopping) return;
                    var wantManaged = kind == "managed";
                    var cnt = Store.Modules.Count(m => m.IsManaged == wantManaged);
                    if (cnt == 0) continue;
                    WriteFolder(kind, MakePath(path, kind), dir,
                        kind == "native" ? "PE modules seen by the OS loader"
                                         : "managed assemblies (CLR runtime)",
                        cnt);
                }
                break;

            case PathKind.ModulesByKindCategoryFolder when info.ModuleKind != null:
                {
                    var wantManaged = info.ModuleKind == "managed";
                    foreach (var m in Store.Modules.Where(x => x.IsManaged == wantManaged))
                    {
                        if (Stopping) return;
                        WriteModuleItem(m, MakePath(path, $"{Sanitize(m.FileName)}.json"), dir);
                    }
                }
                break;

            case PathKind.HeapByGenerationFolder:
                {
                    var byGen = Store.HeapTypesByGeneration;
                    foreach (var gen in GenerationOrder)
                    {
                        if (Stopping) return;
                        if (!byGen.TryGetValue(gen, out var lst)) continue;
                        var totalBytes = lst.Sum(t => t.TotalBytes);
                        var totalInst = lst.Sum(t => (long)t.InstanceCount);
                        WriteFolder(gen, MakePath(path, gen), dir,
                            $"{totalInst} objects, {totalBytes} B", lst.Count);
                    }
                }
                break;

            case PathKind.HeapByGenerationTypesFolder when info.Generation != null:
                if (Store.HeapTypesByGeneration.TryGetValue(info.Generation, out var types))
                {
                    foreach (var t in types)
                    {
                        if (Stopping) return;
                        var tName = $"{SanitizeFull(t.TypeName)}.json";
                        var tPath = MakePath(path, tName);
                        WriteItemObject(new HeapTypeItem
                        {
                            Name = tName,
                            TypeName = t.TypeName,
                            InstanceCount = t.InstanceCount,
                            TotalBytes = t.TotalBytes,
                            Path = EnsureDrivePrefix(tPath),
                            Directory = dir,
                        }, tPath, isContainer: false);
                    }
                }
                break;
        }
    }

    protected override string GetFileText(string normalizedPath)
    {
        var info = Parse(normalizedPath);
        return info.Kind switch
        {
            PathKind.Summary => JsonSerializer.Serialize(Store.Summary, TraceJson.Options),
            PathKind.Analyze => Store.AnalyzeOutput,
            PathKind.Triage  => RenderTriage(),
            PathKind.ThreadInfo when info.ThreadId is int tid
                => Store.Threads.FirstOrDefault(x => x.ManagedThreadId == tid) is { } t
                    ? JsonSerializer.Serialize(t, TraceJson.Options) : "{}",
            PathKind.ThreadStack when info.ThreadId is int tid2 => RenderThreadStack(tid2),
            PathKind.ThreadRegisters when info.ThreadId is int tidR => Store.RenderRegistersForThread(tidR),
            PathKind.FrameFile when info.ThreadId is int tid3 && info.FrameIndex is int fi
                => Store.Threads.FirstOrDefault(x => x.ManagedThreadId == tid3) is { } t2
                    && fi >= 0 && fi < t2.Frames.Count
                    ? JsonSerializer.Serialize(t2.Frames[fi], TraceJson.Options) : "{}",
            PathKind.ModuleFile when info.ModuleFile != null
                => Store.Modules.FirstOrDefault(x => Sanitize(x.FileName)
                    .Equals(info.ModuleFile, StringComparison.OrdinalIgnoreCase)) is { } m
                    ? JsonSerializer.Serialize(m, TraceJson.Options) : "{}",
            PathKind.ThreadException when info.ThreadId is int tidExp
                => Store.Threads.FirstOrDefault(x => x.ManagedThreadId == tidExp)?.CurrentException is { } ex
                    ? JsonSerializer.Serialize(ex, TraceJson.Options) : "{}",
            PathKind.HeapTypeFile when info.TypeName != null
                => Store.HeapTypes.FirstOrDefault(t =>
                    SanitizeFull(t.TypeName).Equals(info.TypeName, StringComparison.Ordinal)) is { } stats
                    ? JsonSerializer.Serialize(stats, TraceJson.Options) : "{}",
            PathKind.HeapByGenerationTypeFile when info.Generation != null && info.TypeName != null
                => Store.HeapTypesByGeneration.TryGetValue(info.Generation, out var glst)
                    && glst.FirstOrDefault(t => SanitizeFull(t.TypeName)
                        .Equals(info.TypeName, StringComparison.Ordinal)) is { } gstats
                    ? JsonSerializer.Serialize(gstats, TraceJson.Options) : "{}",
            _ => "",
        };
    }

    /// <summary>One-page Markdown answering "what happened?" from the data we
    /// can gather cheaply: Summary (metadata), Threads (walked), Modules
    /// (enumerated). Deliberately avoids !analyze -v and HeapTypes because both
    /// can take 10+ seconds; the reader can still reach them via analyze.txt
    /// and heap\types\.</summary>
    private string RenderTriage()
    {
        var s = Store.Summary;
        var sb = new StringBuilder();
        sb.Append("# Dump Triage: ").AppendLine(Path.GetFileName(s.FilePath));
        sb.AppendLine();
        sb.Append("**Dump:** `").Append(s.FilePath).Append("` (")
          .Append(FormatBytes(s.FileSizeBytes)).AppendLine(")");
        sb.Append("**Architecture:** ").AppendLine(s.Architecture);
        if (!string.IsNullOrEmpty(s.ClrVersion))
            sb.Append("**CLR:** ").Append(s.ClrVersion)
              .Append(" (").Append(s.ClrFlavor).AppendLine(")");
        else
            sb.AppendLine("**CLR:** none (native-only dump)");
        sb.Append("**Threads:** ").Append(s.ThreadCount).AppendLine();
        sb.Append("**Modules:** ").Append(s.ModuleCount).AppendLine();
        sb.AppendLine();

        // Threads carrying a managed exception are the primary "what broke?"
        // signal on a CLR dump. Non-managed crashes surface via !analyze -v.
        var withExc = Store.Threads.Where(t => t.CurrentException != null).ToList();
        sb.Append("## Threads with active managed exceptions (")
          .Append(withExc.Count).AppendLine(")");
        sb.AppendLine();
        if (withExc.Count == 0)
        {
            sb.AppendLine("No thread has a current managed exception. If the");
            sb.AppendLine("process crashed natively (AV / stack overflow / etc.),");
            sb.AppendLine("read `analyze.txt` — it runs `!analyze -v` on first");
            sb.AppendLine("access and caches the result.");
        }
        else
        {
            foreach (var t in withExc.Take(10))
            {
                var ex = t.CurrentException!;
                sb.Append("- `threads\\").Append(t.ManagedThreadId).Append("\\` — **")
                  .Append(ex.TypeName).Append("**");
                if (!string.IsNullOrEmpty(ex.Message))
                    sb.Append(": ").Append(ex.Message);
                sb.AppendLine();
                sb.Append("  HResult: 0x").Append(ex.HResult.ToString("X8"))
                  .Append(", OS thread 0x").Append(t.OSThreadId.ToString("X"))
                  .Append(", ").Append(t.Frames.Count).AppendLine(" frames");
            }
            if (withExc.Count > 10)
                sb.Append("- ... (+").Append(withExc.Count - 10).AppendLine(" more)");
        }
        sb.AppendLine();

        // Aggregate thread counts — lets readers spot "everyone's parked in a
        // wait" vs "one GC thread stuck" at a glance.
        var alive      = Store.Threads.Count(t => t.IsAlive);
        var finalizer  = Store.Threads.Count(t => t.IsFinalizer);
        var gcThreads  = Store.Threads.Count(t => t.IsGC);
        var coop       = Store.Threads.Count(t => t.GCMode == "Cooperative");
        var preemptive = Store.Threads.Count(t => t.GCMode == "Preemptive");

        sb.AppendLine("## Thread summary");
        sb.AppendLine();
        sb.Append("- **Alive:** ").Append(alive).Append(" / ")
          .Append(Store.Threads.Count).AppendLine();
        if (finalizer > 0) sb.Append("- **Finalizer:** ").Append(finalizer).AppendLine();
        if (gcThreads > 0) sb.Append("- **GC:** ").Append(gcThreads).AppendLine();
        if (coop + preemptive > 0)
            sb.Append("- **GCMode:** Cooperative=").Append(coop)
              .Append(", Preemptive=").Append(preemptive).AppendLine();
        sb.AppendLine();

        // Top-5 modules by size gives a quick sense of what's loaded without
        // dumping all 200+ modules into the triage. Full list is in modules\.
        var topModules = Store.Modules
            .Where(m => m.Size > 0)
            .OrderByDescending(m => m.Size)
            .Take(5)
            .ToList();
        if (topModules.Count > 0)
        {
            sb.AppendLine("## Top modules by size");
            sb.AppendLine();
            foreach (var m in topModules)
                sb.Append("- `").Append(m.FileName).Append("` (")
                  .Append(FormatBytes(m.Size)).AppendLine(")");
            sb.AppendLine();
        }

        sb.AppendLine("## Where to look next");
        sb.AppendLine();
        sb.AppendLine("- `threads\\<id>\\` — per-thread info, stack, registers, exception");
        sb.AppendLine("- `threads\\<id>\\frames\\<n>.json` — individual stack frames " +
                      "(source location when resolvable)");
        sb.AppendLine("- `threads\\<id>\\stack.txt` — full formatted stack trace");
        if (withExc.Count > 0)
            sb.AppendLine("- `threads\\with-exception\\` — jump to threads that hold an exception");
        sb.AppendLine("- `threads\\by-state\\{finalizer,gc,dead}\\` — filter by thread state");
        sb.AppendLine("- `modules\\` — native + managed modules (unified view)");
        sb.AppendLine("- `modules\\by-kind\\{native,managed}\\` — split by PE vs CLR");
        sb.AppendLine("- `heap\\types\\` — GC heap instance counts per type " +
                      "(first access walks the heap; can take seconds)");
        sb.AppendLine("- `heap\\by-generation\\{gen0,gen1,gen2,loh,…}\\` — " +
                      "heap types bucketed by GC generation (same heap walk)");
        sb.AppendLine("- `analyze.txt` — `!analyze -v` output " +
                      "(first read runs dbgeng analysis; can take 10+ seconds)");
        sb.AppendLine("- `Invoke-CrashCommand` — raw dbgeng escape hatch " +
                      "(e.g. `!locks`, `!syncblk`, `dx`)");
        return sb.ToString();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";
    }

    private string RenderThreadStack(int managedThreadId)
    {
        var t = Store.Threads.FirstOrDefault(x => x.ManagedThreadId == managedThreadId);
        if (t == null) return "";
        var lines = new List<string>
        {
            $"Thread #{t.ManagedThreadId} (OS thread {t.OSThreadId})",
            $"  GCMode={t.GCMode}, IsAlive={t.IsAlive}, IsFinalizer={t.IsFinalizer}",
        };
        if (t.CurrentException != null)
        {
            lines.Add("");
            lines.Add($"Current exception: {t.CurrentException.TypeName}");
            lines.Add($"  Message: {t.CurrentException.Message}");
            lines.Add($"  HResult: 0x{t.CurrentException.HResult:X8}");
        }
        lines.Add("");
        lines.Add($"Stack ({t.Frames.Count} frames):");
        for (int i = 0; i < t.Frames.Count; i++)
        {
            var f = t.Frames[i];
            lines.Add($"  [{i,3}] {f.Kind,-10} 0x{f.InstructionPointer:X16}  {f.Method ?? "<native>"}");
            if (!string.IsNullOrEmpty(f.Module)) lines.Add($"          module: {f.Module}");
        }
        return string.Join("\n", lines);
    }

    private static string Sanitize(string name)
    {
        if (string.IsNullOrEmpty(name)) return "unknown";
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_');
        return sb.ToString();
    }

    /// <summary>More permissive sanitizer for CLR type names (keeps +, &lt;, &gt;, etc.
    /// replaced by markers to stay path-safe while reversible via HeapTypes lookup).</summary>
    private static string SanitizeFull(string name)
    {
        if (string.IsNullOrEmpty(name)) return "unknown";
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' or '+' or ',' or ' ')
                sb.Append(ch);
            else
                sb.Append('_');
        }
        return sb.ToString();
    }

    /// <summary>Shared emit for a module row — used by the flat modules\
    /// listing and by modules\by-kind\&lt;kind&gt;\.</summary>
    private void WriteModuleItem(DumpModuleInfo m, string itemPath, string dir)
    {
        WriteItemObject(new ModuleItem
        {
            Name = m.Name,
            FileName = m.FileName,
            Size = m.Size,
            ImageBaseHex = $"0x{m.ImageBase:X16}",
            IsDynamic = m.IsDynamic,
            IsManaged = m.IsManaged,
            Path = EnsureDrivePrefix(itemPath),
            Directory = dir,
        }, itemPath, isContainer: false);
    }

    /// <summary>Shared emit for a thread row — used by the flat
    /// threads\ listing and by the two classifier listings
    /// (with-exception, by-state\&lt;s&gt;\).</summary>
    private void WriteThreadItem(DumpThreadInfo t, string path, string dir)
    {
        var tPath = MakePath(path, t.ManagedThreadId.ToString());
        WriteItemObject(new ThreadItem
        {
            ManagedThreadId = t.ManagedThreadId,
            OSThreadId = t.OSThreadId,
            GCMode = t.GCMode,
            IsAlive = t.IsAlive,
            IsFinalizer = t.IsFinalizer,
            FrameCount = t.Frames.Count,
            ExceptionSummary = t.CurrentException != null
                ? $"{t.CurrentException.TypeName}: {t.CurrentException.Message}" : null,
            Path = EnsureDrivePrefix(tPath),
            Directory = dir,
        }, tPath, isContainer: true);
    }

    private void WriteFolder(string name, string itemPath, string directory, string desc, int? count)
    {
        WriteItemObject(new FolderItem
        {
            Name = name,
            Path = EnsureDrivePrefix(itemPath),
            Directory = directory,
            Description = desc,
            Count = count,
        }, itemPath, isContainer: true);
    }

    private void WriteFile(string name, string itemPath, string directory)
    {
        WriteItemObject(new FileItem
        {
            Name = name,
            Path = EnsureDrivePrefix(itemPath),
            Directory = directory,
        }, itemPath, isContainer: false);
    }
}
