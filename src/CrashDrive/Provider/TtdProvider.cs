using System.Management.Automation;
using System.Management.Automation.Provider;
using System.Text.Json;
using CrashDrive.Models;
using CrashDrive.Trace;
using CrashDrive.Ttd;

namespace CrashDrive.Provider;

/// <summary>
/// PSProvider "Ttd" — mounts a Time Travel Debugging (.run) trace via DbgEng.
///
/// Hierarchy (v0.3 minimum):
/// <code>
///   &lt;drive&gt;:\
///     summary.json
///     info.json
///     timeline.json
///     ttd-events\&lt;index&gt;.json
/// </code>
///
/// Position-based navigation (positions/, per-time thread state) is planned for v0.4.
/// </summary>
[CmdletProvider("Ttd", ProviderCapabilities.None)]
[OutputType(typeof(FolderItem), ProviderCmdlet = ProviderCmdlet.GetChildItem)]
public sealed class TtdProvider : ProviderBase
{
    private TtdDriveInfo Drive => (TtdDriveInfo)PSDriveInfo;
    private TtdStore Store => Drive.Store;

    // === Drive lifecycle ===

    protected override object NewDriveDynamicParameters() => new NewCrashDriveDynamicParameters();

    protected override PSDriveInfo NewDrive(PSDriveInfo drive)
    {
        if (drive is TtdDriveInfo existing) return existing;

        var dyn = DynamicParameters as NewCrashDriveDynamicParameters
            ?? throw new PSArgumentException(
                "Ttd provider requires -File. Use: New-PSDrive -PSProvider Ttd -Name <name> -Root \\ -File <run-path>");

        var absFile = Path.GetFullPath(dyn.File);
        if (!File.Exists(absFile))
            throw new PSArgumentException($"File not found: {absFile}");

        var inner = new PSDriveInfo(drive.Name, drive.Provider, @"\",
            drive.Description ?? "", drive.Credential);
        return new TtdDriveInfo(inner, absFile, dyn.SymbolPath);
    }

    protected override PSDriveInfo RemoveDrive(PSDriveInfo drive)
    {
        if (drive is TtdDriveInfo info)
            try { info.Store.Dispose(); } catch { }
        return drive;
    }

    // === Path parsing ===

    private enum PathKind
    {
        Root, Summary, Timeline,
        EventsFolder, EventFile,
        PositionsFolder,                            // positions/
        PositionFolder,                             // positions/<pos>/
        PositionInfoFile,                           // positions/<pos>/position.json
        PositionThreadsFolder,                      // positions/<pos>/threads/
        PositionThreadFolder,                       // positions/<pos>/threads/<tid>/
        PositionThreadInfoFile,                     // positions/<pos>/threads/<tid>/info.json
        PositionThreadFramesFolder,                 // positions/<pos>/threads/<tid>/frames/
        PositionFrameFile,                          // positions/<pos>/threads/<tid>/frames/<n>.json

        CallsFolder,                                // calls/
        CallsModuleFolder,                          // calls/<module>/
        CallsFunctionFolder,                        // calls/<module>/<function>/
        CallFile,                                   // calls/<module>/<function>/<n>.json

        MemoryFolder,                               // memory/
        MemoryRangeFolder,                          // memory/<start>_<end>/
        MemoryAccessKindFolder,                     // memory/<range>/{writes,reads,rw}/
        MemoryAccessFile,                           // memory/<range>/<kind>/<n>.json

        Invalid,
    }

