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
        Root, Summary, Info, Timeline,
        EventsFolder, EventFile,
        PositionsFolder,                            // positions/
        PositionFolder,                             // positions/<pos>/
        PositionInfoFile,                           // positions/<pos>/position.json
        PositionThreadsFolder,                      // positions/<pos>/threads/
        PositionThreadFolder,                       // positions/<pos>/threads/<tid>/
        PositionThreadInfoFile,                     // positions/<pos>/threads/<tid>/info.json
        PositionThreadFramesFolder,                 // positions/<pos>/threads/<tid>/frames/
        PositionFrameFile,                          // positions/<pos>/threads/<tid>/frames/<n>.json
        Invalid,
    }

    private readonly record struct ParsedPath(
        PathKind Kind,
        string[] Segments,
        int? Index = null,
        string? EncodedPosition = null,
        string? ThreadId = null,
        int? FrameIndex = null);

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
                "info.json" => new(PathKind.Info, segs),
                "timeline.json" => new(PathKind.Timeline, segs),
                "ttd-events" => new(PathKind.EventsFolder, segs),
                "positions" => new(PathKind.PositionsFolder, segs),
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

        return new(PathKind.Invalid, segs);
    }

    /// <summary>Resolve an encoded position (including "start"/"end") to the native TTD form.</summary>
    private string ResolvePosition(string encoded) => encoded switch
    {
        "start" => Store.Summary.LifetimeStart,
        "end" => Store.Summary.LifetimeEnd,
        _ => Ttd.TtdPosition.Decode(encoded),
    };

    // === Path operations ===

    protected override bool ItemExists(string path)
        => Parse(NormalizePath(path)).Kind != PathKind.Invalid;

    protected override bool IsItemContainer(string path)
    {
        return Parse(NormalizePath(path)).Kind switch
        {
            PathKind.Root or PathKind.EventsFolder or
            PathKind.PositionsFolder or PathKind.PositionFolder or
            PathKind.PositionThreadsFolder or PathKind.PositionThreadFolder or
            PathKind.PositionThreadFramesFolder => true,
            _ => false,
        };
    }

    protected override bool HasChildItems(string path) => IsItemContainer(path);

    protected override void GetItem(string path)
    {
        var parent = GetParentPath(path, null);
        var dir = EnsureDrivePrefix(parent);
        WriteFolder(Path.GetFileName(path), path, dir, "", null);
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
                yield return ("info.json", false);
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
        }
    }

    private void WriteChildren(ParsedPath info, string path)
    {
        var dir = EnsureDrivePrefix(path);
        switch (info.Kind)
        {
            case PathKind.Root:
                WriteFile("summary.json", MakePath(path, "summary.json"), dir);
                WriteFile("info.json", MakePath(path, "info.json"), dir);
                WriteFile("timeline.json", MakePath(path, "timeline.json"), dir);
                WriteFolder("ttd-events", MakePath(path, "ttd-events"), dir,
                    "notable events during recording", Store.Summary.EventCount);
                WriteFolder("positions", MakePath(path, "positions"), dir,
                    "navigable time positions", null);
                break;

            case PathKind.EventsFolder:
                for (int i = 0; i < Store.Events.Count; i++)
                {
                    if (Stopping) return;
                    WriteFile($"{i}.json", MakePath(path, $"{i}.json"), dir);
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
                        WriteFile($"{f.Index}.json", MakePath(path, $"{f.Index}.json"), dir);
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
            PathKind.Info => JsonSerializer.Serialize(Store.Summary, TraceJson.Options),
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

            _ => "",
        };
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
