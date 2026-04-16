using System.Runtime.InteropServices;

namespace CrashDrive.DbgEng;

/// <summary>
/// Minimal DbgEng COM interop for opening dumps / TTD traces and executing
/// Debugger Data Model queries. Covers just enough surface to back the
/// TtdStore: create client → open file → wait for event → execute "dx ..."
/// commands and capture their output.
/// </summary>
internal static class DbgEngNative
{
    [DllImport("dbgeng.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    [return: MarshalAs(UnmanagedType.Interface)]
    internal static extern object DebugCreate(in Guid InterfaceId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetDllDirectory(string? lpPathName);

    internal const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

    /// <summary>
    /// Locate a dbgeng.dll that supports TTD (.run) files. The system32 copy
    /// doesn't; WinDbg Preview ships a newer one at
    /// <c>%ProgramFiles%\WindowsApps\Microsoft.WinDbg_*\amd64\dbgeng.dll</c>.
    /// Returns the directory containing that DLL, or null if not found.
    /// </summary>
    public static string? LocateWinDbgDbgEngDirectory()
    {
        // Honor explicit override.
        var env = Environment.GetEnvironmentVariable("CRASHDRIVE_DBGENG_DIR");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, "dbgeng.dll")))
            return env;

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "amd64",
        };

        // C:\Program Files\WindowsApps is ACL-protected and can't be enumerated from
        // most processes; Get-AppxPackage via a PowerShell subprocess is the reliable
        // way to discover the install location.
        var installLocation = QueryAppxInstallLocation("Microsoft.WinDbg");
        if (installLocation == null) return null;

        var candidate = Path.Combine(installLocation, arch);
        if (File.Exists(Path.Combine(candidate, "dbgeng.dll"))
            && File.Exists(Path.Combine(candidate, "ttd", "TTDReplay.dll")))
        {
            return candidate;
        }
        return null;
    }

    private static string? QueryAppxInstallLocation(string packageNameContains)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powershell")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add($"(Get-AppxPackage -Name '*{packageNameContains}*' | Select-Object -First 1).InstallLocation");

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;
            if (!proc.WaitForExit(10_000))
            {
                try { proc.Kill(); } catch { }
                return null;
            }
            var output = proc.StandardOutput.ReadToEnd().Trim();
            return string.IsNullOrEmpty(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }

    private static bool s_dbgEngLoaded;
    private static readonly object s_loadLock = new();

    /// <summary>
    /// Pre-load a TTD-capable dbgeng.dll so subsequent [DllImport("dbgeng.dll")]
    /// resolves to it. Idempotent.
    /// </summary>
    public static bool TryLoadWinDbgDbgEng()
    {
        if (s_dbgEngLoaded) return true;
        lock (s_loadLock)
        {
            if (s_dbgEngLoaded) return true;
            var dir = LocateWinDbgDbgEngDirectory();
            if (dir == null) return false;

            // Ensure the loader finds sibling DLLs (dbgcore, dbghelp, ttd\TTDReplay, etc.)
            SetDllDirectory(dir);

            var dbgengPath = Path.Combine(dir, "dbgeng.dll");
            var h = LoadLibraryExW(dbgengPath, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
            if (h == IntPtr.Zero) return false;

            s_dbgEngLoaded = true;
            return true;
        }
    }
}

// ─── IIDs ──────────────────────────────────────────────────────────

internal static class DbgEngIIDs
{
    public static readonly Guid IDebugClient5 = new("e3acb9d7-7ec2-4f0c-a0da-e81e0cbbe628");
    public static readonly Guid IDebugControl7 = new("b86fb3b1-80d4-475b-aea3-cf06539cf63a");
    public static readonly Guid IDebugSymbols3 = new("f02fbecc-50ac-4f36-9ad9-c975e8f32ff8");
    public static readonly Guid IDebugSystemObjects4 = new("489468e6-7d0f-4af5-87ab-25207454d553");
}

// ─── Enums ─────────────────────────────────────────────────────────

[Flags]
internal enum DebugEnd : uint
{
    Active_Terminate = 0x00000000,
    Active_Detach = 0x00000001,
    Passive = 0x00000002,
    ReentrantQuiet = 0x00000003,
}

