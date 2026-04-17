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
    $script:FullDump  = Join-Path $script:Scratch 'self-full.dmp'
    $script:TracePath = Join-Path $script:Scratch 'cart.jsonl'
    $script:TtdPath   = Join-Path $script:Scratch 'ttd\python01.run'
    $script:PyTarget  = Join-Path $script:Scratch 'sample_target.py'
    $script:TinyApp   = Join-Path $script:Scratch 'tinyapp\bin\Debug\net8.0\tinyapp.exe'
    $script:HasDump   = Test-Path $script:DumpPath
    $script:HasTrace  = Test-Path $script:TracePath
    $script:HasTtd    = Test-Path $script:TtdPath
    $script:HasPy     = Test-Path $script:PyTarget
    $script:HasTiny   = Test-Path $script:TinyApp
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
        $names | Should -Contain 'triage.md'
        $names | Should -Contain 'summary.json'
        $names | Should -Contain 'threads'
        $names | Should -Contain 'modules'
    }

    It 'renders triage.md without running expensive operations' {
        New-CrashDrive smoke_dump $DumpPath
        $triage = (Get-Content smoke_dump:\triage.md) -join "`n"
        $triage | Should -Match '# Dump Triage:'
        $triage | Should -Match '## Threads with active managed exceptions'
        $triage | Should -Match '## Thread summary'
        $triage | Should -Match '## Where to look next'
    }

    It 'exposes modules\by-kind\ filtering native vs managed' {
        New-CrashDrive smoke_dump $DumpPath
        $top = (Get-ChildItem smoke_dump:\modules).Name
        $top | Should -Contain 'by-kind'

        $kinds = (Get-ChildItem smoke_dump:\modules\by-kind).Name
        # At least one of native / managed must be present (the self dump
        # has both CrashDrive.dll — managed — and ntdll etc. — native).
        ($kinds -contains 'native' -or $kinds -contains 'managed') | Should -BeTrue

        if ($kinds -contains 'native') {
            $nativeMods = Get-ChildItem smoke_dump:\modules\by-kind\native
            $nativeMods.Count | Should -BeGreaterThan 0
            $nativeMods | ForEach-Object { $_.IsManaged | Should -BeFalse }
        }
    }

    It 'exposes heap\by-generation\ for CLR dumps' -Skip:(-not (Test-Path $FullDump)) {
        # Generation info requires CLR data; self.dmp (Normal) may have
        # partial heap, so use self-full.dmp when available.
        New-CrashDrive smoke_dump $FullDump
        $heap = (Get-ChildItem smoke_dump:\heap).Name
        $heap | Should -Contain 'by-generation'

        $gens = (Get-ChildItem smoke_dump:\heap\by-generation).Name
        # gen2 is practically always populated on a live process snapshot
        $gens | Should -Contain 'gen2'

        $gen2Types = Get-ChildItem smoke_dump:\heap\by-generation\gen2
        $gen2Types.Count | Should -BeGreaterThan 0
        $first = $gen2Types | Sort-Object TotalBytes -Descending | Select-Object -First 1
        $first.InstanceCount | Should -BeGreaterThan 0
        $first.TotalBytes    | Should -BeGreaterThan 0
    }

    It 'auto-classifies threads by state and exception' {
        # self.dmp is a snapshot of pwsh with CrashDrive loaded; no managed
        # exceptions expected, but 1 finalizer thread and several dead threads
        # should be present (GC background + terminated workers).
        New-CrashDrive smoke_dump $DumpPath

        $threadsRoot = (Get-ChildItem smoke_dump:\threads).Name
        # by-state should appear as a classifier folder
        $threadsRoot | Should -Contain 'by-state'

        # finalizer / dead are expected; only nonempty categories listed
        $categories = (Get-ChildItem smoke_dump:\threads\by-state).Name
        $categories | Should -Contain 'finalizer'

        # drill-in reuses the normal threads\<id>\ tree
        $finalizers = Get-ChildItem smoke_dump:\threads\by-state\finalizer
        $finalizers.Count | Should -BeGreaterThan 0
        $finalizers[0].IsFinalizer | Should -BeTrue

        $contents = (Get-ChildItem ("smoke_dump:\threads\by-state\finalizer\" +
                                    $finalizers[0].ManagedThreadId)).Name
        $contents | Should -Contain 'info.json'
        $contents | Should -Contain 'frames'
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

    It 'range\<s>_to_<e>\ filters events and memory to a time window' {
        New-CrashDrive smoke_ttd $TtdPath

        # Full lifetime range must always be available
        $range = (Get-ChildItem smoke_ttd:\range).Name
        $range | Should -Contain 'start_to_end'

        # Drill into the full-lifetime events — python01.run has exactly 4
        # significant events (ThreadCreated + 2× ModuleLoaded + ThreadTerminated)
        $evs = Get-ChildItem smoke_ttd:\range\start_to_end\events
        $evs.Count | Should -BeGreaterThan 0

        # A narrower window must strictly subset the full one
        $narrow = Get-ChildItem 'smoke_ttd:\range\14_0_to_1C7F_0\events'
        $narrow.Count | Should -BeLessOrEqual $evs.Count

        # Reversed window → Invalid
        Test-Path smoke_ttd:\range\end_to_start | Should -BeFalse

        # Alias without backing data → Invalid (python01.run has no exceptions)
        Test-Path 'smoke_ttd:\range\start_to_first-exception' | Should -BeFalse
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

Describe 'Capture round-trip (.NET tracer file/line)' -Tag 'Capture' -Skip:(-not $HasTiny) {
    AfterEach { Remove-SmokeDrives }

    It 'resolves portable-PDB sequence points for user methods' {
        # tinyapp is built Debug so .pdb is portable. We expect real Program.cs
        # line numbers on Add/Multiply/Divide — not line=0 with assembly name.
        New-CrashDrive smoke_cap -ExecutablePath $TinyApp
        $count = (Get-ChildItem smoke_cap:\events).Count
        $count | Should -BeGreaterThan 5

        # Scan the middle of the stream for a user method event.
        $events = 2..($count-2) | ForEach-Object {
            Get-Content "smoke_cap:\events\$_.json" | ConvertFrom-Json
        }
        $addEvents = $events | Where-Object function -match 'TinyApp\.Program\.Add'
        $addEvents.Count | Should -BeGreaterThan 0

        foreach ($e in $addEvents[0..([math]::Min(2, $addEvents.Count - 1))]) {
            $e.file | Should -Match '\.cs$' -Because 'source file should resolve to Program.cs, not the assembly name'
            $e.line | Should -BeGreaterThan 0
        }
    }
}
