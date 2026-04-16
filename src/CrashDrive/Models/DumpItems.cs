namespace CrashDrive.Models;

/// <summary>A thread in a crash dump — folder container.</summary>
public class ThreadItem
{
    public int ManagedThreadId { get; set; }
    public uint OSThreadId { get; set; }
    public string GCMode { get; set; } = "";
    public bool IsAlive { get; set; }
    public bool IsFinalizer { get; set; }
    public int FrameCount { get; set; }
    public string? ExceptionSummary { get; set; }
    public string Path { get; set; } = "";
    public string Directory { get; set; } = "";
}

/// <summary>A single stack frame in a dump's thread.</summary>
public class FrameItem
{
    public int Index { get; set; }
    public string Method { get; set; } = "";
    public string? Module { get; set; }
    public string Kind { get; set; } = "";
    public string IpHex { get; set; } = "";
    public string Path { get; set; } = "";
    public string Directory { get; set; } = "";
}

/// <summary>A loaded module in the dumped process.</summary>
public class ModuleItem
{
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public string ImageBaseHex { get; set; } = "";
    public bool IsDynamic { get; set; }
    public string Path { get; set; } = "";
    public string Directory { get; set; } = "";
}
