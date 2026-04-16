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
            'New-TtdBookmark'
            'Get-TtdBookmark'
            'Remove-TtdBookmark'
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

    It 'paginates calls\<mod>\<fn>\ into <start>-<end> folders when count exceeds 256' {
        # python01.run: python313!PyObject_IsTrue has 1042 hits => 5 page folders
        New-CrashDrive smoke_ttd $TtdPath
        $pages = Get-ChildItem smoke_ttd:\calls\python313\PyObject_IsTrue
        $pages.Name | Should -Contain '0-255'
        $pages.Name | Should -Contain '256-511'
        $pages.Name | Should -Contain '1024-1041'
        ($pages | Where-Object Name -like '*-*').Count | Should -Be $pages.Count `
            -Because 'a paginated folder should show only range containers, no .json siblings'

        # Items inside a page carry ABSOLUTE indices
        $mid = Get-ChildItem smoke_ttd:\calls\python313\PyObject_IsTrue\512-767 |
               Select-Object -First 1
        $mid.Index | Should -Be 512
        $mid.Name  | Should -Be '512.json'

        # Deep read pulls the right call
        $json = (Get-Content smoke_ttd:\calls\python313\PyObject_IsTrue\1024-1041\1024.json) -join "`n" |
                ConvertFrom-Json
        $json.Index    | Should -Be 1024
        $json.Function | Should -Be 'python313!PyObject_IsTrue'
    }

    It 'leaves calls\<mod>\<fn>\ flat when count is 256 or fewer' {
        # python01.run: python313!Py_Main has 1 hit => flat (no page folders)
        New-CrashDrive smoke_ttd $TtdPath
        $items = Get-ChildItem smoke_ttd:\calls\python313\Py_Main
        $items.Name | Should -Contain '0.json'
        ($items | Where-Object Name -match '^\d+-\d+$').Count | Should -Be 0
    }

    It 'bookmarks round-trip and resolve to the position tree' {
        New-CrashDrive smoke_ttd $TtdPath

        # Empty by default
        (Get-TtdBookmark -Drive smoke_ttd | Measure-Object).Count | Should -Be 0

        # Create two bookmarks and list them
        New-TtdBookmark -Drive smoke_ttd -Name crash-point -Position 1CBF:8C0 | Out-Null
        New-TtdBookmark -Drive smoke_ttd -Name life-start  -Position start | Out-Null
        $bm = Get-TtdBookmark -Drive smoke_ttd | Sort-Object Name
        $bm.Count     | Should -Be 2
        $bm[0].Name   | Should -Be 'crash-point'
        $bm[0].Position | Should -Be '1CBF:8C0'

        # ttd:\bookmarks\ exposes both
        $children = Get-ChildItem smoke_ttd:\bookmarks
        $children.Name | Should -Contain 'crash-point'
        $children.Name | Should -Contain 'life-start'

        # Drill-in mirrors the position tree
        (Get-ChildItem smoke_ttd:\bookmarks\crash-point).Name |
            Should -Contain 'position.json'
        $json = (Get-Content smoke_ttd:\bookmarks\crash-point\position.json) -join "`n" |
                ConvertFrom-Json
        $json.Native | Should -Be '1CBF:8C0'

        # Remove one, the other survives
        Remove-TtdBookmark -Drive smoke_ttd -Name life-start
        (Get-TtdBookmark -Drive smoke_ttd).Name | Should -Be 'crash-point'
    }

    It 'exposes position aliases when backing data exists' {
        # python01.run has no exceptions; has many significant events.
        # So first-/last-exception should NOT be listed, last-meaningful-event should.
        New-CrashDrive smoke_ttd $TtdPath
        $names = (Get-ChildItem smoke_ttd:\positions).Name
        $names | Should -Contain 'start'
        $names | Should -Contain 'end'
        $names | Should -Contain 'last-meaningful-event'
        $names | Should -Not -Contain 'first-exception'
        $names | Should -Not -Contain 'last-exception'

        # Missing alias path must report as absent (not silently resolve to start)
        Test-Path smoke_ttd:\positions\first-exception | Should -BeFalse

        # Present alias drills into the normal position tree
        (Get-ChildItem smoke_ttd:\positions\last-meaningful-event).Name |
            Should -Contain 'position.json'
    }

    It 'rejects bookmark names containing path separators' {
        New-CrashDrive smoke_ttd $TtdPath
        { New-TtdBookmark -Drive smoke_ttd -Name 'bad/name' -Position start -ErrorAction Stop } |
            Should -Throw
        { New-TtdBookmark -Drive smoke_ttd -Name ''         -Position start -ErrorAction Stop } |
            Should -Throw
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