[Flags]
internal enum DebugWait : uint
{
    Default = 0,
}

[Flags]
internal enum DebugOutctl : uint
{
    ThisClient = 0x00000000,
    AllClients = 0x00000001,
    AllOtherClients = 0x00000002,
    Ignore = 0x00000003,
    LogOnly = 0x00000004,
    OverrideMask = 0x0000000F,
    NotLogged = 0x00000010,
    OverrideText = 0x00000020,
}

[Flags]
internal enum DebugExecute : uint
{
    Default = 0x00000000,
    Echo = 0x00000001,
    NotLogged = 0x00000002,
    NoRepeat = 0x00000004,
}

[Flags]
internal enum DebugOutput : uint
{
    Normal = 0x00000001,
    Error = 0x00000002,
    Warning = 0x00000004,
    Verbose = 0x00000008,
    Prompt = 0x00000010,
    PromptRegisters = 0x00000020,
    ExtensionWarning = 0x00000040,
    Debuggee = 0x00000080,
    DebuggeePrompt = 0x00000100,
    Symbols = 0x00000200,
    All = 0xFFFFFFFF,
}

// ─── Interfaces (minimal surface) ─────────────────────────────────

[Guid("e3acb9d7-7ec2-4f0c-a0da-e81e0cbbe628")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[ComImport]
internal interface IDebugClient5
{
    // --- IDebugClient ---
    [PreserveSig] int AttachKernel(uint Flags, [MarshalAs(UnmanagedType.LPStr)] string? ConnectOptions);
    [PreserveSig] int GetKernelConnectionOptions(IntPtr Buffer, uint BufferSize, out uint OptionsSize);
    [PreserveSig] int SetKernelConnectionOptions([MarshalAs(UnmanagedType.LPStr)] string Options);
    [PreserveSig] int StartProcessServer(uint Flags, [MarshalAs(UnmanagedType.LPStr)] string Options, IntPtr Reserved);
    [PreserveSig] int ConnectProcessServer([MarshalAs(UnmanagedType.LPStr)] string RemoteOptions, out ulong Server);
    [PreserveSig] int DisconnectProcessServer(ulong Server);
    [PreserveSig] int GetRunningProcessSystemIds(ulong Server, IntPtr Ids, uint Count, out uint ActualCount);
    [PreserveSig] int GetRunningProcessSystemIdByExecutableName(ulong Server, [MarshalAs(UnmanagedType.LPStr)] string ExeName, uint Flags, out uint Id);
    [PreserveSig] int GetRunningProcessDescription(ulong Server, uint SystemId, uint Flags, IntPtr ExeName, uint ExeNameSize, out uint ActualExeNameSize, IntPtr Description, uint DescriptionSize, out uint ActualDescriptionSize);
    [PreserveSig] int AttachProcess(ulong Server, uint ProcessID, uint AttachFlags);
    [PreserveSig] int CreateProcess(ulong Server, [MarshalAs(UnmanagedType.LPStr)] string CommandLine, uint CreateFlags);
    [PreserveSig] int CreateProcessAndAttach(ulong Server, [MarshalAs(UnmanagedType.LPStr)] string? CommandLine, uint CreateFlags, uint ProcessId, uint AttachFlags);
    [PreserveSig] int GetProcessOptions(out uint Options);
    [PreserveSig] int AddProcessOptions(uint Options);
    [PreserveSig] int RemoveProcessOptions(uint Options);
    [PreserveSig] int SetProcessOptions(uint Options);
    [PreserveSig] int OpenDumpFile([MarshalAs(UnmanagedType.LPStr)] string DumpFile);
    [PreserveSig] int WriteDumpFile([MarshalAs(UnmanagedType.LPStr)] string DumpFile, uint Qualifier);
    [PreserveSig] int ConnectSession(uint Flags, uint HistoryLimit);
    [PreserveSig] int StartServer([MarshalAs(UnmanagedType.LPStr)] string Options);
    [PreserveSig] int OutputServers(uint OutputControl, [MarshalAs(UnmanagedType.LPStr)] string Machine, uint Flags);
    [PreserveSig] int TerminateProcesses();
    [PreserveSig] int DetachProcesses();
    [PreserveSig] int EndSession(DebugEnd Flags);
    [PreserveSig] int GetExitCode(out uint Code);
    [PreserveSig] int DispatchCallbacks(uint Timeout);
    [PreserveSig] int ExitDispatch([MarshalAs(UnmanagedType.Interface)] IDebugClient5 Client);
    [PreserveSig] int CreateClient(out IntPtr Client);
    [PreserveSig] int GetInputCallbacks(out IntPtr Callbacks);
    [PreserveSig] int SetInputCallbacks(IntPtr Callbacks);
    [PreserveSig] int GetOutputCallbacks(out IntPtr Callbacks);
    [PreserveSig] int SetOutputCallbacks([MarshalAs(UnmanagedType.Interface)] IDebugOutputCallbacks? Callbacks);
    [PreserveSig] int GetOutputMask(out uint Mask);
    [PreserveSig] int SetOutputMask(uint Mask);
    [PreserveSig] int GetOtherOutputMask(IntPtr Client, out uint Mask);
    [PreserveSig] int SetOtherOutputMask(IntPtr Client, uint Mask);
    [PreserveSig] int GetOutputWidth(out uint Columns);
    [PreserveSig] int SetOutputWidth(uint Columns);
    [PreserveSig] int GetOutputLinePrefix(IntPtr Buffer, uint BufferSize, out uint PrefixSize);
    [PreserveSig] int SetOutputLinePrefix([MarshalAs(UnmanagedType.LPStr)] string Prefix);
    [PreserveSig] int GetIdentity(IntPtr Buffer, uint BufferSize, out uint IdentitySize);
    [PreserveSig] int OutputIdentity(uint OutputControl, uint Flags, [MarshalAs(UnmanagedType.LPStr)] string Format);
    [PreserveSig] int GetEventCallbacks(out IntPtr Callbacks);
    [PreserveSig] int SetEventCallbacks(IntPtr Callbacks);
    [PreserveSig] int FlushCallbacks();

    // --- IDebugClient2 ---
    [PreserveSig] int WriteDumpFile2([MarshalAs(UnmanagedType.LPStr)] string DumpFile, uint Qualifier, uint FormatFlags, [MarshalAs(UnmanagedType.LPStr)] string Comment);
    [PreserveSig] int AddDumpInformationFile([MarshalAs(UnmanagedType.LPStr)] string InfoFile, uint Type);
    [PreserveSig] int EndProcessServer(ulong Server);
    [PreserveSig] int WaitForProcessServerEnd(uint Timeout);
    [PreserveSig] int IsKernelDebuggerEnabled();
    [PreserveSig] int TerminateCurrentProcess();
    [PreserveSig] int DetachCurrentProcess();
    [PreserveSig] int AbandonCurrentProcess();

    // --- IDebugClient3 ---
    [PreserveSig] int GetRunningProcessSystemIdByExecutableNameWide(ulong Server, [MarshalAs(UnmanagedType.LPWStr)] string ExeName, uint Flags, out uint Id);
    [PreserveSig] int GetRunningProcessDescriptionWide(ulong Server, uint SystemId, uint Flags, IntPtr ExeName, uint ExeNameSize, out uint ActualExeNameSize, IntPtr Description, uint DescriptionSize, out uint ActualDescriptionSize);
    [PreserveSig] int CreateProcessWide(ulong Server, [MarshalAs(UnmanagedType.LPWStr)] string CommandLine, uint CreateFlags);
    [PreserveSig] int CreateProcessAndAttachWide(ulong Server, [MarshalAs(UnmanagedType.LPWStr)] string? CommandLine, uint CreateFlags, uint ProcessId, uint AttachFlags);

    // --- IDebugClient4 ---
    [PreserveSig] int OpenDumpFileWide([MarshalAs(UnmanagedType.LPWStr)] string FileName, ulong FileHandle);
    [PreserveSig] int WriteDumpFileWide([MarshalAs(UnmanagedType.LPWStr)] string FileName, ulong FileHandle, uint Qualifier, uint FormatFlags, [MarshalAs(UnmanagedType.LPWStr)] string Comment);
    [PreserveSig] int AddDumpInformationFileWide([MarshalAs(UnmanagedType.LPWStr)] string FileName, ulong FileHandle, uint Type);
    [PreserveSig] int GetNumberDumpFiles(out uint Number);
    [PreserveSig] int GetDumpFile(uint Index, IntPtr Buffer, uint BufferSize, out uint NameSize, out ulong Handle, out uint Type);
    [PreserveSig] int GetDumpFileWide(uint Index, IntPtr Buffer, uint BufferSize, out uint NameSize, out ulong Handle, out uint Type);

    // --- IDebugClient5 ---
    [PreserveSig] int AttachKernelWide(uint Flags, [MarshalAs(UnmanagedType.LPWStr)] string? ConnectOptions);
    [PreserveSig] int GetKernelConnectionOptionsWide(IntPtr Buffer, uint BufferSize, out uint OptionsSize);
    [PreserveSig] int SetKernelConnectionOptionsWide([MarshalAs(UnmanagedType.LPWStr)] string Options);
    [PreserveSig] int StartProcessServerWide(uint Flags, [MarshalAs(UnmanagedType.LPWStr)] string Options, IntPtr Reserved);
    [PreserveSig] int ConnectProcessServerWide([MarshalAs(UnmanagedType.LPWStr)] string RemoteOptions, out ulong Server);
    [PreserveSig] int StartServerWide([MarshalAs(UnmanagedType.LPWStr)] string Options);
    [PreserveSig] int OutputServersWide(uint OutputControl, [MarshalAs(UnmanagedType.LPWStr)] string Machine, uint Flags);
    [PreserveSig] int GetOutputCallbacksWide(out IntPtr Callbacks);
    [PreserveSig] int SetOutputCallbacksWide(IntPtr Callbacks);
    [PreserveSig] int GetOutputLinePrefixWide(IntPtr Buffer, uint BufferSize, out uint PrefixSize);
    [PreserveSig] int SetOutputLinePrefixWide([MarshalAs(UnmanagedType.LPWStr)] string Prefix);
    [PreserveSig] int GetIdentityWide(IntPtr Buffer, uint BufferSize, out uint IdentitySize);
    [PreserveSig] int OutputIdentityWide(uint OutputControl, uint Flags, [MarshalAs(UnmanagedType.LPWStr)] string Format);
    [PreserveSig] int GetEventCallbacksWide(out IntPtr Callbacks);
    [PreserveSig] int SetEventCallbacksWide(IntPtr Callbacks);
    [PreserveSig] int CreateProcess2(ulong Server, [MarshalAs(UnmanagedType.LPStr)] string CommandLine, IntPtr OptionsBuffer, uint OptionsBufferSize, [MarshalAs(UnmanagedType.LPStr)] string InitialDirectory, [MarshalAs(UnmanagedType.LPStr)] string Environment);
    [PreserveSig] int CreateProcess2Wide(ulong Server, [MarshalAs(UnmanagedType.LPWStr)] string CommandLine, IntPtr OptionsBuffer, uint OptionsBufferSize, [MarshalAs(UnmanagedType.LPWStr)] string InitialDirectory, [MarshalAs(UnmanagedType.LPWStr)] string Environment);
    [PreserveSig] int CreateProcessAndAttach2(ulong Server, [MarshalAs(UnmanagedType.LPStr)] string CommandLine, IntPtr OptionsBuffer, uint OptionsBufferSize, [MarshalAs(UnmanagedType.LPStr)] string InitialDirectory, [MarshalAs(UnmanagedType.LPStr)] string Environment, uint ProcessId, uint AttachFlags);
    [PreserveSig] int CreateProcessAndAttach2Wide(ulong Server, [MarshalAs(UnmanagedType.LPWStr)] string CommandLine, IntPtr OptionsBuffer, uint OptionsBufferSize, [MarshalAs(UnmanagedType.LPWStr)] string InitialDirectory, [MarshalAs(UnmanagedType.LPWStr)] string Environment, uint ProcessId, uint AttachFlags);
    [PreserveSig] int PushOutputLinePrefix([MarshalAs(UnmanagedType.LPStr)] string NewPrefix, out ulong Handle);
    [PreserveSig] int PushOutputLinePrefixWide([MarshalAs(UnmanagedType.LPWStr)] string NewPrefix, out ulong Handle);
    [PreserveSig] int PopOutputLinePrefix(ulong Handle);
    [PreserveSig] int GetNumberInputCallbacks(out uint Count);
    [PreserveSig] int GetNumberOutputCallbacks(out uint Count);
    [PreserveSig] int GetNumberEventCallbacks(uint EventFlags, out uint Count);
    [PreserveSig] int GetQuitLockString(IntPtr Buffer, uint BufferSize, out uint StringSize);
    [PreserveSig] int SetQuitLockString([MarshalAs(UnmanagedType.LPStr)] string String);
    [PreserveSig] int GetQuitLockStringWide(IntPtr Buffer, uint BufferSize, out uint StringSize);
    [PreserveSig] int SetQuitLockStringWide([MarshalAs(UnmanagedType.LPWStr)] string String);
}

/// <summary>
/// Subset of IDebugControl7 — enough to wait for initial event, execute commands,
/// and inspect basic session state.
/// </summary>
[Guid("b86fb3b1-80d4-475b-aea3-cf06539cf63a")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[ComImport]
internal interface IDebugControl7
{
    // For our use, we only need a handful of methods but COM vtable order matters,
    // so we declare enough of the vtable (from IDebugControl, 2, 3, 4, 5, 6, 7)
    // to reach our targets. For brevity we use IntPtr for methods we don't call
    // but which sit in the vtable above ours.
    [PreserveSig] int GetInterrupt();
    [PreserveSig] int SetInterrupt(uint Flags);
    [PreserveSig] int GetInterruptTimeout(out uint Seconds);
    [PreserveSig] int SetInterruptTimeout(uint Seconds);
    [PreserveSig] int GetLogFile(IntPtr Buffer, uint BufferSize, out uint FileSize, out bool Append);
    [PreserveSig] int OpenLogFile([MarshalAs(UnmanagedType.LPStr)] string File, [MarshalAs(UnmanagedType.Bool)] bool Append);
    [PreserveSig] int CloseLogFile();
    [PreserveSig] int GetLogMask(out uint Mask);
    [PreserveSig] int SetLogMask(uint Mask);
    [PreserveSig] int Input(IntPtr Buffer, uint BufferSize, out uint InputSize);
    [PreserveSig] int ReturnInput([MarshalAs(UnmanagedType.LPStr)] string Buffer);
    [PreserveSig] int Output(uint Mask, [MarshalAs(UnmanagedType.LPStr)] string Format);
    [PreserveSig] int OutputVaList(uint Mask, [MarshalAs(UnmanagedType.LPStr)] string Format, IntPtr Args);
    [PreserveSig] int ControlledOutput(uint OutputControl, uint Mask, [MarshalAs(UnmanagedType.LPStr)] string Format);
    [PreserveSig] int ControlledOutputVaList(uint OutputControl, uint Mask, [MarshalAs(UnmanagedType.LPStr)] string Format, IntPtr Args);
    [PreserveSig] int OutputPrompt(uint OutputControl, [MarshalAs(UnmanagedType.LPStr)] string Format);
    [PreserveSig] int OutputPromptVaList(uint OutputControl, [MarshalAs(UnmanagedType.LPStr)] string Format, IntPtr Args);
    [PreserveSig] int GetPromptText(IntPtr Buffer, uint BufferSize, out uint TextSize);
    [PreserveSig] int OutputCurrentState(uint OutputControl, uint Flags);
    [PreserveSig] int OutputVersionInformation(uint OutputControl);
    [PreserveSig] int GetNotifyEventHandle(out ulong Handle);
    [PreserveSig] int SetNotifyEventHandle(ulong Handle);
    [PreserveSig] int Assemble(ulong Offset, [MarshalAs(UnmanagedType.LPStr)] string Instr, out ulong EndOffset);
    [PreserveSig] int Disassemble(ulong Offset, uint Flags, IntPtr Buffer, uint BufferSize, out uint DisassemblySize, out ulong EndOffset);
    [PreserveSig] int GetDisassembleEffectiveOffset(out ulong Offset);
    [PreserveSig] int OutputDisassembly(uint OutputControl, ulong Offset, uint Flags, out ulong EndOffset);
    [PreserveSig] int OutputDisassemblyLines(uint OutputControl, uint PreviousLines, uint TotalLines, ulong Offset, uint Flags, out uint OffsetLine, out ulong StartOffset, out ulong EndOffset, IntPtr LineOffsets);
    [PreserveSig] int GetNearInstruction(ulong Offset, int Delta, out ulong NearOffset);
    [PreserveSig] int GetStackTrace(ulong FrameOffset, ulong StackOffset, ulong InstructionOffset, IntPtr Frames, uint FramesSize, out uint FramesFilled);
    [PreserveSig] int GetReturnOffset(out ulong Offset);
    [PreserveSig] int OutputStackTrace(uint OutputControl, IntPtr Frames, uint FramesSize, uint Flags);
    [PreserveSig] int GetDebuggeeType(out uint Class, out uint Qualifier);
    [PreserveSig] int GetActualProcessorType(out uint Type);
    [PreserveSig] int GetExecutingProcessorType(out uint Type);
    [PreserveSig] int GetNumberPossibleExecutingProcessorTypes(out uint Number);
    [PreserveSig] int GetPossibleExecutingProcessorTypes(uint Start, uint Count, IntPtr Types);
    [PreserveSig] int GetNumberProcessors(out uint Number);
    [PreserveSig] int GetSystemVersion(out uint PlatformId, out uint Major, out uint Minor, IntPtr ServicePackString, uint ServicePackStringSize, out uint ServicePackStringUsed, out uint ServicePackNumber, IntPtr BuildString, uint BuildStringSize, out uint BuildStringUsed);
    [PreserveSig] int GetPageSize(out uint Size);
    [PreserveSig] int IsPointer64Bit();
    [PreserveSig] int ReadBugCheckData(out uint Code, out ulong Arg1, out ulong Arg2, out ulong Arg3, out ulong Arg4);
    [PreserveSig] int GetNumberSupportedProcessorTypes(out uint Number);
    [PreserveSig] int GetSupportedProcessorTypes(uint Start, uint Count, IntPtr Types);
    [PreserveSig] int GetProcessorTypeNames(uint Type, IntPtr FullNameBuffer, uint FullNameBufferSize, out uint FullNameSize, IntPtr AbbrevNameBuffer, uint AbbrevNameBufferSize, out uint AbbrevNameSize);
    [PreserveSig] int GetEffectiveProcessorType(out uint Type);
    [PreserveSig] int SetEffectiveProcessorType(uint Type);
    [PreserveSig] int GetExecutionStatus(out uint Status);
    [PreserveSig] int SetExecutionStatus(uint Status);
    [PreserveSig] int GetCodeLevel(out uint Level);
    [PreserveSig] int SetCodeLevel(uint Level);
    [PreserveSig] int GetEngineOptions(out uint Options);
    [PreserveSig] int AddEngineOptions(uint Options);
    [PreserveSig] int RemoveEngineOptions(uint Options);
    [PreserveSig] int SetEngineOptions(uint Options);
    [PreserveSig] int GetSystemErrorControl(out uint OutputLevel, out uint BreakLevel);
    [PreserveSig] int SetSystemErrorControl(uint OutputLevel, uint BreakLevel);
    [PreserveSig] int GetTextMacro(uint Slot, IntPtr Buffer, uint BufferSize, out uint MacroSize);
    [PreserveSig] int SetTextMacro(uint Slot, [MarshalAs(UnmanagedType.LPStr)] string Macro);
    [PreserveSig] int GetRadix(out uint Radix);
    [PreserveSig] int SetRadix(uint Radix);
    [PreserveSig] int Evaluate([MarshalAs(UnmanagedType.LPStr)] string Expression, uint DesiredType, IntPtr Value, out uint RemainderIndex);
    [PreserveSig] int CoerceValue(IntPtr In, uint OutType, IntPtr Out);
    [PreserveSig] int CoerceValues(uint Count, IntPtr In, IntPtr OutTypes, IntPtr Out);
    [PreserveSig] int Execute(DebugOutctl OutputControl, [MarshalAs(UnmanagedType.LPStr)] string Command, DebugExecute Flags);
    [PreserveSig] int ExecuteCommandFile(DebugOutctl OutputControl, [MarshalAs(UnmanagedType.LPStr)] string CommandFile, DebugExecute Flags);
    [PreserveSig] int GetNumberBreakpoints(out uint Number);
    [PreserveSig] int GetBreakpointByIndex(uint Index, out IntPtr Bp);
    [PreserveSig] int GetBreakpointById(uint Id, out IntPtr Bp);
    [PreserveSig] int GetBreakpointParameters(uint Count, IntPtr Ids, uint Start, IntPtr Params);
    [PreserveSig] int AddBreakpoint(uint Type, uint DesiredId, out IntPtr Bp);
    [PreserveSig] int RemoveBreakpoint(IntPtr Bp);
    [PreserveSig] int AddExtension([MarshalAs(UnmanagedType.LPStr)] string Path, uint Flags, out ulong Handle);
    [PreserveSig] int RemoveExtension(ulong Handle);
    [PreserveSig] int GetExtensionByPath([MarshalAs(UnmanagedType.LPStr)] string Path, out ulong Handle);
    [PreserveSig] int CallExtension(ulong Handle, [MarshalAs(UnmanagedType.LPStr)] string Function, [MarshalAs(UnmanagedType.LPStr)] string Arguments);
    [PreserveSig] int GetExtensionFunction(ulong Handle, [MarshalAs(UnmanagedType.LPStr)] string FuncName, out IntPtr Function);
    [PreserveSig] int GetWindbgExtensionApis32(IntPtr Api);
    [PreserveSig] int GetWindbgExtensionApis64(IntPtr Api);
    [PreserveSig] int GetNumberEventFilters(out uint SpecificEvents, out uint SpecificExceptions, out uint ArbitraryExceptions);
    [PreserveSig] int GetEventFilterText(uint Index, IntPtr Buffer, uint BufferSize, out uint TextSize);
    [PreserveSig] int GetEventFilterCommand(uint Index, IntPtr Buffer, uint BufferSize, out uint CommandSize);
    [PreserveSig] int SetEventFilterCommand(uint Index, [MarshalAs(UnmanagedType.LPStr)] string Command);
    [PreserveSig] int GetSpecificFilterParameters(uint Start, uint Count, IntPtr Params);
    [PreserveSig] int SetSpecificFilterParameters(uint Start, uint Count, IntPtr Params);
    [PreserveSig] int GetSpecificFilterArgument(uint Index, IntPtr Buffer, uint BufferSize, out uint ArgumentSize);
    [PreserveSig] int SetSpecificFilterArgument(uint Index, [MarshalAs(UnmanagedType.LPStr)] string Argument);
    [PreserveSig] int GetExceptionFilterParameters(uint Count, IntPtr Codes, uint Start, IntPtr Params);
    [PreserveSig] int SetExceptionFilterParameters(uint Count, IntPtr Params);
    [PreserveSig] int GetExceptionFilterSecondCommand(uint Index, IntPtr Buffer, uint BufferSize, out uint CommandSize);
    [PreserveSig] int SetExceptionFilterSecondCommand(uint Index, [MarshalAs(UnmanagedType.LPStr)] string Command);
    [PreserveSig] int WaitForEvent(DebugWait Flags, uint Timeout);
    [PreserveSig] int GetLastEventInformation(out uint Type, out uint ProcessId, out uint ThreadId, IntPtr ExtraInformation, uint ExtraInformationSize, out uint ExtraInformationUsed, IntPtr Description, uint DescriptionSize, out uint DescriptionUsed);
    // rest of IDebugControl7 omitted — not needed for v0.3 bootstrap
}

/// <summary>
/// Output callback. Every line / chunk written by commands (including
/// <c>Execute</c>) flows through <see cref="Output"/>.
/// </summary>
[Guid("4bf58045-d654-4c40-b0af-683090f356dc")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[ComImport]
internal interface IDebugOutputCallbacks
{
    [PreserveSig] int Output(DebugOutput Mask, [MarshalAs(UnmanagedType.LPStr)] string Text);
}
