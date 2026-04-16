using System.Diagnostics;
using System.Management.Automation;
using System.Reflection;
using CrashDrive.Store;

namespace CrashDrive.Cmdlets;

/// <summary>
/// Create a CrashDrive from a post-mortem artifact.
///
/// Two modes, selected by parameter set:
///   FromFile (default, positional <c>-Path</c>): mount an existing
///     trace / dump / TTD file.
///   Capture (<c>-ExecutablePath</c>): launch a program under a
///     language-native tracer, capture a JSONL trace, then mount it.
///
/// The Capture set's <c>-ExecutablePath</c> is deliberately named so
/// that "read an artifact" vs "execute a program" is unambiguous at
/// the call site.
/// </summary>
[Cmdlet(VerbsCommon.New, "CrashDrive", DefaultParameterSetName = FromFileSet)]
public sealed class NewCrashDriveCmdlet : PSCmdlet
{
    private const string FromFileSet = "FromFile";
    private const string CaptureSet = "Capture";

    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrEmpty]
    public string Name { get; set; } = "";

    // ── FromFile ──────────────────────────────────────────────

    [Parameter(Mandatory = true, Position = 1, ParameterSetName = FromFileSet)]
    [Alias("File")]
    [ValidateNotNullOrEmpty]
    public string Path { get; set; } = "";

    [Parameter(ParameterSetName = FromFileSet)]
    [ValidateNotNullOrEmpty]
    public string? SymbolPath { get; set; }

    // ── Capture ───────────────────────────────────────────────

    [Parameter(Mandatory = true, ParameterSetName = CaptureSet)]
    [ValidateNotNullOrEmpty]
    public string ExecutablePath { get; set; } = "";

    [Parameter(ParameterSetName = CaptureSet)]
    [ValidateSet("python", "dotnet")]
    public string? Language { get; set; }

    [Parameter(ParameterSetName = CaptureSet, ValueFromRemainingArguments = true)]
    public string[]? ExecutableArgs { get; set; }

    [Parameter(ParameterSetName = CaptureSet)]
    [ValidateRange(1, 3600)]
    public int TimeoutSeconds { get; set; } = 60;

    [Parameter(ParameterSetName = CaptureSet)]
    [ValidateNotNullOrEmpty]
    public string? OutputFile { get; set; }

    // Python-specific Capture options (no-op for -Language dotnet).
    [Parameter(ParameterSetName = CaptureSet)]
    [ValidateSet("call", "return", "exception")]
    public string[] EventTypes { get; set; } = ["call", "return", "exception"];

    [Parameter(ParameterSetName = CaptureSet)]
    public SwitchParameter IncludeGlobals { get; set; }

    [Parameter(ParameterSetName = CaptureSet)]
    public string[]? Watch { get; set; }

    // Dotnet-specific Capture options (no-op for -Language python).
    [Parameter(ParameterSetName = CaptureSet)]
    public string[]? Include { get; set; }

    [Parameter(ParameterSetName = CaptureSet)]
    public string[]? Exclude { get; set; }

    // ── common ────────────────────────────────────────────────

    [Parameter]
    public SwitchParameter PassThru { get; set; }

    protected override void ProcessRecord()
    {
        string? resolved;
        if (ParameterSetName == CaptureSet)
        {
            resolved = RunCapture();
            if (resolved == null) return;
        }
        else
        {
            resolved = ResolveExistingFile(Path);
            if (resolved == null) return;
        }

        MountDrive(resolved);
    }

    // ── FromFile path ─────────────────────────────────────────

    private string? ResolveExistingFile(string path)
    {
        var resolved = GetUnresolvedProviderPathFromPSPath(path);
        if (!File.Exists(resolved))
        {
            WriteError(new ErrorRecord(
                new FileNotFoundException($"File not found: {resolved}"),
                "FileNotFound", ErrorCategory.ObjectNotFound, resolved));
            return null;
        }
        return resolved;
    }

    private void MountDrive(string resolved)
    {
        var kind = StoreFactory.DetectKind(resolved);
        var providerName = kind switch
        {
            StoreKind.Trace => "Trace",
            StoreKind.Dump => "Dump",
            StoreKind.Ttd => "Ttd",
            _ => throw new NotSupportedException($"Unknown file kind: {kind}"),
        };

        using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
        var cmd = ps.AddCommand("New-PSDrive")
            .AddParameter("PSProvider", providerName)
            .AddParameter("Name", Name)
            .AddParameter("Root", @"\")
            .AddParameter("File", resolved)
            .AddParameter("Scope", "Global");

        if (ParameterSetName == FromFileSet && SymbolPath != null)
            cmd.AddParameter("SymbolPath", SymbolPath);

        var results = ps.Invoke();
        if (ps.HadErrors)
        {
            foreach (var err in ps.Streams.Error) WriteError(err);
            return;
        }
        if (PassThru.IsPresent)
            foreach (var r in results) WriteObject(r);
    }

    // ── Capture path ──────────────────────────────────────────

    private string? RunCapture()
    {
        var absExe = GetUnresolvedProviderPathFromPSPath(ExecutablePath);
        if (!File.Exists(absExe))
        {
            WriteError(new ErrorRecord(
                new FileNotFoundException($"Executable not found: {absExe}"),
                "ExecutableNotFound", ErrorCategory.ObjectNotFound, absExe));
            return null;
        }

        var language = Language ?? InferLanguageFromExtension(absExe);
        if (language == null)
        {
            WriteError(new ErrorRecord(
                new ArgumentException(
                    $"Cannot infer -Language from '{System.IO.Path.GetExtension(absExe)}'. " +
                    "Specify -Language explicitly (python | dotnet)."),
                "LanguageInferenceFailed", ErrorCategory.InvalidArgument, absExe));
            return null;
        }

        var outputPath = OutputFile != null
            ? GetUnresolvedProviderPathFromPSPath(OutputFile)
            : System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"crashdrive_trace_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.jsonl");

        try
        {
            switch (language)
            {
                case "python":
                    RunPythonTracer(absExe, ExecutableArgs ?? [], outputPath);
                    break;
                case "dotnet":
                    RunDotnetTracer(absExe, ExecutableArgs ?? [], outputPath);
                    break;
                default:
                    throw new NotSupportedException($"Language '{language}' is not supported.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WriteError(new ErrorRecord(ex, "TracerFailed", ErrorCategory.NotSpecified, absExe));
            return null;
        }

        return outputPath;
    }

    private static string? InferLanguageFromExtension(string path)
    {
        var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".py" or ".pyw" => "python",
            ".exe" or ".dll" => "dotnet",
            _ => null,
        };
    }

    private void RunPythonTracer(string program, string[] programArgs, string outputPath)
    {
        var tracerScript = ExtractTracerScript("python_tracer.py");
        var pythonExe = ResolvePythonExecutable();
        var filterPrefix = System.IO.Path.GetDirectoryName(program)
            ?? throw new InvalidOperationException($"Cannot determine directory for: {program}");

        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            WorkingDirectory = filterPrefix,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(tracerScript);
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(outputPath);
        psi.ArgumentList.Add("--filter-prefix");
        psi.ArgumentList.Add(filterPrefix);
        psi.ArgumentList.Add("--events");
        psi.ArgumentList.Add(string.Join(",", EventTypes));
        if (IncludeGlobals.IsPresent)
            psi.ArgumentList.Add("--include-globals");
        if (Watch != null && Watch.Length > 0)
        {
            psi.ArgumentList.Add("--watch");
            psi.ArgumentList.Add(string.Join(",", Watch));
        }
        psi.ArgumentList.Add(program);
        foreach (var a in programArgs) psi.ArgumentList.Add(a);

        WriteVerbose($"Running: {pythonExe} {string.Join(" ", psi.ArgumentList)}");

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start python tracer.");
        proc.StandardInput.Close();

        if (!proc.WaitForExit(TimeoutSeconds * 1000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(
                $"Tracer exceeded timeout of {TimeoutSeconds}s. Partial trace at {outputPath}.");
        }

        var stderr = proc.StandardError.ReadToEnd();
        if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            WriteWarning($"Tracer exited {proc.ExitCode}. stderr: {stderr.Trim()}");
    }

    private void RunDotnetTracer(string executable, string[] programArgs, string outputPath)
    {
        // Locate the tracer DLL next to this assembly (deploy.ps1 puts both
        // CrashDrive.dll and CrashDrive.Tracer.Startup.dll in the same folder).
        var moduleDir = System.IO.Path.GetDirectoryName(
            typeof(NewCrashDriveCmdlet).Assembly.Location)!;
        var tracerDll = System.IO.Path.Combine(moduleDir, "CrashDrive.Tracer.Startup.dll");
        if (!File.Exists(tracerDll))
            throw new FileNotFoundException(
                $"Tracer DLL not found at {tracerDll}. Re-run deploy.ps1.");

        // .dll targets must launch via `dotnet <dll>`; .exe goes direct.
        var ext = System.IO.Path.GetExtension(executable).ToLowerInvariant();
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = System.IO.Path.GetDirectoryName(executable)
                ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (ext == ".dll")
        {
            psi.FileName = "dotnet";
            psi.ArgumentList.Add(executable);
        }
        else
        {
            psi.FileName = executable;
        }
        foreach (var a in programArgs) psi.ArgumentList.Add(a);

        // Runtime picks up these env vars on process start:
        //   DOTNET_STARTUP_HOOKS — tells runtime to load our tracer DLL and
        //     invoke StartupHook.Initialize() before user Main.
        //   CRASHDRIVE_TRACE_OUT — where StartupHook writes the JSONL.
        //   CRASHDRIVE_TRACE_INCLUDE / _EXCLUDE — assembly-name glob filter.
        psi.Environment["DOTNET_STARTUP_HOOKS"] = tracerDll;
        psi.Environment["CRASHDRIVE_TRACE_OUT"] = outputPath;
        if (Include != null && Include.Length > 0)
            psi.Environment["CRASHDRIVE_TRACE_INCLUDE"] = string.Join(";", Include);
        if (Exclude != null && Exclude.Length > 0)
            psi.Environment["CRASHDRIVE_TRACE_EXCLUDE"] = string.Join(";", Exclude);

        WriteVerbose($"Running: {psi.FileName} {string.Join(" ", psi.ArgumentList)}");
        WriteVerbose($"DOTNET_STARTUP_HOOKS={tracerDll}");
        WriteVerbose($"CRASHDRIVE_TRACE_OUT={outputPath}");

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start target.");

        if (!proc.WaitForExit(TimeoutSeconds * 1000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(
                $"Target exceeded timeout of {TimeoutSeconds}s. Partial trace at {outputPath}.");
        }

        if (proc.ExitCode != 0)
            WriteWarning($"Target exited with code {proc.ExitCode}.");
    }

    private static string ExtractTracerScript(string scriptName)
    {
        var cacheDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "crashdrive_tracers");
        Directory.CreateDirectory(cacheDir);
        var destPath = System.IO.Path.Combine(cacheDir, scriptName);

        var asm = typeof(NewCrashDriveCmdlet).Assembly;
        var suffix = $".Resources.Tracers.{scriptName}";
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Embedded resource not found: *{suffix}");

        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Failed to open resource stream: {resourceName}");
        using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.CopyTo(fs);
        return destPath;
    }

    private static string ResolvePythonExecutable()
    {
        var envOverride = Environment.GetEnvironmentVariable("CRASHDRIVE_PYTHON");
        if (!string.IsNullOrEmpty(envOverride) && File.Exists(envOverride))
            return envOverride;

        if (!OperatingSystem.IsWindows())
            return "python3";

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(System.IO.Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            if (dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            var candidate = System.IO.Path.Combine(dir.Trim(), "python.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return "python";
    }
}
