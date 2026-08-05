# lib/keep-awake.ps1 - shared "keep this host awake for the duration of the run" helper (spec 171 sections 1 + 3).
#
# Dot-source it, do not copy it:
#   . (Join-Path $scriptDir "lib\keep-awake.ps1")
# run-baseline-scheduled.ps1 and run-radar.ps1 BOTH dot-source this one file (reuse-over-copy: a duplicated
# primitive only ever gets the next fix in one of its copies).
#
# WHY THIS EXISTS. The measurement host is a Modern Standby (S0ix) machine. Connected standby is entered when
# the DISPLAY times out (AC display-off measured at 15 min), not when the sleep timer expires, and the Desktop
# Activity Moderator then suspends background work. Measured 2026-08-04: the baseline run spent 114 of 184
# wall-clock minutes suspended, and - the stronger cost - 54 of 343 collector sources failed as a clean time
# slice beginning the second the machine entered standby (sec-form4 lost 49 of 74 sources, usaspending 3 of 3),
# so Radar scored that day on roughly a third of the Form 4 universe while recording no collection warning.
# Raising the host's display timeout would mask this here and silently regress on the next machine; the RUN
# should declare that it needs the system awake.
#
# DECISIONS, each with its reason:
#   * ES_SYSTEM_REQUIRED ONLY - deliberately NOT ES_DISPLAY_REQUIRED. The run needs the CPU, not the panel;
#     holding the display would keep a screen lit for hours for no benefit.
#   * Held in the wrapper, NOT via `powercfg /requestsoverride`. The override is persistent, needs elevation
#     and survives a crashed run - it fails toward "this machine never sleeps again", which is a worse failure
#     than the one being fixed. A thread execution state is dropped when the process exits, so a crash cannot
#     leave the host permanently awake.
#   * The flag is PER-THREAD and holds only while that thread lives. Both callers' main thread stays alive for
#     the whole run (each waits synchronously on its child process), which is what makes this valid.
#     !! IF EITHER SCRIPT IS EVER RESTRUCTURED TO HAND THE RUN OFF (a job, a background thread, a detached
#     !! process) AND RETURN, THIS PROTECTION BREAKS SILENTLY - nothing will fail, the run will simply be
#     !! suspended again. Move the hold to whatever thread waits on the run.
#   * Released explicitly via Disable-RadarKeepAwake in a `finally`, so the intent is visible in the code
#     rather than left to process teardown.
#
# FAIL OPEN - A DELIBERATE CHOICE, STATED BECAUSE FAIL-OPEN DEFAULTS HAVE BEEN A RECURRING DEFECT HERE.
# If Add-Type or the P/Invoke fails (locked-down host, non-Windows, no compiler), these functions log a warning
# and the run CONTINUES. That is correct in THIS case specifically because the run's OUTPUT is unaffected: a
# suspended run produces byte-identical scores, just later. Refusing to run would trade a slow-but-correct
# result for no result at all. This is the opposite of the vocabulary/coverage fail-opens (specs 169/174),
# where continuing produced a WRONG answer that read as a right one - here the only thing at risk is wall
# clock, and the warning is what makes the degradation visible.
#
# Written to run under Windows PowerShell 5.1 (how the scheduled task launches the wrapper, via powershell.exe)
# as well as PowerShell 7+.

# Resolve (creating on first use) the P/Invoke shim. Returns $null if it cannot be created; callers fail open.
function Get-RadarKeepAwakeApi {
    [CmdletBinding()]
    param()

    $existing = 'Radar.Native.Power' -as [type]
    if ($existing) { return $existing }

    Add-Type -Name Power -Namespace 'Radar.Native' -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)]
public static extern uint SetThreadExecutionState(uint esFlags);
'@

    return ('Radar.Native.Power' -as [type])
}

# Ask Windows to keep the SYSTEM awake until Disable-RadarKeepAwake (or process exit). Returns $true if the
# request is held, $false if it could not be taken (fail open - see the header).
function Enable-RadarKeepAwake {
    [CmdletBinding()]
    param(
        [string]$Reason = 'a Radar run'
    )

    # NOTE the literals. PowerShell parses the hex literal 0x80000000 as Int32 (-2147483648), so the spec's
    # `0x80000000 -bor 0x00000001` yields -2147483647 and will not convert to the uint parameter. Written in
    # decimal with an explicit [uint32] cast, which is correct in both 5.1 and 7.
    $ES_CONTINUOUS      = [uint32]2147483648   # 0x80000000 - the request persists until it is reset
    $ES_SYSTEM_REQUIRED = [uint32]1            # 0x00000001 - system stays awake; the DISPLAY is NOT requested

    if ($null -eq $global:RadarKeepAwakeDepth) { $global:RadarKeepAwakeDepth = 0 }

    if ($global:RadarKeepAwakeDepth -gt 0) {
        # Nested: run-baseline-scheduled.ps1 invokes run-radar.ps1 IN THE SAME PROCESS, so both ask for the
        # hold. The outer request already covers the inner one; count the depth so the inner script's
        # `finally` cannot release the request the outer script is still relying on.
        $global:RadarKeepAwakeDepth++
        return $true
    }

    try {
        $api = Get-RadarKeepAwakeApi
        if ($null -eq $api) { throw "Add-Type produced no Radar.Native.Power type." }

        $previous = $api::SetThreadExecutionState($ES_CONTINUOUS -bor $ES_SYSTEM_REQUIRED)
        if ($previous -eq 0) {
            $lastError = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
            throw "SetThreadExecutionState returned 0 (Win32 error $lastError)."
        }

        $global:RadarKeepAwakeDepth = 1
        return $true
    }
    catch {
        Write-Warning ("Keep-awake: could NOT hold a system power request for $Reason ($($_.Exception.Message)). " +
                       "Continuing anyway - this is a deliberate fail-open: a suspended run produces byte-identical " +
                       "scores, just later, so refusing to run would trade a slow-but-correct result for no result. " +
                       "If this host uses Modern Standby, expect the run to take 3-5x longer and to lose collector " +
                       "sources to connected standby.")
        return $false
    }
}

# Release the request taken by Enable-RadarKeepAwake. Safe to call when nothing is held (it is a no-op), so it
# belongs unconditionally in a `finally`.
function Disable-RadarKeepAwake {
    [CmdletBinding()]
    param()

    $ES_CONTINUOUS = [uint32]2147483648   # 0x80000000 alone = clear ES_SYSTEM_REQUIRED, keep nothing held

    if ($null -eq $global:RadarKeepAwakeDepth -or $global:RadarKeepAwakeDepth -le 0) { return }

    $global:RadarKeepAwakeDepth--
    if ($global:RadarKeepAwakeDepth -gt 0) { return }   # an outer caller still needs the host awake

    try {
        $api = Get-RadarKeepAwakeApi
        if ($null -ne $api) { [void]$api::SetThreadExecutionState($ES_CONTINUOUS) }
    }
    catch {
        Write-Warning ("Keep-awake: could not release the system power request explicitly ($($_.Exception.Message)). " +
                       "It is dropped when this process exits, so the host cannot be left permanently awake.")
    }
}
