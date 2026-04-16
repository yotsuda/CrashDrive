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
        Root, Summary, Timeline, Triage,
        EventsFolder, EventFile,

        TimelineFolder,                             // timeline/
        TimelineEventsFolder,                       // timeline/events/
        TimelineEventFile,                          // timeline/events/<n>.json
        TimelineExceptionsFolder,                   // timeline/exceptions/
        TimelineExceptionFile,                      // timeline/exceptions/<n>.json
        TimelineSignificantFolder,                  // timeline/significant/
        TimelineSignificantFile,                    // timeline/significant/<n>.json

        PositionsFolder,                            // positions/
        PositionFolder,                             // positions/<pos>/
        PositionInfoFile,                           // positions/<pos>/position.json
        PositionThreadsFolder,                      // positions/<pos>/threads/
        PositionThreadFolder,                       // positions/<pos>/threads/<tid>/
        PositionThreadInfoFile,                     // positions/<pos>/threads/<tid>/info.json
        PositionThreadRegistersFile,                // positions/<pos>/threads/<tid>/registers.txt
        PositionThreadFramesFolder,                 // positions/<pos>/threads/<tid>/frames/
        PositionFrameFile,                          // positions/<pos>/threads/<tid>/frames/<n>.json

        CallsFolder,                                // calls/
        CallsModuleFolder,                          // calls/<module>/
        CallsFunctionFolder,                        // calls/<module>/<function>/
        CallFile,                                   // calls/<module>/<function>/<n>.json
        CallsRangeFolder,                           // calls/<module>/<function>/<start>-<end>/
        CallsRangeFile,                             // calls/<module>/<function>/<start>-<end>/<n>.json

        BookmarksFolder,                            // bookmarks/

        MemoryFolder,                               // memory/
        MemoryRangeFolder,                          // memory/<start>_<end>/
        MemoryAccessKindFolder,                     // memory/<range>/{writes,reads,rw}/
        MemoryAccessFile,                           // memory/<range>/<kind>/<n>.json
        MemoryFirstWriteFile,                       // memory/<range>/first-write.json
        MemoryLastWriteBeforeFolder,                // memory/<range>/last-write-before/
        MemoryLastWriteBeforeFile,                  // memory/<range>/last-write-before/<encoded-pos>.json

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
        string? AccessMode = null,
        int? RangeStart = null,
        int? RangeEnd = null);

    /// <summary>Pagination threshold for <c>calls\&lt;mod&gt;\&lt;fn&gt;\</c>. Hit lists with
    /// more entries are exposed as <c>&lt;start&gt;-&lt;end&gt;\</c> subfolders instead of
    /// thousands of .json siblings — avoids multi-second <c>ls</c> hangs.</summary>
    private const int CallPageSize = 256;

    private static bool TryParseCallsRange(string seg, out int start, out int end)
    {
        start = end = 0;
        var dash = seg.IndexOf('-');
        if (dash <= 0 || dash == seg.Length - 1) return false;
        if (!int.TryParse(seg.AsSpan(0, dash), out start)) return false;
        if (!int.TryParse(seg.AsSpan(dash + 1), out end)) return false;
        return start >= 0 && end >= start;
    }

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
                "triage.md" => new(PathKind.Triage, segs),
                "timeline" => new(PathKind.TimelineFolder, segs),
                "ttd-events" => new(PathKind.EventsFolder, segs),
                "positions" => new(PathKind.PositionsFolder, segs),
                "calls" => new(PathKind.CallsFolder, segs),
                "bookmarks" => new(PathKind.BookmarksFolder, segs),
                "memory" => new(PathKind.MemoryFolder, segs),
                _ => new(PathKind.Invalid, segs),
            };
        }
        if (head == "ttd-events" && segs.Length == 2
            && segs[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(segs[1][..^5], out var idx))
            return new(PathKind.EventFile, segs, Index: idx);

        if (head == "timeline")
        {
            if (segs.Length == 2)
            {
                return segs[1].ToLowerInvariant() switch
                {
                    "events" => new(PathKind.TimelineEventsFolder, segs),
                    "exceptions" => new(PathKind.TimelineExceptionsFolder, segs),
                    "significant" => new(PathKind.TimelineSignificantFolder, segs),
                    _ => new(PathKind.Invalid, segs),
                };
            }
            if (segs.Length == 3 && segs[2].EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(segs[2][..^5], out var tlIdx))
            {
                return segs[1].ToLowerInvariant() switch
                {
                    "events" => new(PathKind.TimelineEventFile, segs, Index: tlIdx),
                    "exceptions" => new(PathKind.TimelineExceptionFile, segs, Index: tlIdx),
                    "significant" => new(PathKind.TimelineSignificantFile, segs, Index: tlIdx),
                    _ => new(PathKind.Invalid, segs),
                };
            }
        }

        if (head == "positions")
        {
            // positions/<pos>
            if (segs.Length < 2) return new(PathKind.Invalid, segs);
            var encPos = segs[1];
            if (!Ttd.TtdPosition.IsValid(encPos)) return new(PathKind.Invalid, segs);
            // Symbolic aliases (start/end/first-exception/…) that the trace
            // can't satisfy — e.g. "first-exception" when ExceptionEvents is
            // empty — are rejected here so every downstream path under them
            // short-circuits to Invalid instead of silently falling back to
            // lifetime start.
            if (Ttd.TtdPosition.IsSymbolicName(encPos)
                && ResolvePositionOrNull(encPos) == null)
                return new(PathKind.Invalid, segs);

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
                        "registers.txt" => new(PathKind.PositionThreadRegistersFile, segs, EncodedPosition: encPos, ThreadId: tid),
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
            // calls/<module>[/<function>[/<n>.json|<start>-<end>[/<n>.json]]]
            if (segs.Length == 2) return new(PathKind.CallsModuleFolder, segs, Module: segs[1]);
            if (segs.Length == 3) return new(PathKind.CallsFunctionFolder, segs, Module: segs[1], Function: segs[2]);
            if (segs.Length == 4)
            {
                if (segs[3].EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(segs[3][..^5], out var callIdx))
                    return new(PathKind.CallFile, segs, Module: segs[1], Function: segs[2], Index: callIdx);
                if (TryParseCallsRange(segs[3], out var rs, out var re))
                    return new(PathKind.CallsRangeFolder, segs,
                        Module: segs[1], Function: segs[2], RangeStart: rs, RangeEnd: re);
            }
            if (segs.Length == 5
                && TryParseCallsRange(segs[3], out var rs2, out var re2)
                && segs[4].EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(segs[4][..^5], out var rnIdx)
                && rnIdx >= rs2 && rnIdx <= re2)
            {
                return new(PathKind.CallsRangeFile, segs,
                    Module: segs[1], Function: segs[2],
                    RangeStart: rs2, RangeEnd: re2, Index: rnIdx);
            }
        }

        if (head == "bookmarks" && segs.Length >= 2)
        {
            // Resolve the bookmark name → native position, then re-parse the
            // remainder as if it were the equivalent positions/<encoded>/ path.
            // Keeps the surface `bookmarks\<name>\…` but reuses every downstream
            // code path that already handles position-rooted navigation.
            if (PSDriveInfo is not TtdDriveInfo drive
                || !drive.Bookmarks.TryGetValue(segs[1], out var native))
                return new(PathKind.Invalid, segs);
            var encPos = Ttd.TtdPosition.Encode(native);
            if (segs.Length == 2)
                return new(PathKind.PositionFolder, segs, EncodedPosition: encPos);
            var synthetic = "positions/" + encPos + "/" + string.Join('/', segs.Skip(2));
            var resolved = Parse(synthetic);
            // Swap segments back to the bookmark-rooted form so downstream
            // path-join calls (MakePath) produce bookmarks\…, not positions\….
            return resolved with { Segments = segs };
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
            if (segs.Length == 3)
            {
                if (segs[2] is "writes" or "reads" or "rw")
                    return new(PathKind.MemoryAccessKindFolder, segs,
                        MemStart: start, MemEnd: end, AccessMode: ModeFromFolder(segs[2]));
                if (segs[2].Equals("first-write.json", StringComparison.OrdinalIgnoreCase))
                    return new(PathKind.MemoryFirstWriteFile, segs, MemStart: start, MemEnd: end);
                if (segs[2].Equals("last-write-before", StringComparison.OrdinalIgnoreCase))
                    return new(PathKind.MemoryLastWriteBeforeFolder, segs, MemStart: start, MemEnd: end);
            }
            if (segs.Length == 4)
            {
                if (segs[2] is "writes" or "reads" or "rw"
                    && segs[3].EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(segs[3][..^5], out var memIdx))
                    return new(PathKind.MemoryAccessFile, segs,
                        MemStart: start, MemEnd: end,
                        AccessMode: ModeFromFolder(segs[2]), Index: memIdx);
                if (segs[2].Equals("last-write-before", StringComparison.OrdinalIgnoreCase)
                    && segs[3].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    var encPos = segs[3][..^5];
                    if (Ttd.TtdPosition.IsValid(encPos))
                        return new(PathKind.MemoryLastWriteBeforeFile, segs,
                            MemStart: start, MemEnd: end, EncodedPosition: encPos);
                }
            }
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

    /// <summary>Resolve an encoded position (symbolic name or encoded form) to the
    /// native TTD form. Returns null only for aliases whose data source is empty
    /// (e.g. "first-exception" on a recording with zero exceptions).</summary>
    private string? ResolvePositionOrNull(string encoded) => encoded switch
    {
        "start" => Store.Summary.LifetimeStart,
        "end"   => Store.Summary.LifetimeEnd,
        "first-exception" => Store.ExceptionEvents.Count > 0
            ? Store.ExceptionEvents[0].Event.Position : null,
        "last-exception"  => Store.ExceptionEvents.Count > 0
            ? Store.ExceptionEvents[^1].Event.Position : null,
        "last-meaningful-event" => Store.SignificantEvents.Count > 0
            ? Store.SignificantEvents[^1].Event.Position : null,
        _ => Ttd.TtdPosition.Decode(encoded),
    };

    /// <summary>Non-null variant for code paths that have already confirmed the
    /// alias resolves (via <see cref="ItemExists"/>). Falls back to the lifetime
    /// start if a missing alias slips through, which keeps dbgeng calls from
    /// receiving a null seek target.</summary>
    private string ResolvePosition(string encoded)
        => ResolvePositionOrNull(encoded) ?? Store.Summary.LifetimeStart;

    // === Path operations ===

    protected override bool ItemExists(string path)
    {
        var info = Parse(NormalizePath(path));
        return info.Kind switch
        {
            PathKind.Invalid => false,
            PathKind.EventFile
                => info.Index is int i && i >= 0 && i < Store.Events.Count,
            PathKind.TimelineEventFile
                => info.Index is int ti && ti >= 0 && ti < Store.EventsByPosition.Count,
            PathKind.TimelineExceptionFile
                => info.Index is int tei && tei >= 0 && tei < Store.ExceptionEvents.Count,
            PathKind.TimelineSignificantFile
                => info.Index is int tsi && tsi >= 0 && tsi < Store.SignificantEvents.Count,
            PathKind.CallFile when info.Module != null && info.Function != null && info.Index is int ci
                => ci >= 0 && ci < Store.GetCallCount(info.Module, info.Function),
            PathKind.CallsRangeFolder when info.Module != null && info.Function != null
                    && info.RangeStart is int crs
                => crs < Store.GetCallCount(info.Module, info.Function),
            PathKind.CallsRangeFile when info.Module != null && info.Function != null
                    && info.Index is int cri
                => cri >= 0 && cri < Store.GetCallCount(info.Module, info.Function),
            PathKind.MemoryAccessFile when info.MemStart != null && info.MemEnd != null
                && info.AccessMode != null && info.Index is int mi
                => mi >= 0 && mi < Store.GetMemoryAccesses(info.MemStart, info.MemEnd, info.AccessMode).Count,
            PathKind.MemoryFirstWriteFile when info.MemStart != null && info.MemEnd != null
                => Store.GetFirstWrite(info.MemStart, info.MemEnd) != null,
            PathKind.MemoryLastWriteBeforeFile when info.MemStart != null && info.MemEnd != null
                    && info.EncodedPosition != null
                => Store.GetLastWriteBefore(info.MemStart, info.MemEnd,
                    ResolvePosition(info.EncodedPosition)) != null,
            _ => true,
        };
    }

    protected override bool IsItemContainer(string path)
    {
        return Parse(NormalizePath(path)).Kind switch
        {
            PathKind.Root or PathKind.EventsFolder or
            PathKind.TimelineFolder or PathKind.TimelineEventsFolder or
            PathKind.TimelineExceptionsFolder or PathKind.TimelineSignificantFolder or
            PathKind.PositionsFolder or PathKind.PositionFolder or
            PathKind.PositionThreadsFolder or PathKind.PositionThreadFolder or
            PathKind.PositionThreadFramesFolder or
            PathKind.CallsFolder or PathKind.CallsModuleFolder or
            PathKind.CallsFunctionFolder or PathKind.CallsRangeFolder or
            PathKind.BookmarksFolder or
            PathKind.MemoryFolder or PathKind.MemoryRangeFolder or
            PathKind.MemoryAccessKindFolder or
            PathKind.MemoryLastWriteBeforeFolder => true,
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

            case PathKind.TimelineEventFile when info.Index is int tli && tli >= 0 && tli < Store.EventsByPosition.Count:
                WriteEventItem(Store.EventsByPosition[tli], name, path, dir);
                break;
            case PathKind.TimelineExceptionFile when info.Index is int txi && txi >= 0 && txi < Store.ExceptionEvents.Count:
                WriteEventItem(Store.ExceptionEvents[txi], name, path, dir);
                break;
            case PathKind.TimelineSignificantFile when info.Index is int tsi && tsi >= 0 && tsi < Store.SignificantEvents.Count:
                WriteEventItem(Store.SignificantEvents[tsi], name, path, dir);
                break;

            case PathKind.CallFile when info.Module != null && info.Function != null
                    && info.Index is int ci:
                {
                    var chunk = Store.GetCalls(info.Module, info.Function, skip: ci, take: 1);
                    var c = chunk.FirstOrDefault(x => x.Index == ci);
                    if (c != null)
                    {
                        WriteItemObject(new Models.TtdCallItem
                        {
                            Index = ci, Name = name,
                            ThreadId = c.ThreadId, TimeStart = c.TimeStart,
                            TimeEnd = c.TimeEnd, ReturnValue = c.ReturnValue,
                            Path = EnsureDrivePrefix(path), Directory = dir,
                        }, path, isContainer: false);
                    }
                }
                break;

            case PathKind.CallsRangeFile when info.Module != null && info.Function != null
                    && info.Index is int cri:
                {
                    var chunk = Store.GetCalls(info.Module, info.Function, skip: cri, take: 1);
                    var c = chunk.FirstOrDefault(x => x.Index == cri);
                    if (c != null)
                    {
                        WriteItemObject(new Models.TtdCallItem
                        {
                            Index = cri, Name = name,
                            ThreadId = c.ThreadId, TimeStart = c.TimeStart,
                            TimeEnd = c.TimeEnd, ReturnValue = c.ReturnValue,
                            Path = EnsureDrivePrefix(path), Directory = dir,
                        }, path, isContainer: false);
                    }
                }
                break;

            case PathKind.CallsRangeFolder when info.Module != null && info.Function != null
                    && info.RangeStart is int rfs && info.RangeEnd is int rfe:
                WriteFolder(name, path, dir,
                    $"calls {rfs}..{rfe} of {info.Module}!{info.Function}",
                    rfe - rfs + 1);
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

            case PathKind.MemoryFirstWriteFile when info.MemStart != null && info.MemEnd != null:
                {
                    var first = Store.GetFirstWrite(info.MemStart, info.MemEnd);
                    if (first != null)
                        WriteMemoryItem(first, name, path, dir);
                }
                break;

            case PathKind.MemoryLastWriteBeforeFile when info.MemStart != null && info.MemEnd != null
                    && info.EncodedPosition != null:
                {
                    var last = Store.GetLastWriteBefore(info.MemStart, info.MemEnd,
                        ResolvePosition(info.EncodedPosition));
                    if (last != null)
                        WriteMemoryItem(last, name, path, dir);
                }
                break;

            case PathKind.PositionFrameFile when info.EncodedPosition != null
                    && info.ThreadId != null && info.FrameIndex is int pfi:
                Store.SeekTo(ResolvePosition(info.EncodedPosition));
                var pframes = Store.GetFramesAtCurrentPosition(info.ThreadId);
                var pf = pframes.FirstOrDefault(x => x.Index == pfi);
                if (pf != null)
                {
                    var psrc = Store.GetSourceLocation(pf.InstructionPointer);
                    WriteItemObject(new Models.TtdPositionFrameItem
                    {
                        Index = pf.Index, Name = name, Frame = pf.Description,
                        SourceFile = psrc?.File, Line = psrc?.Line,
                        Path = EnsureDrivePrefix(path), Directory = dir,
                    }, path, isContainer: false);
                }
                break;

            case PathKind.Summary or PathKind.Timeline or PathKind.Triage
                or PathKind.PositionInfoFile or PathKind.PositionThreadInfoFile
                or PathKind.PositionThreadRegistersFile:
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
        switch (encodedPosition)
        {
            case "start": return $"Lifetime start ({Store.Summary.LifetimeStart})";
            case "end":   return $"Lifetime end ({Store.Summary.LifetimeEnd})";
            case "first-exception":
                return Store.ExceptionEvents.Count > 0
                    ? $"{Store.ExceptionEvents[0].Event.Type} @ {Store.ExceptionEvents[0].Event.Position}" : "";
            case "last-exception":
                return Store.ExceptionEvents.Count > 0
                    ? $"{Store.ExceptionEvents[^1].Event.Type} @ {Store.ExceptionEvents[^1].Event.Position}" : "";
            case "last-meaningful-event":
                return Store.SignificantEvents.Count > 0
                    ? $"{Store.SignificantEvents[^1].Event.Type} @ {Store.SignificantEvents[^1].Event.Position}" : "";
        }
        // Look up event at this encoded hex position.
        var native = Ttd.TtdPosition.Decode(encodedPosition);
        var ev = Store.Events.FirstOrDefault(e => e.Position == native);
        return ev != null ? $"{ev.Type} @ {ev.Position}" : "";
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
            WriteError(new ErrorRecord(ex, "TtdGetChildError", ErrorCategory.NotSpecified, path));
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
                yield return ("timeline.json", false);
                yield return ("timeline", true);
                yield return ("ttd-events", true);
                yield return ("positions", true);
                yield return ("bookmarks", true);
                break;

            case PathKind.BookmarksFolder:
                if (PSDriveInfo is TtdDriveInfo bdrv)
                {
                    foreach (var name in bdrv.Bookmarks.Keys
                                                .OrderBy(x => x, StringComparer.Ordinal))
                        yield return (name, true);
                }
                break;

            case PathKind.TimelineFolder:
                yield return ("events", true);
                yield return ("exceptions", true);
                yield return ("significant", true);
                break;

            case PathKind.TimelineEventsFolder:
                for (int i = 0; i < Store.EventsByPosition.Count; i++)
                    yield return ($"{i}.json", false);
                break;
            case PathKind.TimelineExceptionsFolder:
                for (int i = 0; i < Store.ExceptionEvents.Count; i++)
                    yield return ($"{i}.json", false);
                break;
            case PathKind.TimelineSignificantFolder:
                for (int i = 0; i < Store.SignificantEvents.Count; i++)
                    yield return ($"{i}.json", false);
                break;

            case PathKind.EventsFolder:
                for (int i = 0; i < Store.Events.Count; i++)
                    yield return ($"{i}.json", false);
                break;

            case PathKind.PositionsFolder:
                // Lifetime anchors — always present.
                yield return ("start", true);
                yield return ("end", true);
                // Event-derived aliases — listed only when the backing data exists.
                if (Store.ExceptionEvents.Count > 0)
                {
                    yield return ("first-exception", true);
                    if (Store.ExceptionEvents.Count > 1)
                        yield return ("last-exception", true);
                }
                if (Store.SignificantEvents.Count > 0)
                    yield return ("last-meaningful-event", true);
                // Encoded positions for every unique event location.
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
                yield return ("registers.txt", false);
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
                    var count = Store.GetCallCount(info.Module, info.Function);
                    if (count <= CallPageSize)
                    {
                        for (int i = 0; i < count; i++)
                            yield return ($"{i}.json", false);
                    }
                    else
                    {
                        // Surface pages as <start>-<end>\ instead of thousands of siblings.
                        for (int start = 0; start < count; start += CallPageSize)
                        {
                            var end = Math.Min(start + CallPageSize - 1, count - 1);
                            yield return ($"{start}-{end}", true);
                        }
                    }
                }
                break;

            case PathKind.CallsRangeFolder:
                if (info.RangeStart is int rs && info.RangeEnd is int re)
                {
                    for (int i = rs; i <= re; i++)
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
                yield return ("first-write.json", false);
                yield return ("last-write-before", true);
                break;

            case PathKind.MemoryLastWriteBeforeFolder:
                // Positions aren't enumerable in advance — callers specify by name.
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
                WriteFile("triage.md", MakePath(path, "triage.md"), dir);
                WriteFile("summary.json", MakePath(path, "summary.json"), dir);
                WriteFile("timeline.json", MakePath(path, "timeline.json"), dir);
                WriteFolder("timeline", MakePath(path, "timeline"), dir,
                    "answer-first event views: events (ordered), exceptions, significant", null);
                WriteFolder("ttd-events", MakePath(path, "ttd-events"), dir,
                    "notable events during recording", Store.Summary.EventCount);
                WriteFolder("positions", MakePath(path, "positions"), dir,
                    "navigable time positions", null);
                WriteFolder("calls", MakePath(path, "calls"), dir,
                    "query calls to specific functions: calls\\<module>\\<function>\\", null);
                WriteFolder("bookmarks", MakePath(path, "bookmarks"), dir,
                    "named positions: New-TtdBookmark -Name <n> -Position <pos>",
                    PSDriveInfo is TtdDriveInfo bkDrv ? bkDrv.Bookmarks.Count : (int?)null);
                WriteFolder("memory", MakePath(path, "memory"), dir,
                    "memory access history: memory\\<start>_<end>\\{writes,reads,rw}\\", null);
                break;

            case PathKind.TimelineFolder:
                WriteFolder("events", MakePath(path, "events"), dir,
                    "all events ordered by position", Store.EventsByPosition.Count);
                WriteFolder("exceptions", MakePath(path, "exceptions"), dir,
                    "events where Type matches Exception*", Store.ExceptionEvents.Count);
                WriteFolder("significant", MakePath(path, "significant"), dir,
                    "module loads + thread lifecycle events", Store.SignificantEvents.Count);
                break;

            case PathKind.TimelineEventsFolder:
                for (int i = 0; i < Store.EventsByPosition.Count; i++)
                {
                    if (Stopping) return;
                    WriteEventItem(Store.EventsByPosition[i], $"{i}.json", MakePath(path, $"{i}.json"), dir);
                }
                break;
            case PathKind.TimelineExceptionsFolder:
                for (int i = 0; i < Store.ExceptionEvents.Count; i++)
                {
                    if (Stopping) return;
                    WriteEventItem(Store.ExceptionEvents[i], $"{i}.json", MakePath(path, $"{i}.json"), dir);
                }
                break;
            case PathKind.TimelineSignificantFolder:
                for (int i = 0; i < Store.SignificantEvents.Count; i++)
                {
                    if (Stopping) return;
                    WriteEventItem(Store.SignificantEvents[i], $"{i}.json", MakePath(path, $"{i}.json"), dir);
                }
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

            case PathKind.BookmarksFolder:
                if (PSDriveInfo is TtdDriveInfo bkDrvW)
                {
                    foreach (var kvp in bkDrvW.Bookmarks
                                              .OrderBy(x => x.Key, StringComparer.Ordinal))
                    {
                        if (Stopping) return;
                        var bPath = MakePath(path, kvp.Key);
                        WriteFolder(kvp.Key, bPath, dir,
                            $"→ position {kvp.Value}", null);
                    }
                }
                break;

            case PathKind.CallsFunctionFolder:
                if (info.Module != null && info.Function != null)
                {
                    var count = Store.GetCallCount(info.Module, info.Function);
                    if (count <= CallPageSize)
                    {
                        WriteCallRangeItems(info.Module, info.Function, path, dir, 0, count - 1);
                    }
                    else
                    {
                        for (int start = 0; start < count; start += CallPageSize)
                        {
                            if (Stopping) return;
                            var end = Math.Min(start + CallPageSize - 1, count - 1);
                            var name = $"{start}-{end}";
                            WriteFolder(name, MakePath(path, name), dir,
                                $"calls {start}..{end} of {info.Module}!{info.Function}",
                                end - start + 1);
                        }
                    }
                }
                break;

            case PathKind.CallsRangeFolder:
                if (info.Module != null && info.Function != null
                    && info.RangeStart is int rsCh && info.RangeEnd is int reCh)
                {
                    WriteCallRangeItems(info.Module, info.Function, path, dir, rsCh, reCh);
                }
                break;

            case PathKind.MemoryRangeFolder:
                WriteFolder("writes", MakePath(path, "writes"), dir, "memory writes to this range", null);
                WriteFolder("reads", MakePath(path, "reads"), dir, "memory reads from this range", null);
                WriteFolder("rw", MakePath(path, "rw"), dir, "reads and writes combined", null);
                WriteFile("first-write.json", MakePath(path, "first-write.json"), dir);
                WriteFolder("last-write-before", MakePath(path, "last-write-before"), dir,
                    "pick the last write before <position>.json (position is path-encoded, e.g. 1CBF_8C1)", null);
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
                if (Store.ExceptionEvents.Count > 0)
                {
                    var fx = Store.ExceptionEvents[0];
                    WriteFolder("first-exception",
                        MakePath(path, "first-exception"), dir,
                        $"{fx.Event.Type} @ {fx.Event.Position}", null);
                    if (Store.ExceptionEvents.Count > 1)
                    {
                        var lx = Store.ExceptionEvents[^1];
                        WriteFolder("last-exception",
                            MakePath(path, "last-exception"), dir,
                            $"{lx.Event.Type} @ {lx.Event.Position}", null);
                    }
                }
                if (Store.SignificantEvents.Count > 0)
                {
                    var lm = Store.SignificantEvents[^1];
                    WriteFolder("last-meaningful-event",
                        MakePath(path, "last-meaningful-event"), dir,
                        $"{lm.Event.Type} @ {lm.Event.Position}", null);
                }
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
                WriteFile("registers.txt", MakePath(path, "registers.txt"), dir);
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
                        var src = Store.GetSourceLocation(f.InstructionPointer);
                        WriteItemObject(new Models.TtdPositionFrameItem
                        {
                            Index = f.Index,
                            Name = $"{f.Index}.json",
                            Frame = f.Description,
                            SourceFile = src?.File,
                            Line = src?.Line,
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

            PathKind.Triage => RenderTriage(),

            PathKind.TimelineEventFile when info.Index is int tli
                    && tli >= 0 && tli < Store.EventsByPosition.Count
                => SerializeTimelineEvent(Store.EventsByPosition[tli]),
            PathKind.TimelineExceptionFile when info.Index is int txi
                    && txi >= 0 && txi < Store.ExceptionEvents.Count
                => SerializeTimelineEvent(Store.ExceptionEvents[txi]),
            PathKind.TimelineSignificantFile when info.Index is int tsi
                    && tsi >= 0 && tsi < Store.SignificantEvents.Count
                => SerializeTimelineEvent(Store.SignificantEvents[tsi]),

            PathKind.PositionInfoFile when info.EncodedPosition != null
                => JsonSerializer.Serialize(new
                {
                    Encoded = info.EncodedPosition,
                    Native = ResolvePosition(info.EncodedPosition),
                }, TraceJson.Options),

            PathKind.PositionThreadInfoFile when info.EncodedPosition != null && info.ThreadId != null
                => SerializeThreadInfo(info),

            PathKind.PositionThreadRegistersFile when info.EncodedPosition != null && info.ThreadId != null
                => Store.RenderRegistersAtPosition(ResolvePosition(info.EncodedPosition), info.ThreadId),

            PathKind.PositionFrameFile when info.EncodedPosition != null
                    && info.ThreadId != null && info.FrameIndex is int fi
                => SerializeFrame(info, fi),

            PathKind.CallFile when info.Module != null && info.Function != null
                    && info.Index is int callIdx
                => SerializeCall(info.Module, info.Function, callIdx),

            PathKind.CallsRangeFile when info.Module != null && info.Function != null
                    && info.Index is int rnCallIdx
                => SerializeCall(info.Module, info.Function, rnCallIdx),

            PathKind.MemoryAccessFile when info.MemStart != null && info.MemEnd != null
                    && info.AccessMode != null && info.Index is int memIdx
                => SerializeMemoryAccess(info.MemStart, info.MemEnd, info.AccessMode, memIdx),

            PathKind.MemoryFirstWriteFile when info.MemStart != null && info.MemEnd != null
                => JsonSerializer.Serialize(
                    Store.GetFirstWrite(info.MemStart, info.MemEnd) ?? (object)new { },
                    TraceJson.Options),

            PathKind.MemoryLastWriteBeforeFile when info.MemStart != null && info.MemEnd != null
                    && info.EncodedPosition != null
                => JsonSerializer.Serialize(
                    Store.GetLastWriteBefore(info.MemStart, info.MemEnd,
                        ResolvePosition(info.EncodedPosition)) ?? (object)new { },
                    TraceJson.Options),

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
        if (index < 0) return "{}";
        // Targeted single-record fetch; the paged GetCalls overload pins Index
        // to the absolute position so we can match by identity.
        var chunk = Store.GetCalls(module, function, skip: index, take: 1);
        var call = chunk.FirstOrDefault(c => c.Index == index);
        if (call == null) return "{}";
        // Parameters are per-call + lazy to avoid N×param queries during enum.
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

    private void WriteMemoryItem(TtdMemoryAccess r, string name, string itemPath, string directory)
    {
        WriteItemObject(new Models.TtdMemoryItem
        {
            Index = r.Index,
            Name = name,
            Position = r.TimeStart,
            AccessType = r.AccessType,
            Address = r.Address,
            Value = r.Value,
            Path = EnsureDrivePrefix(itemPath),
            Directory = directory,
        }, itemPath, isContainer: false);
    }

    private void WriteEventItem(TtdEventWithIndex entry, string name, string itemPath, string directory)
    {
        WriteItemObject(new Models.TtdEventItem
        {
            Index = entry.OriginalIndex,
            Name = name,
            Position = entry.Event.Position,
            Type = entry.Event.Type,
            Module = entry.Event.Module,
            Path = EnsureDrivePrefix(itemPath),
            Directory = directory,
        }, itemPath, isContainer: false);
    }

    private string SerializeTimelineEvent(TtdEventWithIndex entry) =>
        JsonSerializer.Serialize(new
        {
            entry.OriginalIndex,
            entry.Event.Position,
            entry.Event.Type,
            entry.Event.Module,
        }, TraceJson.Options);

    private string RenderTriage()
    {
        var s = Store.Summary;
        var sb = new System.Text.StringBuilder();
        sb.Append("# TTD Triage: ").AppendLine(Path.GetFileName(s.FilePath));
        sb.AppendLine();
        sb.Append("**Recording:** `").Append(s.FilePath).Append("` (")
          .Append(FormatBytes(s.FileSizeBytes)).AppendLine(")");
        sb.Append("**Lifetime:** ").Append(s.LifetimeStart).Append(" → ").AppendLine(s.LifetimeEnd);
        sb.Append("**Threads:** ").Append(s.ThreadCount).AppendLine();
        sb.Append("**Modules:** ").Append(s.ModuleCount).AppendLine();
        sb.Append("**Events:** ").Append(s.EventCount).AppendLine();
        sb.AppendLine();

        var excs = Store.ExceptionEvents;
        sb.Append("## Exceptions (").Append(excs.Count).AppendLine(")");
        sb.AppendLine();
        if (excs.Count == 0)
        {
            sb.AppendLine("No exceptions recorded.");
        }
        else
        {
            foreach (var x in excs.Take(20))
                sb.Append("- [").Append(x.OriginalIndex).Append("] `")
                  .Append(x.Event.Position).Append("` ").Append(x.Event.Type)
                  .Append(' ').AppendLine(x.Event.Module);
            if (excs.Count > 20) sb.Append("- ... (+").Append(excs.Count - 20).AppendLine(" more)");
        }
        sb.AppendLine();

        var sig = Store.SignificantEvents;
        sb.Append("## Significant events (").Append(sig.Count).AppendLine(")");
        sb.AppendLine();
        if (sig.Count == 0)
        {
            sb.AppendLine("None.");
        }
        else
        {
            foreach (var x in sig.Take(25))
                sb.Append("- [").Append(x.OriginalIndex).Append("] `")
                  .Append(x.Event.Position).Append("` ").Append(x.Event.Type)
                  .Append(' ').AppendLine(x.Event.Module);
            if (sig.Count > 25) sb.Append("- ... (+").Append(sig.Count - 25).AppendLine(" more)");
        }
        sb.AppendLine();

        sb.AppendLine("## Where to look next");
        sb.AppendLine();
        sb.AppendLine("- `timeline\\exceptions\\` — all exceptions by position");
        sb.AppendLine("- `timeline\\significant\\` — module loads + thread lifecycle");
        sb.AppendLine("- `timeline\\events\\` — full event list by position");
        sb.AppendLine("- `positions\\<pos>\\threads\\` — thread state at a position");
        sb.AppendLine("- `calls\\<module>\\<function>\\` — call history for a function");
        sb.AppendLine("- `memory\\<start>_<end>\\` — memory access history for an address range");
        return sb.ToString();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.0} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";
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

    /// <summary>Write one TtdCallItem per index in [start..end] inclusive.
    /// Shared by the flat (≤256 total) and paged (range-folder) code paths.</summary>
    private void WriteCallRangeItems(string module, string function, string path,
        string directory, int start, int end)
    {
        if (end < start) return;
        var chunk = Store.GetCalls(module, function, skip: start, take: end - start + 1);
        foreach (var c in chunk)
        {
            if (Stopping) return;
            var childName = $"{c.Index}.json";
            var childPath = MakePath(path, childName);
            WriteItemObject(new Models.TtdCallItem
            {
                Index = c.Index,
                Name = childName,
                ThreadId = c.ThreadId,
                TimeStart = c.TimeStart,
                TimeEnd = c.TimeEnd,
                ReturnValue = c.ReturnValue,
                Path = EnsureDrivePrefix(childPath),
                Directory = directory,
            }, childPath, isContainer: false);
        }
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