    private readonly record struct ParsedPath(
        PathKind Kind,
        string[] Segments,
        int? Index = null,
        string? EncodedPosition = null,
        string? ThreadId = null,
        int? FrameIndex = null,
        string? Module = null,
        string? Function = null,
        string? MemStart = null,
        string? MemEnd = null,
        string? AccessMode = null);

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
                "timeline.json" => new(PathKind.Timeline, segs),
                "ttd-events" => new(PathKind.EventsFolder, segs),
                "positions" => new(PathKind.PositionsFolder, segs),
                "calls" => new(PathKind.CallsFolder, segs),
                "memory" => new(PathKind.MemoryFolder, segs),
                _ => new(PathKind.Invalid, segs),
            };
        }
        if (head == "ttd-events" && segs.Length == 2
            && segs[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(segs[1][..^5], out var idx))
            return new(PathKind.EventFile, segs, Index: idx);

        if (head == "positions")
        {
            // positions/<pos>
            if (segs.Length < 2) return new(PathKind.Invalid, segs);
            var encPos = segs[1];
            if (!Ttd.TtdPosition.IsValid(encPos)) return new(PathKind.Invalid, segs);

            if (segs.Length == 2) return new(PathKind.PositionFolder, segs, EncodedPosition: encPos);
            if (segs.Length == 3 && segs[2].Equals("position.json", StringComparison.OrdinalIgnoreCase))
                return new(PathKind.PositionInfoFile, segs, EncodedPosition: encPos);
            if (segs.Length == 3 && segs[2].Equals("threads", StringComparison.OrdinalIgnoreCase))
                return new(PathKind.PositionThreadsFolder, segs, EncodedPosition: encPos);

            // positions/<pos>/threads/<tid>/...
            if (segs.Length >= 4 && segs[2].Equals("threads", StringComparison.OrdinalIgnoreCase))
            {
                var tid = segs[3];
                if (segs.Length == 4) return new(PathKind.PositionThreadFolder, segs, EncodedPosition: encPos, ThreadId: tid);
                if (segs.Length == 5)
                {
                    var sub = segs[4].ToLowerInvariant();
                    return sub switch
                    {
                        "info.json" => new(PathKind.PositionThreadInfoFile, segs, EncodedPosition: encPos, ThreadId: tid),
                        "frames" => new(PathKind.PositionThreadFramesFolder, segs, EncodedPosition: encPos, ThreadId: tid),
                        _ => new(PathKind.Invalid, segs),
                    };
                }
                if (segs.Length == 6 && segs[4].Equals("frames", StringComparison.OrdinalIgnoreCase)
                    && segs[5].EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(segs[5][..^5], out var fi))
                    return new(PathKind.PositionFrameFile, segs, EncodedPosition: encPos, ThreadId: tid, FrameIndex: fi);
            }
        }

        if (head == "calls")
        {
            // calls/<module>[/<function>[/<n>.json]]
            if (segs.Length == 2) return new(PathKind.CallsModuleFolder, segs, Module: segs[1]);
            if (segs.Length == 3) return new(PathKind.CallsFunctionFolder, segs, Module: segs[1], Function: segs[2]);
            if (segs.Length == 4
                && segs[3].EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(segs[3][..^5], out var callIdx))
                return new(PathKind.CallFile, segs, Module: segs[1], Function: segs[2], Index: callIdx);
        }

        if (head == "memory")
        {
            // memory/<start>_<end>[/<mode>[/<n>.json]]
            if (segs.Length < 2) return new(PathKind.Invalid, segs);
            var range = segs[1];
            var sep = range.IndexOf('_');
            if (sep <= 0 || sep >= range.Length - 1) return new(PathKind.Invalid, segs);
            var start = range[..sep];
            var end = range[(sep + 1)..];

            if (segs.Length == 2) return new(PathKind.MemoryRangeFolder, segs, MemStart: start, MemEnd: end);
            if (segs.Length == 3 && segs[2] is "writes" or "reads" or "rw")
                return new(PathKind.MemoryAccessKindFolder, segs,
                    MemStart: start, MemEnd: end, AccessMode: ModeFromFolder(segs[2]));
            if (segs.Length == 4 && segs[2] is "writes" or "reads" or "rw"
                && segs[3].EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(segs[3][..^5], out var memIdx))
                return new(PathKind.MemoryAccessFile, segs,
                    MemStart: start, MemEnd: end,
                    AccessMode: ModeFromFolder(segs[2]), Index: memIdx);
        }

        return new(PathKind.Invalid, segs);
    }

    private static string ModeFromFolder(string folder) => folder switch
    {
        "writes" => "w",
        "reads" => "r",
        "rw" => "rw",
        _ => "w",
    };

    /// <summary>Resolve an encoded position (including "start"/"end") to the native TTD form.</summary>
    private string ResolvePosition(string encoded) => encoded switch
    {
        "start" => Store.Summary.LifetimeStart,
        "end" => Store.Summary.LifetimeEnd,
        _ => Ttd.TtdPosition.Decode(encoded),
    };

    // === Path operations ===

    protected override bool ItemExists(string path)
    {
        var info = Parse(NormalizePath(path));
        return info.Kind switch
        {
            PathKind.Invalid => false,
            PathKind.EventFile
                => info.Index is int i && i >= 0 && i < Store.Events.Count,
            PathKind.CallFile when info.Module != null && info.Function != null && info.Index is int ci
                => ci >= 0 && ci < Store.GetCalls(info.Module, info.Function).Count,
            PathKind.MemoryAccessFile when info.MemStart != null && info.MemEnd != null
                && info.AccessMode != null && info.Index is int mi
                => mi >= 0 && mi < Store.GetMemoryAccesses(info.MemStart, info.MemEnd, info.AccessMode).Count,
            _ => true,
        };
    }

    protected override bool IsItemContainer(string path)
    {
        return Parse(NormalizePath(path)).Kind switch
        {
            PathKind.Root or PathKind.EventsFolder or
            PathKind.PositionsFolder or PathKind.PositionFolder or
            PathKind.PositionThreadsFolder or PathKind.PositionThreadFolder or
            PathKind.PositionThreadFramesFolder or
            PathKind.CallsFolder or PathKind.CallsModuleFolder or
            PathKind.CallsFunctionFolder or
            PathKind.MemoryFolder or PathKind.MemoryRangeFolder or
            PathKind.MemoryAccessKindFolder => true,
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
            case PathKind.EventFile when info.Index is int ei && ei >= 0 && ei < Store.Events.Count:
                var ev = Store.Events[ei];
                WriteItemObject(new Models.TtdEventItem
                {
                    Index = ei, Name = name, Position = ev.Position,
                    Type = ev.Type, Module = ev.Module,
                    Path = EnsureDrivePrefix(path), Directory = dir,
                }, path, isContainer: false);
                break;

            case PathKind.CallFile when info.Module != null && info.Function != null
                    && info.Index is int ci:
                var calls = Store.GetCalls(info.Module, info.Function);
                if (ci >= 0 && ci < calls.Count)
                {
                    var c = calls[ci];
                    WriteItemObject(new Models.TtdCallItem
                    {
                        Index = ci, Name = name,
                        ThreadId = c.ThreadId, TimeStart = c.TimeStart,
                        TimeEnd = c.TimeEnd, ReturnValue = c.ReturnValue,
                        Path = EnsureDrivePrefix(path), Directory = dir,
                    }, path, isContainer: false);
                }
                break;

            case PathKind.MemoryAccessFile when info.MemStart != null && info.MemEnd != null
                    && info.AccessMode != null && info.Index is int mi:
                var recs = Store.GetMemoryAccesses(info.MemStart, info.MemEnd, info.AccessMode);
                if (mi >= 0 && mi < recs.Count)
                {
                    var r = recs[mi];
                    WriteItemObject(new Models.TtdMemoryItem
                    {
                        Index = mi, Name = name, Position = r.TimeStart,
                        AccessType = r.AccessType, Address = r.Address, Value = r.Value,
                        Path = EnsureDrivePrefix(path), Directory = dir,
                    }, path, isContainer: false);
                }
                break;

            case PathKind.PositionFrameFile when info.EncodedPosition != null
                    && info.ThreadId != null && info.FrameIndex is int pfi:
                Store.SeekTo(ResolvePosition(info.EncodedPosition));
                var pframes = Store.GetFramesAtCurrentPosition(info.ThreadId);
                var pf = pframes.FirstOrDefault(x => x.Index == pfi);
                if (pf != null)
                {
                    WriteItemObject(new Models.TtdPositionFrameItem
                    {
                        Index = pf.Index, Name = name, Frame = pf.Description,
                        Path = EnsureDrivePrefix(path), Directory = dir,
                    }, path, isContainer: false);
                }
                break;

            case PathKind.Summary or PathKind.Timeline
                or PathKind.PositionInfoFile or PathKind.PositionThreadInfoFile:
                WriteFile(name, path, dir);
                break;

            case PathKind.PositionFolder when info.EncodedPosition != null:
                WriteFolder(name, path, dir, PositionDescription(info.EncodedPosition), null);
                break;

            case PathKind.PositionThreadFolder when info.EncodedPosition != null && info.ThreadId != null:
                Store.SeekTo(ResolvePosition(info.EncodedPosition));
                var tCount = Store.GetThreadsAtCurrentPosition()
                    .FirstOrDefault(x => x.Id.Equals(info.ThreadId, StringComparison.OrdinalIgnoreCase))?.FrameCount ?? 0;
                WriteFolder(name, path, dir,
                    $"{tCount} frames at position {ResolvePosition(info.EncodedPosition)}", tCount);
                break;

            case PathKind.EventsFolder:
                WriteFolder(name, path, dir, "notable events during recording", Store.Summary.EventCount);
                break;

            default:
                WriteFolder(name, path, dir, "", null);
                break;
        }
    }

    /// <summary>
    /// Extract module short-names (without path/extension) from ModuleLoaded events.
    /// Used to enumerate calls\&lt;module&gt;\ — so users/AI can discover which
    /// modules' functions are queryable.
    /// </summary>
    private IEnumerable<string> GetLoadedModuleNames()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ev in Store.Events)
        {
            if (ev.Type != "ModuleLoaded" || string.IsNullOrEmpty(ev.Module)) continue;
            // ev.Module text looks like: "Module C:\WINDOWS\System32\bcrypt.dll at address 0X... with size ..."
            var text = ev.Module;
            var startIdx = text.IndexOf(' ');
            if (startIdx < 0) continue;
            var atIdx = text.IndexOf(" at ", startIdx, StringComparison.Ordinal);
            if (atIdx < 0) continue;
            var fullPath = text[(startIdx + 1)..atIdx].Trim();
            var fileName = System.IO.Path.GetFileNameWithoutExtension(fullPath);
            if (!string.IsNullOrEmpty(fileName) && seen.Add(fileName))
                yield return fileName;
        }
    }

    private string PositionDescription(string encodedPosition)
    {
        if (encodedPosition == "start") return $"Lifetime start ({Store.Summary.LifetimeStart})";
        if (encodedPosition == "end") return $"Lifetime end ({Store.Summary.LifetimeEnd})";
        // Look up event at this position
        var native = Ttd.TtdPosition.Decode(encodedPosition);
        var ev = Store.Events.FirstOrDefault(e => e.Position == native);
        return ev != null ? $"{ev.Type} @ {ev.Position}" : "";
    }

    // === Children ===

    protected override void GetChildItems(string path, bool recurse)
    {
        var info = Parse(NormalizePath(path));
        try { WriteChildren(info, path); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WriteError(new ErrorRecord(ex, "TtdGetChildError", ErrorCategory.NotSpecified, path));
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
                yield return ("timeline.json", false);
                yield return ("ttd-events", true);
                yield return ("positions", true);
                break;

            case PathKind.EventsFolder:
                for (int i = 0; i < Store.Events.Count; i++)
                    yield return ($"{i}.json", false);
                break;

            case PathKind.PositionsFolder:
                // Symbolic positions
                yield return ("start", true);
                yield return ("end", true);
                // Event positions (encoded)
                var seen = new HashSet<string>();
                foreach (var ev in Store.Events)
                {
                    if (string.IsNullOrEmpty(ev.Position)) continue;
                    var enc = Ttd.TtdPosition.Encode(ev.Position);
                    if (seen.Add(enc)) yield return (enc, true);
                }
                break;

            case PathKind.PositionFolder:
                yield return ("position.json", false);
                yield return ("threads", true);
                break;

            case PathKind.PositionThreadsFolder:
                if (info.EncodedPosition != null)
                {
                    Store.SeekTo(ResolvePosition(info.EncodedPosition));
                    foreach (var t in Store.GetThreadsAtCurrentPosition())
                        yield return (t.Id, true);
                }
                break;

            case PathKind.PositionThreadFolder:
                yield return ("info.json", false);
                yield return ("frames", true);
                break;

            case PathKind.PositionThreadFramesFolder:
                if (info.EncodedPosition != null && info.ThreadId != null)
                {
                    Store.SeekTo(ResolvePosition(info.EncodedPosition));
                    var frames = Store.GetFramesAtCurrentPosition(info.ThreadId);
                    foreach (var f in frames)
                        yield return ($"{f.Index}.json", false);
                }
                break;

            case PathKind.CallsFolder:
                // Enumerate modules known to have loaded from ModuleLoaded events.
                foreach (var mod in GetLoadedModuleNames())
                    yield return (mod, true);
                break;
            case PathKind.CallsModuleFolder:
                // Functions can't be enumerated (too many); user specifies by name.
                break;
            case PathKind.CallsFunctionFolder:
                if (info.Module != null && info.Function != null)
                {
                    var calls = Store.GetCalls(info.Module, info.Function);
                    for (int i = 0; i < calls.Count; i++)
                        yield return ($"{i}.json", false);
                }
                break;

            case PathKind.MemoryFolder:
            case PathKind.MemoryRangeFolder when info.MemStart == null:
                break; // ranges specified by user

            case PathKind.MemoryRangeFolder:
                yield return ("writes", true);
                yield return ("reads", true);
                yield return ("rw", true);
                break;

            case PathKind.MemoryAccessKindFolder:
                if (info.MemStart != null && info.MemEnd != null && info.AccessMode != null)
                {
                    var records = Store.GetMemoryAccesses(info.MemStart, info.MemEnd, info.AccessMode);
                    for (int i = 0; i < records.Count; i++)
                        yield return ($"{i}.json", false);
                }
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
                WriteFile("timeline.json", MakePath(path, "timeline.json"), dir);
                WriteFolder("ttd-events", MakePath(path, "ttd-events"), dir,
                    "notable events during recording", Store.Summary.EventCount);
                WriteFolder("positions", MakePath(path, "positions"), dir,
                    "navigable time positions", null);
                WriteFolder("calls", MakePath(path, "calls"), dir,
                    "query calls to specific functions: calls\\<module>\\<function>\\", null);
                WriteFolder("memory", MakePath(path, "memory"), dir,
                    "memory access history: memory\\<start>_<end>\\{writes,reads,rw}\\", null);
                break;

            case PathKind.CallsFolder:
                foreach (var mod in GetLoadedModuleNames())
                {
                    if (Stopping) return;
                    var mPath = MakePath(path, mod);
                    WriteFolder(mod, mPath, dir,
                        "enter a function name as sub-folder (e.g. calls\\" + mod + "\\SomeFunction)", null);
                }
                break;

            case PathKind.CallsFunctionFolder:
                if (info.Module != null && info.Function != null)
                {
                    var calls = Store.GetCalls(info.Module, info.Function);
                    for (int i = 0; i < calls.Count; i++)
                    {
                        if (Stopping) return;
                        var c = calls[i];
                        var cPath = MakePath(path, $"{i}.json");
                        WriteItemObject(new Models.TtdCallItem
                        {
                            Index = i,
                            Name = $"{i}.json",
                            ThreadId = c.ThreadId,
                            TimeStart = c.TimeStart,
                            TimeEnd = c.TimeEnd,
                            ReturnValue = c.ReturnValue,
                            Path = EnsureDrivePrefix(cPath),
                            Directory = dir,
                        }, cPath, isContainer: false);
                    }
                }
                break;

            case PathKind.MemoryRangeFolder:
                WriteFolder("writes", MakePath(path, "writes"), dir, "memory writes to this range", null);
                WriteFolder("reads", MakePath(path, "reads"), dir, "memory reads from this range", null);
                WriteFolder("rw", MakePath(path, "rw"), dir, "reads and writes combined", null);
                break;

            case PathKind.MemoryAccessKindFolder:
                if (info.MemStart != null && info.MemEnd != null && info.AccessMode != null)
                {
                    var records = Store.GetMemoryAccesses(info.MemStart, info.MemEnd, info.AccessMode);
                    for (int i = 0; i < records.Count; i++)
                    {
                        if (Stopping) return;
                        var r = records[i];
                        var rPath = MakePath(path, $"{i}.json");
                        WriteItemObject(new Models.TtdMemoryItem
                        {
                            Index = i,
                            Name = $"{i}.json",
                            Position = r.TimeStart,
                            AccessType = r.AccessType,
                            Address = r.Address,
                            Value = r.Value,
                            Path = EnsureDrivePrefix(rPath),
                            Directory = dir,
                        }, rPath, isContainer: false);
                    }
                }
                break;

            case PathKind.EventsFolder:
                for (int i = 0; i < Store.Events.Count; i++)
                {
                    if (Stopping) return;
                    var ev = Store.Events[i];
                    var ePath = MakePath(path, $"{i}.json");
                    WriteItemObject(new Models.TtdEventItem
                    {
                        Index = i,
                        Name = $"{i}.json",
                        Position = ev.Position,
                        Type = ev.Type,
                        Module = ev.Module,
                        Path = EnsureDrivePrefix(ePath),
                        Directory = dir,
                    }, ePath, isContainer: false);
                }
                break;

            case PathKind.PositionsFolder:
                WriteFolder("start", MakePath(path, "start"), dir,
                    $"Lifetime start ({Store.Summary.LifetimeStart})", null);
                WriteFolder("end", MakePath(path, "end"), dir,
                    $"Lifetime end ({Store.Summary.LifetimeEnd})", null);
                var seen = new HashSet<string>();
                foreach (var ev in Store.Events)
                {
                    if (Stopping) return;
                    if (string.IsNullOrEmpty(ev.Position)) continue;
                    var enc = Ttd.TtdPosition.Encode(ev.Position);
                    if (!seen.Add(enc)) continue;
                    WriteFolder(enc, MakePath(path, enc), dir,
                        $"{ev.Type} @ {ev.Position}", null);
                }
                break;

            case PathKind.PositionFolder:
                WriteFile("position.json", MakePath(path, "position.json"), dir);
                WriteFolder("threads", MakePath(path, "threads"), dir,
                    "threads at this time position", null);
                break;

            case PathKind.PositionThreadsFolder:
                if (info.EncodedPosition != null)
                {
                    Store.SeekTo(ResolvePosition(info.EncodedPosition));
                    foreach (var t in Store.GetThreadsAtCurrentPosition())
                    {
                        if (Stopping) return;
                        var tPath = MakePath(path, t.Id);
                        WriteFolder(t.Id, tPath, dir,
                            $"{t.FrameCount} frames at position {ResolvePosition(info.EncodedPosition)}",
                            t.FrameCount);
                    }
                }
                break;

            case PathKind.PositionThreadFolder:
                WriteFile("info.json", MakePath(path, "info.json"), dir);
                WriteFolder("frames", MakePath(path, "frames"), dir, "stack frames", null);
                break;

            case PathKind.PositionThreadFramesFolder:
                if (info.EncodedPosition != null && info.ThreadId != null)
                {
                    Store.SeekTo(ResolvePosition(info.EncodedPosition));
                    foreach (var f in Store.GetFramesAtCurrentPosition(info.ThreadId))
                    {
                        if (Stopping) return;
                        var fPath = MakePath(path, $"{f.Index}.json");
                        WriteItemObject(new Models.TtdPositionFrameItem
                        {
                            Index = f.Index,
                            Name = $"{f.Index}.json",
                            Frame = f.Description,
                            Path = EnsureDrivePrefix(fPath),
                            Directory = dir,
                        }, fPath, isContainer: false);
                    }
                }
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
            PathKind.Timeline => JsonSerializer.Serialize(new
            {
                Store.Summary.LifetimeStart,
                Store.Summary.LifetimeEnd,
                EventCount = Store.Summary.EventCount
            }, TraceJson.Options),
            PathKind.EventFile when info.Index is int i && i >= 0 && i < Store.Events.Count
                => JsonSerializer.Serialize(Store.Events[i], TraceJson.Options),

            PathKind.PositionInfoFile when info.EncodedPosition != null
                => JsonSerializer.Serialize(new
                {
                    Encoded = info.EncodedPosition,
                    Native = ResolvePosition(info.EncodedPosition),
                }, TraceJson.Options),

            PathKind.PositionThreadInfoFile when info.EncodedPosition != null && info.ThreadId != null
                => SerializeThreadInfo(info),

            PathKind.PositionFrameFile when info.EncodedPosition != null
                    && info.ThreadId != null && info.FrameIndex is int fi
                => SerializeFrame(info, fi),

            PathKind.CallFile when info.Module != null && info.Function != null
                    && info.Index is int callIdx
                => SerializeCall(info.Module, info.Function, callIdx),

            PathKind.MemoryAccessFile when info.MemStart != null && info.MemEnd != null
                    && info.AccessMode != null && info.Index is int memIdx
                => SerializeMemoryAccess(info.MemStart, info.MemEnd, info.AccessMode, memIdx),

            _ => "",
        };
    }

    private string SerializeMemoryAccess(string start, string end, string mode, int index)
    {
        var records = Store.GetMemoryAccesses(start, end, mode);
        if (index < 0 || index >= records.Count) return "{}";
        return JsonSerializer.Serialize(records[index], TraceJson.Options);
    }

    private string SerializeCall(string module, string function, int index)
    {
        var calls = Store.GetCalls(module, function);
        if (index < 0 || index >= calls.Count) return "{}";
        // Fetch parameters only when reading a specific call — avoids N×param queries
        // during folder enumeration.
        var call = calls[index];
        call.Parameters = Store.GetCallParameters(module, function, index);
        return JsonSerializer.Serialize(call, TraceJson.Options);
    }

    private string SerializeThreadInfo(ParsedPath info)
    {
        Store.SeekTo(ResolvePosition(info.EncodedPosition!));
        var thread = Store.GetThreadsAtCurrentPosition()
            .FirstOrDefault(t => t.Id.Equals(info.ThreadId!, StringComparison.OrdinalIgnoreCase));
        return thread != null
            ? JsonSerializer.Serialize(new
            {
                Id = thread.Id,
                FrameCount = thread.FrameCount,
                Position = ResolvePosition(info.EncodedPosition!),
            }, TraceJson.Options)
            : "{}";
    }

    private string SerializeFrame(ParsedPath info, int frameIndex)
    {
        Store.SeekTo(ResolvePosition(info.EncodedPosition!));
        var frames = Store.GetFramesAtCurrentPosition(info.ThreadId!);
        var f = frames.FirstOrDefault(x => x.Index == frameIndex);
        return f != null
            ? JsonSerializer.Serialize(f, TraceJson.Options) : "{}";
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
