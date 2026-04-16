using System.Management.Automation;
using CrashDrive.Trace;

namespace CrashDrive.Provider;

/// <summary>
/// Custom PSDriveInfo: one crash/trace file per drive. Lazy — the trace is
/// not parsed until something tries to access event data.
/// </summary>
public sealed class CrashDriveInfo : PSDriveInfo
{
    public string SourceFile { get; }
    public string? SymbolPath { get; }
    public TraceStore Trace { get; }

    public CrashDriveInfo(PSDriveInfo inner, string displayRoot, string sourceFile, string? symbolPath)
        : base(inner.Name, inner.Provider, inner.Root,
               inner.Description, inner.Credential, displayRoot)
    {
        SourceFile = sourceFile;
        SymbolPath = symbolPath;
        Trace = new TraceStore(sourceFile);
    }
}
