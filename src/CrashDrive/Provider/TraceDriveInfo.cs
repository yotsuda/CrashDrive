using System.Management.Automation;
using CrashDrive.Trace;

namespace CrashDrive.Provider;

/// <summary>Drive for a JSONL execution trace file.</summary>
public sealed class TraceDriveInfo : PSDriveInfo
{
    public string SourceFile { get; }
    public TraceStore Store { get; }

    public TraceDriveInfo(PSDriveInfo inner, string sourceFile)
        : base(inner.Name, inner.Provider, inner.Root,
               inner.Description, inner.Credential, displayRoot: sourceFile)
    {
        SourceFile = sourceFile;
        Store = new TraceStore(sourceFile);
    }
}
