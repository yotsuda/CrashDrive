using System.Diagnostics;
using System.Management.Automation;

namespace CrashDrive.Cmdlets;

/// <summary>
/// Enables "editor follow" mode: when the current location changes to a
/// CrashDrive path that carries a source file + line (e.g. a trace event,
/// a frame with symbols), automatically open that location in the editor.
/// </summary>
[Cmdlet(VerbsLifecycle.Enable, "CrashEditorFollow")]
public sealed class EnableCrashEditorFollowCmdlet : PSCmdlet
{
    /// <summary>Editor command template. $file and $line are substituted.
    /// Default: "code --goto {file}:{line}" (VS Code).</summary>
    [Parameter]
    public string EditorCommand { get; set; } = "code";

    /// <summary>Arguments template — use {file} and {line} placeholders.
    /// Default: "--goto {file}:{line}"</summary>
    [Parameter]
    public string ArgumentsTemplate { get; set; } = "--goto \"{file}:{line}\"";

    protected override void EndProcessing()
    {
        // Runspace-level setup. LocationChangedAction expects a ScriptBlock
        // via PS assignment semantics, not the C# EventHandler type it
        // technically exposes. Easiest: eval a PS snippet that does it.
        using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
        ps.AddScript(@"
param($editorCmd, $editorArgs)
$global:CrashDrive_EditorCommand = $editorCmd
$global:CrashDrive_EditorArgs    = $editorArgs
$executionContext.SessionState.InvokeCommand.LocationChangedAction = {
    param($source, $eventArgs)
    try {
        $newLoc = $eventArgs.NewPath
        if (-not $newLoc) { return }
        $qual = (Split-Path $newLoc -Qualifier).TrimEnd(':')
        if (-not $qual) { return }
        $drive = Get-PSDrive -Name $qual -ErrorAction SilentlyContinue
        if (-not $drive -or $drive.Provider.Name -notin 'Trace','Dump','Ttd') { return }
        $item = Get-Item $newLoc -ErrorAction SilentlyContinue
        if (-not $item) { return }
        $file = $item.SourceFile
        $line = $item.Line
        if (-not $file -or -not (Test-Path $file)) { return }
        $cmd  = $global:CrashDrive_EditorCommand
        $tmpl = $global:CrashDrive_EditorArgs
        if (-not $cmd) { return }
        $argsStr = $tmpl.Replace('{file}', $file).Replace('{line}', [string]$line)
        Start-Process -FilePath $cmd -ArgumentList $argsStr -NoNewWindow -ErrorAction SilentlyContinue | Out-Null
    } catch { }
}
").AddArgument(EditorCommand).AddArgument(ArgumentsTemplate);
        ps.Invoke();
        WriteVerbose($"Editor follow enabled: {EditorCommand} {ArgumentsTemplate}");
    }
}

[Cmdlet(VerbsLifecycle.Disable, "CrashEditorFollow")]
public sealed class DisableCrashEditorFollowCmdlet : PSCmdlet
{
    protected override void EndProcessing()
    {
        using var ps = PowerShell.Create(RunspaceMode.CurrentRunspace);
        ps.AddScript("$executionContext.SessionState.InvokeCommand.LocationChangedAction = $null");
        ps.Invoke();
        WriteVerbose("Editor follow disabled.");
    }
}
