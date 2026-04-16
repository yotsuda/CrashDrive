#Requires -Version 7.4
#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.3.0' }

# Smoke tests. The point is to catch gross regressions (module won't load,
# provider mount fails, a previously-working cmdlet vanished) with zero setup.
# Anything that needs an elevated shell (TTD recording) or a huge artifact is
# skipped when the artifact isn't present, so these run unchanged in CI.

BeforeDiscovery {
    # -Skip expressions are evaluated at Discovery time, so paths must be
    # set here rather than in a Run-phase BeforeAll.
    $script:RepoRoot  = Split-Path -Parent $PSScriptRoot
    $script:Scratch   = Join-Path $script:RepoRoot 'scratch'
    $script:DumpPath  = Join-Path $script:Scratch 'self.dmp'
    $script:TracePath = Join-Path $script:Scratch 'cart.jsonl'
    $script:TtdPath   = Join-Path $script:Scratch 'ttd\python01.run'
    $script:PyTarget  = Join-Path $script:Scratch 'sample_target.py'
    $script:HasDump   = Test-Path $script:DumpPath
    $script:HasTrace  = Test-Path $script:TracePath
    $script:HasTtd    = Test-Path $script:TtdPath
    $script:HasPy     = Test-Path $script:PyTarget
}

BeforeAll {
    Import-Module CrashDrive -Force -ErrorAction Stop
    function Remove-SmokeDrives {
        foreach ($n in 'smoke_dump','smoke_trace','smoke_ttd','smoke_cap') {
            if (Get-PSDrive -Name $n -ErrorAction SilentlyContinue) {
                Set-Location C:\
                Remove-PSDrive -Name $n -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

Describe 'Module manifest' {
    It 'imports and reports a version' {
        (Get-Module CrashDrive).Version | Should -Not -BeNullOrEmpty
    }

    It 'exports the expected cmdlets' {
        $expected = @(
            'New-CrashDrive'
            'Invoke-CrashCommand'
            'Read-CrashMemory'
            'Get-CrashObject'
            'Get-CrashLocalVariable'
            'Enable-CrashEditorFollow'
            'Disable-CrashEditorFollow'
        )
        $actual = (Get-Command -Module CrashDrive).Name
        foreach ($cmd in $expected) {
            $actual | Should -Contain $cmd -Because "$cmd must stay exported"
        }
    }
}

Describe 'Dump provider' -Tag 'Dump' -Skip:(-not $HasDump) {
    AfterEach { Remove-SmokeDrives }

    It 'mounts a .dmp and exposes the expected root children' {
        New-CrashDrive smoke_dump $DumpPath
        $names = (Get-ChildItem smoke_dump:\).Name
        $names | Should -Contain 'summary.json'
        $names | Should -Contain 'threads'
        $names | Should -Contain 'modules'
    }

    It 'reads summary.json as JSON' {
        New-CrashDrive smoke_dump $DumpPath
        $json = (Get-Content smoke_dump:\summary.json) -join "`n" | ConvertFrom-Json
        $json.FilePath | Should -Match 'self\.dmp$'
    }

    It 'Invoke-CrashCommand runs a dbgeng command (lm)' {
        New-CrashDrive smoke_dump $DumpPath
        $out = Invoke-CrashCommand -Command 'lm' -Drive smoke_dump
        $out | Should -Match 'start\s+end\s+module name'
    }
}

Describe 'Trace provider' -Tag 'Trace' -Skip:(-not $HasTrace) {
    AfterEach { Remove-SmokeDrives }

    It 'mounts a JSONL trace and exposes the expected root children' {
        New-CrashDrive smoke_trace $TracePath
        $names = (Get-ChildItem smoke_trace:\).Name
        $names | Should -Contain 'summary.json'
        $names | Should -Contain 'events'
    }
}

Describe 'TTD provider' -Tag 'Ttd' -Skip:(-not $HasTtd) {
    AfterEach { Remove-SmokeDrives }

    It 'mounts a .run file and exposes the expected root children' {
        New-CrashDrive smoke_ttd $TtdPath
        $names = (Get-ChildItem smoke_ttd:\).Name
        $names | Should -Contain 'triage.md'
        $names | Should -Contain 'summary.json'
        $names | Should -Contain 'timeline'
    }
}

Describe 'Capture round-trip (Python)' -Tag 'Capture' -Skip:(-not $HasPy) {
    AfterEach { Remove-SmokeDrives }

    It 'runs a Python target under the tracer and produces JSONL' {
        $python = (Get-Command python -ErrorAction SilentlyContinue) ??
                  (Get-Command python3 -ErrorAction SilentlyContinue)
        if (-not $python) {
            Set-ItResult -Skipped -Because "python not on PATH"
            return
        }
        New-CrashDrive smoke_cap -ExecutablePath $PyTarget
        $names = (Get-ChildItem smoke_cap:\).Name
        $names | Should -Contain 'events'
    }
}
