using System.Management.Automation;
using CrashDrive.Dump;

namespace CrashDrive.Provider;

/// <summary>Drive for a Windows crash dump (.dmp) file.</summary>
public sealed class DumpDriveInfo : PSDriveInfo
{
    public string SourceFile { get; }
    public string? SymbolPath { get; }
    public DumpStore Store { get; }

    public DumpDriveInfo(PSDriveInfo inner, string sourceFile, string? symbolPath)
        : base(inner.Name, inner.Provider, inner.Root,
               inner.Description, inner.Credential, displayRoot: sourceFile)
    {
        SourceFile = sourceFile;
        SymbolPath = symbolPath;
        Store = new DumpStore(sourceFile, symbolPath);
    }
}
