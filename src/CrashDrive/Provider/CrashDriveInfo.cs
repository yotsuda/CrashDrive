using System.Management.Automation;
using CrashDrive.Store;
using CrashDrive.Trace;
using CrashDrive.Dump;
using CrashDrive.Ttd;

namespace CrashDrive.Provider;

/// <summary>
/// Custom PSDriveInfo: one crash/trace file per drive. The backing <see cref="IStore"/>
/// is opened lazily (constructor only stats the file; heavy parse deferred).
/// </summary>
public sealed class CrashDriveInfo : PSDriveInfo
{
    public string SourceFile { get; }
    public string? SymbolPath { get; }
    public IStore Store { get; }

    public TraceStore? AsTrace => Store as TraceStore;
    public DumpStore? AsDump => Store as DumpStore;
    public TtdStore? AsTtd => Store as TtdStore;

    /// <summary>
    /// Convenience accessor used by trace-specific code paths.
    /// Throws if this drive is not backed by a trace file — callers should
    /// gate on <see cref="Store"/>.<see cref="IStore.Kind"/> first.
    /// </summary>
    public TraceStore Trace => AsTrace
        ?? throw new InvalidOperationException(
            $"Drive '{Name}' is a {Store.Kind}, not a Trace. Use dump-specific paths (threads\\, modules\\).");

    public CrashDriveInfo(PSDriveInfo inner, string displayRoot, string sourceFile, string? symbolPath)
        : base(inner.Name, inner.Provider, inner.Root,
               inner.Description, inner.Credential, displayRoot)
    {
        SourceFile = sourceFile;
        SymbolPath = symbolPath;
        Store = StoreFactory.Open(sourceFile, symbolPath);
    }
}
