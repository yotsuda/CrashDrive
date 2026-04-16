using System.Collections.Concurrent;
using System.Management.Automation;
using CrashDrive.Ttd;

namespace CrashDrive.Provider;

/// <summary>Drive for a Time Travel Debugging (.run) trace file.</summary>
public sealed class TtdDriveInfo : PSDriveInfo
{
    public string SourceFile { get; }
    public string? SymbolPath { get; }
    public TtdStore Store { get; }

    /// <summary>Session-local name→native-position map. Exposed as
    /// <c>ttd:\bookmarks\&lt;name&gt;\</c> which mirrors the structure under
    /// <c>ttd:\positions\&lt;encoded&gt;\</c> (position.json, threads/…).
    /// Populated via <c>New-TtdBookmark</c>; not persisted across sessions.</summary>
    public ConcurrentDictionary<string, string> Bookmarks { get; }
        = new(StringComparer.Ordinal);

    public TtdDriveInfo(PSDriveInfo inner, string sourceFile, string? symbolPath)
        : base(inner.Name, inner.Provider, inner.Root,
               inner.Description, inner.Credential, displayRoot: sourceFile)
    {
        SourceFile = sourceFile;
        SymbolPath = symbolPath;
        Store = new TtdStore(sourceFile, symbolPath);
    }
}
