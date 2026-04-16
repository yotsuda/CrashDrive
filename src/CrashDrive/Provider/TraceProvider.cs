using System.Management.Automation;
using System.Management.Automation.Provider;
using System.Text.Json;
using CrashDrive.Models;
using CrashDrive.Trace;

namespace CrashDrive.Provider;

/// <summary>
/// PSProvider "Trace" — mounts a JSONL execution trace file (produced by
/// wake-style language tracers) as a navigable filesystem.
///
/// Hierarchy:
/// <code>
///   &lt;drive&gt;:\
///     summary.json
///     stdout.txt
///     stderr.txt
///     events\&lt;seq&gt;.json
///     by-type\&lt;type&gt;\&lt;seq&gt;.json
///     by-function\&lt;fn&gt;\&lt;seq&gt;.json
///     exceptions\&lt;i&gt;\{event,context,stack}
/// </code>
/// </summary>
[CmdletProvider("Trace", ProviderCapabilities.None)]
[OutputType(typeof(FolderItem), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
[OutputType(typeof(EventItem), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
[OutputType(typeof(ExceptionItem), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
public sealed class TraceProvider : ProviderBase
{
    private TraceDriveInfo Drive => (TraceDriveInfo)PSDriveInfo;
    private TraceStore Store => Drive.Store;

    // === Drive lifecycle ===

    protected override object NewDriveDynamicParameters() => new NewCrashDriveDynamicParameters();

    protected override PSDriveInfo NewDrive(PSDriveInfo drive)
    {
        if (drive is TraceDriveInfo existing) return existing;

        var dyn = DynamicParameters as NewCrashDriveDynamicParameters
            ?? throw new PSArgumentException(
                "Trace provider requires -File. Use: New-PSDrive -PSProvider Trace -Name <name> -Root \\ -File <jsonl-path>");

        var absFile = Path.GetFullPath(dyn.File);
        if (!File.Exists(absFile))
            throw new PSArgumentException($"File not found: {absFile}");

        var inner = new PSDriveInfo(drive.Name, drive.Provider, @"\",
            drive.Description ?? "", drive.Credential);
        return new TraceDriveInfo(inner, absFile);
    }

    protected override PSDriveInfo RemoveDrive(PSDriveInfo drive) => drive;

    // === Path parsing (simple, inlined) ===

    private enum PathKind
    {
        Root, Summary,
        EventsFolder, EventFile,
        ByTypeFolder, ByTypeCategory, ByTypeEvent,
        ByFunctionFolder, ByFunctionCategory, ByFunctionEvent,
        ExceptionsFolder, ExceptionFolder, ExceptionEvent, ExceptionContext, ExceptionStack,
        Invalid,
    }

    private readonly record struct ParsedPath(
        PathKind Kind,
        string[] Segments,
        int? Seq = null,
        string? Category = null,
        int? ExceptionIndex = null);

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
                "events" => new(PathKind.EventsFolder, segs),
                "by-type" => new(PathKind.ByTypeFolder, segs),
                "by-function" => new(PathKind.ByFunctionFolder, segs),
                "exceptions" => new(PathKind.ExceptionsFolder, segs),
                _ => new(PathKind.Invalid, segs),
            };
        }

        return head switch
        {
            "events" when segs.Length == 2 && TryParseSeqFile(segs[1], out var s1)
                => new(PathKind.EventFile, segs, Seq: s1),
            "by-type" when segs.Length == 2
                => new(PathKind.ByTypeCategory, segs, Category: segs[1]),
            "by-type" when segs.Length == 3 && TryParseSeqFile(segs[2], out var s2)
                => new(PathKind.ByTypeEvent, segs, Category: segs[1], Seq: s2),
            "by-function" when segs.Length == 2
                => new(PathKind.ByFunctionCategory, segs, Category: segs[1]),
            "by-function" when segs.Length == 3 && TryParseSeqFile(segs[2], out var s3)
                => new(PathKind.ByFunctionEvent, segs, Category: segs[1], Seq: s3),
            "exceptions" when segs.Length == 2 && int.TryParse(segs[1], out var i1)
                => new(PathKind.ExceptionFolder, segs, ExceptionIndex: i1),
            "exceptions" when segs.Length == 3 && int.TryParse(segs[1], out var i2)
                => segs[2].ToLowerInvariant() switch
                {
                    "event.json" => new(PathKind.ExceptionEvent, segs, ExceptionIndex: i2),
                    "context.json" => new(PathKind.ExceptionContext, segs, ExceptionIndex: i2),
                    "stack.txt" => new(PathKind.ExceptionStack, segs, ExceptionIndex: i2),
                    _ => new(PathKind.Invalid, segs),
                },
            _ => new(PathKind.Invalid, segs),
        };
    }

    private static bool TryParseSeqFile(string segment, out int seq)
    {
        seq = 0;
        return segment.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(segment[..^5], out seq);
    }

    // === Path operations ===

    protected override bool ItemExists(string path)
    {
        var info = Parse(NormalizePath(path));
        return info.Kind switch
        {
            PathKind.Invalid => false,
            PathKind.EventFile or PathKind.ByTypeEvent or PathKind.ByFunctionEvent
                => info.Seq is int s && Store.BySeq.ContainsKey(s),
            PathKind.ByTypeCategory
                => info.Category != null && Store.ByType.ContainsKey(info.Category),
            PathKind.ByFunctionCategory
                => info.Category != null && Store.ByFunction.ContainsKey(info.Category),
            PathKind.ExceptionFolder or PathKind.ExceptionEvent
                or PathKind.ExceptionContext or PathKind.ExceptionStack
                => info.ExceptionIndex is int xi && xi >= 1 && xi <= Store.Exceptions.Count,
            _ => true,
        };
    }

    protected override bool IsItemContainer(string path)
    {
        return Parse(NormalizePath(path)).Kind switch
        {
            PathKind.Root or PathKind.EventsFolder or
            PathKind.ByTypeFolder or PathKind.ByTypeCategory or
            PathKind.ByFunctionFolder or PathKind.ByFunctionCategory or
            PathKind.ExceptionsFolder or PathKind.ExceptionFolder => true,
            _ => false,
        };
    }

    protected override bool HasChildItems(string path) => IsItemContainer(path);

    protected override void GetItem(string path)
    {
        var info = Parse(NormalizePath(path));
        var parentPath = GetParentPath(path, null);
        var directory = EnsureDrivePrefix(parentPath);

        switch (info.Kind)
        {
            case PathKind.EventFile:
            case PathKind.ByTypeEvent:
            case PathKind.ByFunctionEvent:
                if (info.Seq is int seq && Store.BySeq.TryGetValue(seq, out var ev))
                    WriteEvent(ev, path, directory);
                break;

            case PathKind.ExceptionFolder:
                if (info.ExceptionIndex is int xi && xi >= 1 && xi <= Store.Exceptions.Count)
                    WriteException(Store.Exceptions[xi - 1], path, directory);
                break;

            default:
                WriteFolder(Path.GetFileName(path), path, directory, "", null);
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
            WriteError(new ErrorRecord(ex, "TraceGetChildError", ErrorCategory.NotSpecified, path));
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
                yield return ("events", true);
                yield return ("by-type", true);
                yield return ("by-function", true);
                yield return ("exceptions", true);
                break;
            case PathKind.EventsFolder:
                foreach (var ev in Store.Events) yield return ($"{ev.Seq}.json", false);
                break;
            case PathKind.ByTypeFolder:
                foreach (var k in Store.ByType.Keys.OrderBy(k => k)) yield return (k, true);
                break;
            case PathKind.ByTypeCategory:
                if (info.Category != null && Store.ByType.TryGetValue(info.Category, out var bt))
                    foreach (var ev in bt) yield return ($"{ev.Seq}.json", false);
                break;
            case PathKind.ByFunctionFolder:
                foreach (var k in Store.ByFunction.Keys.OrderBy(k => k)) yield return (k, true);
                break;
            case PathKind.ByFunctionCategory:
                if (info.Category != null && Store.ByFunction.TryGetValue(info.Category, out var bf))
                    foreach (var ev in bf) yield return ($"{ev.Seq}.json", false);
                break;
            case PathKind.ExceptionsFolder:
                foreach (var rec in Store.Exceptions) yield return (rec.Index.ToString(), true);
                break;
            case PathKind.ExceptionFolder:
                yield return ("event.json", false);
                yield return ("context.json", false);
                yield return ("stack.txt", false);
                break;
        }
    }

    private void WriteChildren(ParsedPath info, string path)
    {
        var directory = EnsureDrivePrefix(path);
        switch (info.Kind)
        {
            case PathKind.Root:
                WriteFile("summary.json", MakePath(path, "summary.json"), directory);
                WriteFolder("events", MakePath(path, "events"), directory,
                    "all events by sequence", Store.Summary.TotalEvents);
                WriteFolder("by-type", MakePath(path, "by-type"), directory,
                    "events grouped by type", Store.ByType.Count);
                WriteFolder("by-function", MakePath(path, "by-function"), directory,
                    "events grouped by function", Store.ByFunction.Count);
                WriteFolder("exceptions", MakePath(path, "exceptions"), directory,
                    "exception occurrences with context", Store.Exceptions.Count);
                break;

            case PathKind.EventsFolder:
                foreach (var ev in Store.Events)
                {
                    if (Stopping) return;
                    WriteEvent(ev, MakePath(path, $"{ev.Seq}.json"), directory);
                }
                break;

            case PathKind.ByTypeFolder:
                foreach (var kv in Store.ByType.OrderBy(kv => kv.Key))
                {
                    if (Stopping) return;
                    WriteFolder(kv.Key, MakePath(path, kv.Key), directory,
                        $"{kv.Value.Count} events", kv.Value.Count);
                }
                break;

            case PathKind.ByTypeCategory:
                if (info.Category != null && Store.ByType.TryGetValue(info.Category, out var bt))
                    foreach (var ev in bt)
                    {
                        if (Stopping) return;
                        WriteEvent(ev, MakePath(path, $"{ev.Seq}.json"), directory);
                    }
                break;

            case PathKind.ByFunctionFolder:
                foreach (var kv in Store.ByFunction.OrderBy(kv => kv.Key))
                {
                    if (Stopping) return;
                    WriteFolder(kv.Key, MakePath(path, kv.Key), directory,
                        $"{kv.Value.Count} events", kv.Value.Count);
                }
                break;

            case PathKind.ByFunctionCategory:
                if (info.Category != null && Store.ByFunction.TryGetValue(info.Category, out var bf))
                    foreach (var ev in bf)
                    {
                        if (Stopping) return;
                        WriteEvent(ev, MakePath(path, $"{ev.Seq}.json"), directory);
                    }
                break;

            case PathKind.ExceptionsFolder:
                foreach (var rec in Store.Exceptions)
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
                        SourceFile = rec.Event.File,
                        Path = EnsureDrivePrefix(xPath),
                        Directory = directory,
                    };
                    WriteItemObject(item, xPath, isContainer: true);
                }
                break;

            case PathKind.ExceptionFolder:
                WriteFile("event.json", MakePath(path, "event.json"), directory);
                WriteFile("context.json", MakePath(path, "context.json"), directory);
                WriteFile("stack.txt", MakePath(path, "stack.txt"), directory);
                break;
        }
    }

    // === File content ===

    protected override string GetFileText(string normalizedPath)
    {
        var info = Parse(normalizedPath);
        return info.Kind switch
        {
            PathKind.Summary => JsonSerializer.Serialize(Store.Summary, TraceJson.Options),
            PathKind.EventFile or PathKind.ByTypeEvent or PathKind.ByFunctionEvent
                => info.Seq is int s && Store.BySeq.TryGetValue(s, out var ev)
                    ? JsonSerializer.Serialize(ev, TraceJson.Options) : "{}",
            PathKind.ExceptionEvent when TryGetException(info.ExceptionIndex, out var rec)
                => JsonSerializer.Serialize(rec!.Event, TraceJson.Options),
            PathKind.ExceptionContext when TryGetException(info.ExceptionIndex, out var rec)
                => JsonSerializer.Serialize(rec!.Context, TraceJson.Options),
            PathKind.ExceptionStack when TryGetException(info.ExceptionIndex, out var rec)
                => RenderStack(rec!),
            _ => "",
        };
    }

    private bool TryGetException(int? idx, out ExceptionRecord? rec)
    {
        rec = null;
        if (idx is not int i || i < 1 || i > Store.Exceptions.Count) return false;
        rec = Store.Exceptions[i - 1];
        return true;
    }

    private static string RenderStack(ExceptionRecord rec)
    {
        var lines = new List<string>
        {
            $"Exception #{rec.Index}: {rec.Event.Exception}: {rec.Event.Message}",
            $"Raised at {rec.Event.File}:{rec.Event.Line} in {rec.Event.Function}",
        };
        if (rec.Event.Locals != null && rec.Event.Locals.Count > 0)
        {
            lines.Add("");
            lines.Add("Locals at throw site:");
            foreach (var kv in rec.Event.Locals) lines.Add($"  {kv.Key} = {kv.Value}");
        }
        lines.Add("");
        lines.Add($"Context (±{(rec.Context.Count - 1) / 2} events):");
        foreach (var c in rec.Context)
        {
            var marker = c.Seq == rec.Event.Seq ? ">>>" : "   ";
            var depth = c.Depth?.ToString() ?? "-";
            var line = c.Line?.ToString() ?? "-";
            lines.Add($"{marker} [#{c.Seq,4} d={depth} L{line}] {c.Type,-10} {c.Function}");
        }
        return string.Join("\n", lines);
    }

    // === Write helpers ===

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

    private void WriteEvent(TraceEvent ev, string itemPath, string directory)
    {
        WriteItemObject(EventItem.From(ev, EnsureDrivePrefix(itemPath), directory),
            itemPath, isContainer: false);
    }

    private void WriteException(ExceptionRecord rec, string itemPath, string directory)
    {
        WriteItemObject(new ExceptionItem
        {
            Index = rec.Index,
            Seq = rec.Event.Seq,
            ExceptionType = rec.Event.Exception ?? "",
            Message = rec.Event.Message ?? "",
            Function = rec.Event.Function,
            Line = rec.Event.Line,
            SourceFile = rec.Event.File,
            Path = EnsureDrivePrefix(itemPath),
            Directory = directory,
        }, itemPath, isContainer: true);
    }
}
