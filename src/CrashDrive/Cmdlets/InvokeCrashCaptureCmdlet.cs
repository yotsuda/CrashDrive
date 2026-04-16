using System.Diagnostics;
using System.Management.Automation;
using System.Reflection;

namespace CrashDrive.Cmdlets;

/// <summary>
/// Capture a fresh execution trace by running a target program under a
/// language-native tracer. Output JSONL file is returned; use -Mount to
/// also mount it as a CrashDrive in one step.
/// </summary>
[Cmdlet(VerbsLifecycle.Invoke, "CrashCapture")]
[OutputType(typeof(string))]
public sealed class InvokeCrashCaptureCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0)]
    [ValidateNotNullOrEmpty]
    public string Program { get; set; } = "";

    [Parameter(Position = 1, ValueFromRemainingArguments = true)]
    public string[]? ArgumentList { get; set; }

    [Parameter]
    [ValidateSet("python")]
    public string Language { get; set; } = "python";

    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? OutputFile { get; set; }

    [Parameter]
    [ValidateNotNullOrEmpty]
    public string? Name { get; set; }   // If set, also mount as PSDrive

    [Parameter]
    [ValidateSet("call", "return", "exception")]
    public string[] Capture { get; set; } = ["call", "return", "exception"];

    [Parameter]
    [ValidateRange(1, 3600)]
    public int TimeoutSeconds { get; set; } = 60;

    protected override void ProcessRecord()
    {
        var absProgram = GetUnresolvedProviderPathFromPSPath(Program);
        if (!System.IO.File.Exists(absProgram))
        {
            WriteError(new ErrorRecord(
                new System.IO.FileNotFoundException($"Program not found: {absProgram}"),
                "ProgramNotFound", ErrorCategory.ObjectNotFound, absProgram));
            return;
        }

        var outputPath = OutputFile != null
            ? GetUnresolvedProviderPathFromPSPath(OutputFile)
            : System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"crashdrive_trace_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.jsonl");

        try
        {
            RunTracer(Language, absProgram, ArgumentList ?? [], outputPath, Capture, TimeoutSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            WriteError(new ErrorRecord(ex, "TracerFailed", ErrorCategory.NotSpecified, absProgram));
            return;
        }

        // If -Name given, also mount
        if (Name != null)
        {
            var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
            try
            {
                ps.AddCommand("New-CrashDrive")
                    .AddParameter("Name", Name)
                    .AddParameter("File", outputPath);
                ps.Invoke();
                if (ps.HadErrors)
                    foreach (var err in ps.Streams.Error)
                        WriteError(err);
            }
            finally
            {
                ps.Dispose();
            }
        }

        WriteObject(outputPath);
    }

    private void RunTracer(
        string language, string program, string[] programArgs,
        string outputPath, string[] capture, int timeoutSeconds)
    {
        if (language != "python")
            throw new NotSupportedException($"Language '{language}' is not supported yet.");

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
        psi.ArgumentList.Add(string.Join(",", capture));
        psi.ArgumentList.Add(program);
        foreach (var a in programArgs) psi.ArgumentList.Add(a);

        WriteVerbose($"Running: {pythonExe} {string.Join(" ", psi.ArgumentList)}");

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start python tracer.");
        proc.StandardInput.Close();

        if (!proc.WaitForExit(timeoutSeconds * 1000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException(
                $"Tracer exceeded timeout of {timeoutSeconds}s. Partial trace at {outputPath}.");
        }

        var stderr = proc.StandardError.ReadToEnd();
        if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            WriteWarning($"Tracer exited {proc.ExitCode}. stderr: {stderr.Trim()}");
    }

    private static string ExtractTracerScript(string scriptName)
    {
        var cacheDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "crashdrive_tracers");
        Directory.CreateDirectory(cacheDir);
        var destPath = System.IO.Path.Combine(cacheDir, scriptName);

        var asm = typeof(InvokeCrashCaptureCmdlet).Assembly;
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
        if (!string.IsNullOrEmpty(envOverride) && System.IO.File.Exists(envOverride))
            return envOverride;

        if (!OperatingSystem.IsWindows())
            return "python3";

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(System.IO.Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            if (dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            var candidate = System.IO.Path.Combine(dir.Trim(), "python.exe");
            if (System.IO.File.Exists(candidate)) return candidate;
        }
        return "python";
    }
}
