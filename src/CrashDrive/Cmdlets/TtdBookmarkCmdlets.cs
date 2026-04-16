using System.Management.Automation;
using CrashDrive.Provider;
using CrashDrive.Ttd;

namespace CrashDrive.Cmdlets;

/// <summary>
/// Pin a TTD position under a memorable name. Exposes
/// <c>ttd:\bookmarks\&lt;name&gt;\</c>, which mirrors the layout of
/// <c>ttd:\positions\&lt;encoded&gt;\</c> (position.json, threads/…). Session-local —
/// bookmarks live on the drive's <see cref="TtdDriveInfo"/> and vanish when the drive
/// is removed.
/// </summary>
[Cmdlet(VerbsCommon.New, "TtdBookmark")]
[OutputType(typeof(PSObject))]
public sealed class NewTtdBookmarkCmdlet : PSCmdlet
{
    /// <summary>Bookmark name. Must not contain path separators.</summary>
    [Parameter(Mandatory = true, Position = 0)]
    public string Name { get; set; } = "";

    /// <summary>Position to bookmark. Accepts native <c>major:minor</c>, path-form
    /// <c>major_minor</c>, or the aliases <c>start</c> / <c>end</c>.</summary>
    [Parameter(Mandatory = true, Position = 1)]
    public string Position { get; set; } = "";

    /// <summary>Drive name. If omitted, inferred from the current PS location.</summary>
    [Parameter]
    public string? Drive { get; set; }

    protected override void EndProcessing()
    {
        if (Name.Length == 0 || Name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
        {
            WriteError(new ErrorRecord(
                new ArgumentException("Bookmark name cannot be empty or contain '/', '\\', or ':'."),
                "BadName", ErrorCategory.InvalidArgument, Name));
            return;
        }

        var drive = ResolveTtdDrive();
        if (drive == null) return;

        var native = ResolveNative(drive);
        if (native == null)
        {
            WriteError(new ErrorRecord(
                new ArgumentException($"Invalid position: {Position}"),
                "BadPosition", ErrorCategory.InvalidArgument, Position));
            return;
        }

        drive.Bookmarks[Name] = native;
        WriteObject(new PSObject(new
        {
            Name,
            Position = native,
            Drive = drive.Name,
        }));
    }

    private string? ResolveNative(TtdDriveInfo drive)
    {
        var trimmed = Position.Trim();
        if (trimmed.Equals("start", StringComparison.OrdinalIgnoreCase))
            return drive.Store.Summary.LifetimeStart;
        if (trimmed.Equals("end", StringComparison.OrdinalIgnoreCase))
            return drive.Store.Summary.LifetimeEnd;
        // Accept both native (colon) and path-form (underscore).
        var candidate = trimmed.Contains(':') ? trimmed : trimmed.Replace('_', ':');
        return TtdPosition.IsValid(TtdPosition.Encode(candidate)) ? candidate : null;
    }

    private TtdDriveInfo? ResolveTtdDrive()
    {
        PSDriveInfo? d = !string.IsNullOrEmpty(Drive)
            ? SessionState.Drive.Get(Drive)
            : SessionState.Path.CurrentLocation.Drive;
        if (d is TtdDriveInfo t) return t;
        WriteError(new ErrorRecord(
            new InvalidOperationException(
                $"Drive '{d?.Name ?? Drive ?? "<current>"}' is not a CrashDrive Ttd drive."),
            "NotATtdDrive", ErrorCategory.InvalidOperation, d));
        return null;
    }
}

/// <summary>List bookmarks on a Ttd drive, or look up one by name.</summary>
[Cmdlet(VerbsCommon.Get, "TtdBookmark")]
[OutputType(typeof(PSObject))]
public sealed class GetTtdBookmarkCmdlet : PSCmdlet
{
    [Parameter(Position = 0)]
    public string? Name { get; set; }

    [Parameter]
    public string? Drive { get; set; }

    protected override void EndProcessing()
    {
        var drive = ResolveTtdDrive();
        if (drive == null) return;

        if (!string.IsNullOrEmpty(Name))
        {
            if (drive.Bookmarks.TryGetValue(Name, out var native))
                WriteObject(new PSObject(new { Name, Position = native, Drive = drive.Name }));
            else
                WriteError(new ErrorRecord(
                    new ItemNotFoundException($"Bookmark '{Name}' not found on drive '{drive.Name}'."),
                    "BookmarkNotFound", ErrorCategory.ObjectNotFound, Name));
            return;
        }

        foreach (var kvp in drive.Bookmarks.OrderBy(x => x.Key, StringComparer.Ordinal))
            WriteObject(new PSObject(new { Name = kvp.Key, Position = kvp.Value, Drive = drive.Name }));
    }

    private TtdDriveInfo? ResolveTtdDrive()
    {
        PSDriveInfo? d = !string.IsNullOrEmpty(Drive)
            ? SessionState.Drive.Get(Drive)
            : SessionState.Path.CurrentLocation.Drive;
        if (d is TtdDriveInfo t) return t;
        WriteError(new ErrorRecord(
            new InvalidOperationException(
                $"Drive '{d?.Name ?? Drive ?? "<current>"}' is not a CrashDrive Ttd drive."),
            "NotATtdDrive", ErrorCategory.InvalidOperation, d));
        return null;
    }
}

/// <summary>Delete a bookmark by name. Silent no-op if the name doesn't exist.</summary>
[Cmdlet(VerbsCommon.Remove, "TtdBookmark")]
public sealed class RemoveTtdBookmarkCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    public string Name { get; set; } = "";

    [Parameter]
    public string? Drive { get; set; }

    protected override void EndProcessing()
    {
        var drive = ResolveTtdDrive();
        if (drive == null) return;
        drive.Bookmarks.TryRemove(Name, out _);
    }

    private TtdDriveInfo? ResolveTtdDrive()
    {
        PSDriveInfo? d = !string.IsNullOrEmpty(Drive)
            ? SessionState.Drive.Get(Drive)
            : SessionState.Path.CurrentLocation.Drive;
        if (d is TtdDriveInfo t) return t;
        WriteError(new ErrorRecord(
            new InvalidOperationException(
                $"Drive '{d?.Name ?? Drive ?? "<current>"}' is not a CrashDrive Ttd drive."),
            "NotATtdDrive", ErrorCategory.InvalidOperation, d));
        return null;
    }
}
