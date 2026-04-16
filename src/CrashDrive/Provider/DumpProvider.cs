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
        Root, Summary, Analyze,
        ThreadsFolder, ThreadFolder, ThreadInfo, ThreadStack, ThreadException, ThreadRegisters,
        ThreadFramesFolder, FrameFile,
        ModulesFolder, ModuleFile,
        HeapFolder, HeapTypesFolder, HeapTypeFile,
        Invalid,
    }

    private readonly record struct ParsedPath(
        PathKind Kind,
        string[] Segments,
        int? ThreadId = null,
        int? FrameIndex = null,
        string? ModuleFile = null,
        string? TypeName = null);

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
                "threads" => new(PathKind.ThreadsFolder, segs),
                "modules" => new(PathKind.ModulesFolder, segs),
                "heap" => new(PathKind.HeapFolder, segs),
                _ => new(PathKind.Invalid, segs),
            };
        }

        if (head == "threads")
        {
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
        if (head == "modules" && segs.Length == 2
            && segs[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return new(PathKind.ModuleFile, segs, ModuleFile: segs[1][..^5]);

        if (head == "heap")
        {
            if (segs.Length == 2 && segs[1] == "types")
                return new(PathKind.HeapTypesFolder, segs);
            if (segs.Length == 3 && segs[1] == "types"
                && segs[2].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                return new(PathKind.HeapTypeFile, segs, TypeName: segs[2][..^5]);
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
            _ => true,
        };
    }

    protected override bool IsItemContainer(string path)
    {
        return Parse(NormalizePath(path)).Kind switch
        {
            PathKind.Root or PathKind.ThreadsFolder or PathKind.ThreadFolder or
            PathKind.ThreadFramesFolder or PathKind.ModulesFolder or
            PathKind.HeapFolder or PathKind.HeapTypesFolder => true,
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

            case PathKind.Summary or PathKind.Analyze or PathKind.ThreadInfo or PathKind.ThreadStack
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
                yield return ("summary.json", false);
                yield return ("analyze.txt", false);
                yield return ("threads", true);
                yield return ("modules", true);
                yield return ("heap", true);
                break;
            case PathKind.HeapFolder:
                yield return ("types", true);
                break;
            case PathKind.HeapTypesFolder:
                foreach (var t in Store.HeapTypes)
                    yield return ($"{SanitizeFull(t.TypeName)}.json", false);
                break;
            case PathKind.ThreadsFolder:
                foreach (var t in Store.Threads)
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
                foreach (var m in Store.Modules)
                    yield return ($"{Sanitize(m.FileName)}.json", false);
                break;
        }
    }

    private void WriteChildren(ParsedPath info, string path)
    {
        var dir = EnsureDrivePrefix(path);
        switch (info.Kind)
        {
            case PathKind.Root:
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
                foreach (var t in Store.Threads)
                {
                    if (Stopping) return;
                    var tPath = MakePath(path, t.ManagedThreadId.ToString());
                    var item = new ThreadItem
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
                    };
                    WriteItemObject(item, tPath, isContainer: true);
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
                foreach (var m in Store.Modules)
                {
                    if (Stopping) return;
                    var mPath = MakePath(path, $"{Sanitize(m.FileName)}.json");
                    WriteItemObject(new ModuleItem
                    {
                        Name = m.Name,
                        FileName = m.FileName,
                        Size = m.Size,
                        ImageBaseHex = $"0x{m.ImageBase:X16}",
                        IsDynamic = m.IsDynamic,
                        IsManaged = m.IsManaged,
                        Path = EnsureDrivePrefix(mPath),
                        Directory = dir,
                    }, mPath, isContainer: false);
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
            _ => "",
        };
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
