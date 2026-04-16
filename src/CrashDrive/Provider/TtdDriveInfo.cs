using System.Management.Automation;
using CrashDrive.Ttd;

namespace CrashDrive.Provider;

/// <summary>Drive for a Time Travel Debugging (.run) trace file.</summary>
public sealed class TtdDriveInfo : PSDriveInfo
{
    public string SourceFile { get; }
    public string? SymbolPath { get; }
    public TtdStore Store { get; }

    public TtdDriveInfo(PSDriveInfo inner, string sourceFile, string? symbolPath)
        : base(inner.Name, inner.Provider, inner.Root,
               inner.Description, inner.Credential, displayRoot: sourceFile)
    {
        SourceFile = sourceFile;
        SymbolPath = symbolPath;
        Store = new TtdStore(sourceFile, symbolPath);
    }
}
