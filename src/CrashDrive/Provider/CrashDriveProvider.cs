using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Provider;
using System.Text.Json;
using CrashDrive.Models;
using CrashDrive.Trace;

namespace CrashDrive.Provider;

[CmdletProvider("CrashDrive", ProviderCapabilities.None)]
[OutputType(typeof(FolderItem), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
[OutputType(typeof(EventItem), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
[OutputType(typeof(ExceptionItem), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
public sealed class CrashDriveProvider : NavigationCmdletProvider, IContentCmdletProvider
{
    private CrashDriveInfo Drive => (CrashDriveInfo)PSDriveInfo;

    // === Drive lifecycle ===

    protected override object NewDriveDynamicParameters() => new NewCrashDriveDynamicParameters();

    protected override PSDriveInfo NewDrive(PSDriveInfo drive)
    {
        if (drive is CrashDriveInfo existing) return existing;

        var dyn = DynamicParameters as NewCrashDriveDynamicParameters
            ?? throw new PSArgumentException(
                "CrashDrive requires -File. Use: New-PSDrive -PSProvider CrashDrive -Name <name> -Root \\ -File <path>");

        string absFile;
        try
        {
            absFile = Path.GetFullPath(dyn.File);
        }
        catch (Exception ex)
        {
            throw new PSArgumentException($"Invalid -File path: {ex.Message}", ex);
        }

        if (!File.Exists(absFile))
            throw new PSArgumentException($"File not found: {absFile}");

        var displayRoot = $"crash://{Path.GetFileName(absFile)}";
        var inner = new PSDriveInfo(drive.Name, drive.Provider, @"\",
            drive.Description ?? "", drive.Credential);

        return new CrashDriveInfo(inner, displayRoot, absFile, dyn.SymbolPath);
    }

    protected override PSDriveInfo RemoveDrive(PSDriveInfo drive) => drive;

    // === Path operations ===

    protected override bool IsValidPath(string path) => true;

    protected override bool ItemExists(string path)
    {
        var info = CrashPathParser.Parse(NormalizePath(path));
        if (info.Type == CrashPathType.Invalid) return false;

        // For specific items, confirm the underlying data exists
        return info.Type switch
        {
            CrashPathType.EventFile => info.Seq.HasValue && Drive.Trace.BySeq.ContainsKey(info.Seq.Value),
            CrashPathType.ByTypeCategoryFolder => info.Category != null && Drive.Trace.ByType.ContainsKey(info.Category),
            CrashPathType.ByTypeEventFile => info.Category != null && info.Seq.HasValue
                && Drive.Trace.ByType.TryGetValue(info.Category, out var bt) && bt.Any(e => e.Seq == info.Seq),
            CrashPathType.ByFunctionCategoryFolder => info.Category != null && Drive.Trace.ByFunction.ContainsKey(info.Category),
            CrashPathType.ByFunctionEventFile => info.Category != null && info.Seq.HasValue
                && Drive.Trace.ByFunction.TryGetValue(info.Category, out var bf) && bf.Any(e => e.Seq == info.Seq),
            CrashPathType.ExceptionFolder => info.ExceptionIndex.HasValue
                && info.ExceptionIndex.Value >= 1 && info.ExceptionIndex.Value <= Drive.Trace.Exceptions.Count,
            CrashPathType.ExceptionEventFile
                or CrashPathType.ExceptionContextFile
                or CrashPathType.ExceptionStackFile => info.ExceptionIndex.HasValue
                && info.ExceptionIndex.Value >= 1 && info.ExceptionIndex.Value <= Drive.Trace.Exceptions.Count,
            _ => true,
        };
    }

    protected override bool IsItemContainer(string path)
    {
        var info = CrashPathParser.Parse(NormalizePath(path));
        return info.IsContainer;
    }

    protected override bool HasChildItems(string path)
    {
        var info = CrashPathParser.Parse(NormalizePath(path));
        return info.IsContainer;
    }

    protected override void GetItem(string path)
    {
        var info = CrashPathParser.Parse(NormalizePath(path));
        var parentPath = GetParentPath(path, null);
        var directory = EnsureDrivePrefix(parentPath);

        switch (info.Type)
        {
            case CrashPathType.Root:
                WriteFolder(parentPath, Drive.Name, path, directory, "root", null);
                return;
            case CrashPathType.SummaryFile:
            case CrashPathType.StdoutFile:
            case CrashPathType.StderrFile:
                WriteFile(Path.GetFileName(info.Segments[^1]), path, directory);
                return;
            case CrashPathType.EventFile:
            case CrashPathType.ByTypeEventFile:
            case CrashPathType.ByFunctionEventFile:
                if (info.Seq is int seq && Drive.Trace.BySeq.TryGetValue(seq, out var ev))
                {
                    WriteEvent(ev, path, directory);
                }
                return;
            case CrashPathType.ExceptionFolder:
                if (info.ExceptionIndex is int xidx && xidx >= 1 && xidx <= Drive.Trace.Exceptions.Count)
                {
                    var rec = Drive.Trace.Exceptions[xidx - 1];
                    WriteException(rec, path, directory);
                }
                return;
            default:
                WriteFolder(parentPath, LastSegment(info), path, directory, info.Type.ToString(), null);
                return;
        }
    }

    // === Children ===

    protected override void GetChildItems(string path, bool recurse)
    {
        var info = CrashPathParser.Parse(NormalizePath(path));
        try
        {
            WriteChildren(info, path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WriteError(new ErrorRecord(ex, "GetChildError", ErrorCategory.NotSpecified, path));
        }
    }

    protected override void GetChildNames(string path, ReturnContainers returnContainers)
    {
        var info = CrashPathParser.Parse(NormalizePath(path));
        try
        {
            foreach (var (name, isContainer) in EnumerateChildNames(info))
            {
                if (Stopping) return;
                var escaped = WildcardPattern.ContainsWildcardCharacters(name)
                    ? WildcardPattern.Escape(name) : name;
                WriteItemObject(escaped, MakePath(path, name), isContainer);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"GetChildNames: {ex.Message}");
        }
    }

    private IEnumerable<(string name, bool isContainer)> EnumerateChildNames(CrashPathInfo info)
    {
        switch (info.Type)
        {
            case CrashPathType.Root:
                yield return ("summary.json", false);
                if (Drive.Store.Kind == Store.StoreKind.Trace)
                {
                    yield return ("stdout.txt", false);
                    yield return ("stderr.txt", false);
                    yield return ("events", true);
                    yield return ("by-type", true);
                    yield return ("by-function", true);
                    yield return ("exceptions", true);
                }
                else if (Drive.Store.Kind == Store.StoreKind.Dump)
                {
                    yield return ("info.json", false);
                    yield return ("threads", true);
                    yield return ("modules", true);
                }
                break;

            case CrashPathType.ThreadsFolder:
                if (Drive.AsDump is { } dAll)
                    foreach (var t in dAll.Threads)
                        yield return (t.ManagedThreadId.ToString(), true);
                break;

            case CrashPathType.ThreadFolder:
                yield return ("info.json", false);
                yield return ("stack.txt", false);
                yield return ("frames", true);
                break;

            case CrashPathType.ThreadFramesFolder:
                if (Drive.AsDump is { } dFr && info.ThreadId is int tid1)
                {
                    var thread = dFr.Threads.FirstOrDefault(t => t.ManagedThreadId == tid1);
                    if (thread != null)
                        for (int i = 0; i < thread.Frames.Count; i++)
                            yield return ($"{i}.json", false);
                }
                break;

            case CrashPathType.ModulesFolder:
                if (Drive.AsDump is { } dM)
                    foreach (var m in dM.Modules)
                        yield return ($"{SanitizeFileName(m.FileName)}.json", false);
                break;

            case CrashPathType.EventsFolder:
                foreach (var ev in Drive.Trace.Events)
                    yield return ($"{ev.Seq}.json", false);
                break;

            case CrashPathType.ByTypeFolder:
                foreach (var key in Drive.Trace.ByType.Keys.OrderBy(k => k))
                    yield return (key, true);
                break;

            case CrashPathType.ByTypeCategoryFolder:
                if (info.Category != null && Drive.Trace.ByType.TryGetValue(info.Category, out var bt))
                    foreach (var ev in bt) yield return ($"{ev.Seq}.json", false);
                break;

            case CrashPathType.ByFunctionFolder:
                foreach (var key in Drive.Trace.ByFunction.Keys.OrderBy(k => k))
                    yield return (key, true);
                break;

            case CrashPathType.ByFunctionCategoryFolder:
                if (info.Category != null && Drive.Trace.ByFunction.TryGetValue(info.Category, out var bf))
                    foreach (var ev in bf) yield return ($"{ev.Seq}.json", false);
                break;

            case CrashPathType.ExceptionsFolder:
                foreach (var rec in Drive.Trace.Exceptions)
                    yield return (rec.Index.ToString(), true);
                break;

            case CrashPathType.ExceptionFolder:
                yield return ("event.json", false);
                yield return ("context.json", false);
                yield return ("stack.txt", false);
                break;
        }
    }

    private void WriteChildren(CrashPathInfo info, string path)
    {
        var directory = EnsureDrivePrefix(path);
        switch (info.Type)
        {
            case CrashPathType.Root:
                WriteFile("summary.json", MakePath(path, "summary.json"), directory);
                if (Drive.Store.Kind == Store.StoreKind.Trace)
                {
                    WriteFile("stdout.txt", MakePath(path, "stdout.txt"), directory);
                    WriteFile("stderr.txt", MakePath(path, "stderr.txt"), directory);
                    WriteFolder(path, "events", MakePath(path, "events"), directory,
                        "all events by sequence", Drive.Trace.Summary.TotalEvents);
                    WriteFolder(path, "by-type", MakePath(path, "by-type"), directory,
                        "events grouped by type", Drive.Trace.ByType.Count);
                    WriteFolder(path, "by-function", MakePath(path, "by-function"), directory,
                        "events grouped by function", Drive.Trace.ByFunction.Count);
                    WriteFolder(path, "exceptions", MakePath(path, "exceptions"), directory,
                        "exception occurrences with context", Drive.Trace.Exceptions.Count);
                }
                else if (Drive.AsDump is { } rd)
                {
                    WriteFile("info.json", MakePath(path, "info.json"), directory);
                    WriteFolder(path, "threads", MakePath(path, "threads"), directory,
                        $"threads at time of dump", rd.Summary.ThreadCount);
                    WriteFolder(path, "modules", MakePath(path, "modules"), directory,
                        "loaded modules", rd.Summary.ModuleCount);
                }
                break;

            case CrashPathType.ThreadsFolder:
                if (Drive.AsDump is { } threadsDump)
                {
                    foreach (var t in threadsDump.Threads)
                    {
                        if (Stopping) return;
                        var tPath = MakePath(path, t.ManagedThreadId.ToString());
                        var item = new Models.ThreadItem
                        {
                            ManagedThreadId = t.ManagedThreadId,
                            OSThreadId = t.OSThreadId,
                            GCMode = t.GCMode,
                            IsAlive = t.IsAlive,
                            IsFinalizer = t.IsFinalizer,
                            FrameCount = t.Frames.Count,
                            ExceptionSummary = t.CurrentException != null
                                ? $"{t.CurrentException.TypeName}: {t.CurrentException.Message}"
                                : null,
                            Path = EnsureDrivePrefix(tPath),
                            Directory = directory,
                        };
                        WriteItemObject(item, tPath, isContainer: true);
                    }
                }
                break;

            case CrashPathType.ThreadFolder:
                WriteFile("info.json", MakePath(path, "info.json"), directory);
                WriteFile("stack.txt", MakePath(path, "stack.txt"), directory);
                WriteFolder(path, "frames", MakePath(path, "frames"), directory,
                    "stack frames", null);
                break;

            case CrashPathType.ThreadFramesFolder:
                if (Drive.AsDump is { } framesDump && info.ThreadId is int tidFr)
                {
                    var thread = framesDump.Threads.FirstOrDefault(t => t.ManagedThreadId == tidFr);
                    if (thread != null)
                    {
                        for (int i = 0; i < thread.Frames.Count; i++)
                        {
                            if (Stopping) return;
                            var f = thread.Frames[i];
                            var fPath = MakePath(path, $"{i}.json");
                            var item = new Models.FrameItem
                            {
                                Index = i,
                                Method = f.Method ?? "<unknown>",
                                Module = f.Module,
                                Kind = f.Kind,
                                IpHex = $"0x{f.InstructionPointer:X16}",
                                Path = EnsureDrivePrefix(fPath),
                                Directory = directory,
                            };
                            WriteItemObject(item, fPath, isContainer: false);
                        }
                    }
                }
                break;

            case CrashPathType.ModulesFolder:
                if (Drive.AsDump is { } modDump)
                {
                    foreach (var m in modDump.Modules)
                    {
                        if (Stopping) return;
                        var mPath = MakePath(path, $"{SanitizeFileName(m.FileName)}.json");
                        var item = new Models.ModuleItem
                        {
                            Name = m.Name,
                            FileName = m.FileName,
                            Size = m.Size,
                            ImageBaseHex = $"0x{m.ImageBase:X16}",
                            IsDynamic = m.IsDynamic,
                            Path = EnsureDrivePrefix(mPath),
                            Directory = directory,
                        };
                        WriteItemObject(item, mPath, isContainer: false);
                    }
                }
                break;

            case CrashPathType.EventsFolder:
                foreach (var ev in Drive.Trace.Events)
                {
                    if (Stopping) return;
                    WriteEvent(ev, MakePath(path, $"{ev.Seq}.json"), directory);
                }
                break;

            case CrashPathType.ByTypeFolder:
                foreach (var kv in Drive.Trace.ByType.OrderBy(kv => kv.Key))
                {
                    if (Stopping) return;
                    WriteFolder(path, kv.Key, MakePath(path, kv.Key), directory,
                        $"{kv.Value.Count} events", kv.Value.Count);
                }
                break;

            case CrashPathType.ByTypeCategoryFolder:
                if (info.Category != null && Drive.Trace.ByType.TryGetValue(info.Category, out var bt))
                {
                    foreach (var ev in bt)
                    {
                        if (Stopping) return;
                        WriteEvent(ev, MakePath(path, $"{ev.Seq}.json"), directory);
                    }
                }
                break;

            case CrashPathType.ByFunctionFolder:
                foreach (var kv in Drive.Trace.ByFunction.OrderBy(kv => kv.Key))
                {
                    if (Stopping) return;
                    WriteFolder(path, kv.Key, MakePath(path, kv.Key), directory,
                        $"{kv.Value.Count} events", kv.Value.Count);
                }
                break;

            case CrashPathType.ByFunctionCategoryFolder:
                if (info.Category != null && Drive.Trace.ByFunction.TryGetValue(info.Category, out var bf))
                {
                    foreach (var ev in bf)
                    {
                        if (Stopping) return;
                        WriteEvent(ev, MakePath(path, $"{ev.Seq}.json"), directory);
                    }
                }
                break;

            case CrashPathType.ExceptionsFolder:
                foreach (var rec in Drive.Trace.Exceptions)
                {
                    if (Stopping) return;
                    var xPath = MakePath(path, rec.Index.ToString());
                    var item = new ExceptionItem
                    {
                        Index = rec.Index,
                        Seq = rec.Event.Seq,
                        ExceptionType = rec.Event.Exception ?? "",
                        Message = rec.Event.Message ?? "",
                        Function = rec.Event.Function,
                        Line = rec.Event.Line,
                        Path = EnsureDrivePrefix(xPath),
                        Directory = directory,
                    };
                    WriteItemObject(item, xPath, isContainer: true);
                }
                break;

            case CrashPathType.ExceptionFolder:
                WriteFile("event.json", MakePath(path, "event.json"), directory);
                WriteFile("context.json", MakePath(path, "context.json"), directory);
                WriteFile("stack.txt", MakePath(path, "stack.txt"), directory);
                break;
        }
    }

    // === IContentCmdletProvider: Get-Content for JSON/text files ===

    public IContentReader GetContentReader(string path)
    {
        var info = CrashPathParser.Parse(NormalizePath(path));
        var text = GetFileText(info);
        return new StringContentReader(text);
    }

    public object? GetContentReaderDynamicParameters(string path) => null;
    public IContentWriter GetContentWriter(string path) => throw new NotSupportedException("CrashDrive is read-only.");
    public object? GetContentWriterDynamicParameters(string path) => null;
    public void ClearContent(string path) => throw new NotSupportedException("CrashDrive is read-only.");
    public object? ClearContentDynamicParameters(string path) => null;

    private string GetFileText(CrashPathInfo info)
    {
        switch (info.Type)
        {
            case CrashPathType.SummaryFile:
                if (Drive.Store.Kind == Store.StoreKind.Trace)
                    return JsonSerializer.Serialize(Drive.Trace.Summary, TraceJson.Options);
                if (Drive.AsDump is { } dSum)
                    return JsonSerializer.Serialize(dSum.Summary, TraceJson.Options);
                return "{}";

            case CrashPathType.DumpInfoFile:
                if (Drive.AsDump is { } dInfo)
                    return JsonSerializer.Serialize(dInfo.Summary, TraceJson.Options);
                return "{}";

            case CrashPathType.ThreadInfoFile:
                if (Drive.AsDump is { } dTh && info.ThreadId is int tidI)
                {
                    var t = dTh.Threads.FirstOrDefault(x => x.ManagedThreadId == tidI);
                    if (t != null) return JsonSerializer.Serialize(t, TraceJson.Options);
                }
                return "{}";

            case CrashPathType.ThreadStackFile:
                if (Drive.AsDump is { } dSt && info.ThreadId is int tidS)
                    return RenderThreadStack(dSt, tidS);
                return "";

            case CrashPathType.FrameFile:
                if (Drive.AsDump is { } dF && info.ThreadId is int tidF && info.FrameIndex is int fi)
                {
                    var t = dF.Threads.FirstOrDefault(x => x.ManagedThreadId == tidF);
                    if (t != null && fi >= 0 && fi < t.Frames.Count)
                        return JsonSerializer.Serialize(t.Frames[fi], TraceJson.Options);
                }
                return "{}";

            case CrashPathType.ModuleFile:
                if (Drive.AsDump is { } dMod && info.ModuleFile != null)
                {
                    var m = dMod.Modules.FirstOrDefault(x =>
                        SanitizeFileName(x.FileName).Equals(info.ModuleFile, StringComparison.OrdinalIgnoreCase));
                    if (m != null) return JsonSerializer.Serialize(m, TraceJson.Options);
                }
                return "{}";

            case CrashPathType.StdoutFile:
                // Placeholder — wake currently doesn't capture stdout separately from events.
                return "";
            case CrashPathType.StderrFile:
                return "";

            case CrashPathType.EventFile:
            case CrashPathType.ByTypeEventFile:
            case CrashPathType.ByFunctionEventFile:
                if (info.Seq is int seq && Drive.Trace.BySeq.TryGetValue(seq, out var ev))
                    return JsonSerializer.Serialize(ev, TraceJson.Options);
                break;

            case CrashPathType.ExceptionEventFile:
                if (info.ExceptionIndex is int xi1 && TryGetException(xi1, out var rec1))
                    return JsonSerializer.Serialize(rec1!.Event, TraceJson.Options);
                break;

            case CrashPathType.ExceptionContextFile:
                if (info.ExceptionIndex is int xi2 && TryGetException(xi2, out var rec2))
                    return JsonSerializer.Serialize(rec2!.Context, TraceJson.Options);
                break;

            case CrashPathType.ExceptionStackFile:
                if (info.ExceptionIndex is int xi3 && TryGetException(xi3, out var rec3))
                    return RenderStack(rec3!);
                break;
        }
        return "";
    }

    private bool TryGetException(int idx, out ExceptionRecord? rec)
    {
        rec = null;
        if (idx < 1 || idx > Drive.Trace.Exceptions.Count) return false;
        rec = Drive.Trace.Exceptions[idx - 1];
        return true;
    }

    private static string RenderStack(ExceptionRecord rec)
    {
        // A simple reverse-chronological stack from the context's call events leading to the exception
        var lines = new List<string>();
        lines.Add($"Exception #{rec.Index}: {rec.Event.Exception}: {rec.Event.Message}");
        lines.Add($"Raised at {rec.Event.File}:{rec.Event.Line} in {rec.Event.Function}");
        if (rec.Event.Locals != null && rec.Event.Locals.Count > 0)
        {
            lines.Add("");
            lines.Add("Locals at throw site:");
            foreach (var kv in rec.Event.Locals)
                lines.Add($"  {kv.Key} = {kv.Value}");
        }
        lines.Add("");
        lines.Add($"Context (±{(rec.Context.Count - 1) / 2} events):");
        foreach (var c in rec.Context)
        {
            var marker = c.Seq == rec.Event.Seq ? ">>>" : "   ";
            lines.Add($"{marker} [#{c.Seq,4} d={c.Depth} L{c.Line}] {c.Type,-10} {c.Function}");
        }
        return string.Join("\n", lines);
    }

    private sealed class StringContentReader : IContentReader
    {
        private readonly string _text;
        private bool _read;

        public StringContentReader(string text) => _text = text;

        public IList Read(long readCount)
        {
            if (_read) return new System.Collections.ArrayList();
            _read = true;
            return new System.Collections.ArrayList { _text };
        }

        public void Seek(long offset, SeekOrigin origin) { /* no-op */ }
        public void Close() { }
        public void Dispose() { }
    }

    // === Helpers ===

    private void WriteFolder(string parentPath, string name, string itemPath, string directory, string desc, int? count)
    {
        var item = new FolderItem
        {
            Name = name,
            Path = EnsureDrivePrefix(itemPath),
            Directory = directory,
            Description = desc,
            Count = count,
        };
        WriteItemObject(item, itemPath, isContainer: true);
    }

    private void WriteFile(string name, string itemPath, string directory)
    {
        var item = new FileItem
        {
            Name = name,
            Path = EnsureDrivePrefix(itemPath),
            Directory = directory,
        };
        WriteItemObject(item, itemPath, isContainer: false);
    }

    private void WriteEvent(TraceEvent ev, string itemPath, string directory)
    {
        var item = EventItem.From(ev, EnsureDrivePrefix(itemPath), directory);
        WriteItemObject(item, itemPath, isContainer: false);
    }

    private void WriteException(ExceptionRecord rec, string itemPath, string directory)
    {
        var item = new ExceptionItem
        {
            Index = rec.Index,
            Seq = rec.Event.Seq,
            ExceptionType = rec.Event.Exception ?? "",
            Message = rec.Event.Message ?? "",
            Function = rec.Event.Function,
            Line = rec.Event.Line,
            Path = EnsureDrivePrefix(itemPath),
            Directory = directory,
        };
        WriteItemObject(item, itemPath, isContainer: true);
    }

    protected override string MakePath(string parent, string child)
    {
        var result = base.MakePath(parent, child);
        if (result.EndsWith('\\') && result.Length > 1 && result[^2] != ':')
            result = result[..^1];
        return result;
    }

    protected override string NormalizeRelativePath(string path, string basePath)
    {
        var result = base.NormalizeRelativePath(path, basePath);
        if (result.StartsWith('\\') && result.Length > 1)
            result = result[1..];
        return result;
    }

    private string NormalizePath(string path)
    {
        var colonIdx = path.IndexOf(':');
        if (colonIdx > 0 && colonIdx + 1 < path.Length && path[colonIdx + 1] is '\\' or '/')
            path = path[(colonIdx + 1)..];
        return path.Replace('\\', '/').Trim('/');
    }

    private string EnsureDrivePrefix(string path)
    {
        if (PSDriveInfo == null) return path;
        var prefix = $"{PSDriveInfo.Name}:";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path : prefix + path;
    }

    private static string LastSegment(CrashPathInfo info)
        => info.Segments.Length > 0 ? info.Segments[^1] : "";

    /// <summary>Filesystem-safe version of a module filename for path segment use.</summary>
    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "unknown";
        var safe = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.') safe.Append(ch);
            else safe.Append('_');
        }
        return safe.ToString();
    }

    private static string RenderThreadStack(CrashDrive.Dump.DumpStore dump, int managedThreadId)
    {
        var t = dump.Threads.FirstOrDefault(x => x.ManagedThreadId == managedThreadId);
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
}
