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
    /// <summary>Source file where the frame's IP resolves, or null if
    /// unavailable (stripped PDB, public symbols, missing source indexing).
    /// Consumed by editor-follow.</summary>
    public string? SourceFile { get; set; }
    public int? Line { get; set; }
    public string Path { get; set; } = "";
    public string Directory { get; set; } = "";
}

/// <summary>Aggregate stats for one type on the GC heap.</summary>
public class HeapTypeItem
{
    public string Name { get; set; } = "";
    public string TypeName { get; set; } = "";
    public int InstanceCount { get; set; }
    public long TotalBytes { get; set; }
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
    public bool IsManaged { get; set; }
    public string Path { get; set; } = "";
    public string Directory { get; set; } = "";
}

/// <summary>TTD notable event (ModuleLoaded, ThreadCreated, etc.) as a PSDrive entry.</summary>
public class TtdEventItem
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string Position { get; set; } = "";
    public string Type { get; set; } = "";
    public string Module { get; set; } = "";
    public string Path { get; set; } = "";
    public string Directory { get; set; } = "";
}

/// <summary>TTD call record (one invocation of a function).</summary>
public class TtdCallItem
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string ThreadId { get; set; } = "";
    public string TimeStart { get; set; } = "";
    public string TimeEnd { get; set; } = "";
    public string ReturnValue { get; set; } = "";
    public string Path { get; set; } = "";
    public string Directory { get; set; } = "";
}

/// <summary>TTD stack frame at a specific time position.</summary>
public class TtdPositionFrameItem
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string Frame { get; set; } = "";   // e.g. "ntdll!LdrInitializeThunk"
    public string? SourceFile { get; set; }
    public int? Line { get; set; }
    public string Path { get; set; } = "";
    public string Directory { get; set; } = "";
}

/// <summary>TTD memory access record.</summary>
public class TtdMemoryItem
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string Position { get; set; } = "";
    public string AccessType { get; set; } = "";
    public string Address { get; set; } = "";
    public string Value { get; set; } = "";
    public string Path { get; set; } = "";
    public string Directory { get; set; } = "";
}
