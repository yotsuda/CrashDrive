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
        Invalid,
    }

    private readonly record struct ParsedPath(PathKind Kind, string[] Segments, int? Index = null);

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
                _ => new(PathKind.Invalid, segs),
            };
        }
        if (head == "ttd-events" && segs.Length == 2
            && segs[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(segs[1][..^5], out var idx))
            return new(PathKind.EventFile, segs, Index: idx);

        return new(PathKind.Invalid, segs);
    }

    // === Path operations ===

    protected override bool ItemExists(string path)
        => Parse(NormalizePath(path)).Kind != PathKind.Invalid;

    protected override bool IsItemContainer(string path)
        => Parse(NormalizePath(path)).Kind is PathKind.Root or PathKind.EventsFolder;

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
                break;
            case PathKind.EventsFolder:
                for (int i = 0; i < Store.Events.Count; i++)
                    yield return ($"{i}.json", false);
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
                break;
            case PathKind.EventsFolder:
                for (int i = 0; i < Store.Events.Count; i++)
                {
                    if (Stopping) return;
                    WriteFile($"{i}.json", MakePath(path, $"{i}.json"), dir);
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
            _ => "",
        };
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
