# continue-next.ps1 - RESUME an interrupted spec run in a worktree, WITHOUT resetting it.
#
# Why this exists:
#   run-next.ps1 ALWAYS starts by `git reset --hard origin/main` + `git clean -fd` on the worktree. That is
#   correct when starting a spec, and destructive when a run died part-way through one. A headless run that
#   hits a usage limit / crash / network failure leaves its work UNCOMMITTED in the worktree - CLAUDE.md
#   Step 4 only commits after the reviewer returns APPROVED, so an interrupted run has nothing committed by
#   design. Re-dispatching through run-next.ps1 then silently throws that work away.
#
#   On 2026-07-27 this cost two full implementations (~1.2k and ~3.2k diff lines) before it was noticed that
#   a third interrupted run was already building clean with every test passing - it had died at the REVIEW
#   step, not mid-implementation, and only needed Steps 3-4 finishing.
#
# What it does:
#   1. Refuses to run unless there is genuinely something to resume (feature branch + work present).
#   2. Runs the build/test gate FIRST and reports where the interrupted run actually got to.
#   3. Dispatches a headless claude IN PLACE with hard "do not reset/clean/stash/re-implement" guardrails,
#      told to do CLAUDE.md Step 3 (radar-code-reviewer to APPROVED) then Step 4 (commit/push/PR/promote).
#   4. Optionally runs the same Copilot review+fix stage as run-next.ps1.
#
#   # from anywhere:
#   powershell -File scripts/continue-next.ps1 -CopilotReview
#   powershell -File scripts/continue-next.ps1 -WorktreeIndex 2 -Spec 148
#   powershell -File scripts/continue-next.ps1 -Inspect          # report state and exit, dispatch nothing
#
# It never resets, never cleans, never stashes, and never creates a branch. If there is nothing to resume it
# tells you to use run-next.ps1 instead.

[CmdletBinding()]
param(
    [string]$ProjectName   = "radar",
    [string]$RepoPath      = "",   # defaults to <repo> (parent of scripts/)
    [string]$WorktreeBase  = "",   # defaults to <repo>'s parent
    [string]$DefaultBranch = "main",
    [int]   $WorktreeIndex = 1,        # which <project>-claude-N worktree to resume
    [string]$Spec          = "",       # spec being implemented; inferred from the branch name if omitted
    [switch]$Inspect,                  # report worktree state and exit without dispatching
    [switch]$SkipGate,                 # skip the build/test pre-check (faster, but you resume blind)
    [string]$PermissionFlag = "--dangerously-skip-permissions",
    [switch]$CopilotReview,
    [int]   $CopilotPollSeconds = 180,
    [int]   $CopilotTimeoutSeconds = 1200
)

$ScriptDir = $PSScriptRoot
if (-not $ScriptDir -and $PSCommandPath) { $ScriptDir = Split-Path -Parent $PSCommandPath }
if (-not $ScriptDir -and $MyInvocation.MyCommand.Path) { $ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $ScriptDir) { throw "Could not determine script directory; pass -RepoPath and -WorktreeBase explicitly." }

if (-not $RepoPath)     { $RepoPath     = Split-Path $ScriptDir -Parent }
if (-not $WorktreeBase) { $WorktreeBase = Split-Path $RepoPath -Parent }

# Native tools write progress to stderr; gate on exit codes instead of treating stderr as fatal.
$ErrorActionPreference = 'Continue'

function Write-Section($t) { Write-Host ""; Write-Host "==== $t ====" -ForegroundColor Cyan }

if (-not (Get-Command claude -ErrorAction SilentlyContinue)) { throw "claude CLI not found on PATH." }

$worktreePath = Join-Path $WorktreeBase "$ProjectName-claude-$WorktreeIndex"
$baseBranch   = "$ProjectName-claude-$WorktreeIndex-$DefaultBranch"
if (-not (Test-Path $worktreePath)) {
    throw "Worktree not found: $worktreePath  (nothing to resume - use run-next.ps1 to start a spec)"
}

Write-Section "Inspecting worktree $worktreePath"

Push-Location $worktreePath
try {
    $branch = (& git rev-parse --abbrev-ref HEAD).Trim()

    # --- is there anything to resume? ---
    if ($branch -eq $baseBranch) {
        throw "Worktree is on the base branch '$baseBranch' - no interrupted spec run to resume. Use run-next.ps1 to start one."
    }

    $dirty        = @(& git status --porcelain)
    $dirtyCount   = $dirty.Count
    # Commits on the feature branch that are not on origin/<default>. Missing remote ref is not fatal.
    $aheadRaw     = (& git rev-list --count "origin/$DefaultBranch..HEAD" 2>$null | Out-String).Trim()
    $ahead        = 0
    if ($aheadRaw -match '^\d+$') { $ahead = [int]$aheadRaw }

    if ($dirtyCount -eq 0 -and $ahead -eq 0) {
        throw "Branch '$branch' has no uncommitted changes and no commits ahead of origin/$DefaultBranch - nothing to resume."
    }

    # --- does a PR already exist? then this is a fix pass, not a resume ---
    $existingPr = (& gh pr view --json number -q .number 2>$null | Out-String).Trim()

    Write-Host "Branch          : $branch"
    Write-Host "Uncommitted     : $dirtyCount file(s)"
    Write-Host "Commits ahead   : $ahead (vs origin/$DefaultBranch)"
    if ($existingPr) {
        Write-Host "Open PR         : #$existingPr" -ForegroundColor Yellow
        Write-Host "  A PR already exists for this branch. Resuming will ADD to it, not open a new one." -ForegroundColor Yellow
    } else {
        Write-Host "Open PR         : none"
    }

    # --- infer the spec if not supplied: match docs/next/*.md against the branch name's words ---
    # NB: do NOT assign to $spec - PowerShell variable names are case-insensitive, so that would clobber the
    # caller's -Spec parameter (the same trap documented in run-next.ps1).
    $specRel = ""
    if ($Spec) {
        $candidates = @(Get-ChildItem -Path (Join-Path $RepoPath "docs/next") -Filter *.md -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ieq $Spec -or $_.BaseName -ieq $Spec -or $_.Name -ilike "$Spec-*" })
        if ($candidates.Count -eq 1) { $specRel = "docs/next/$($candidates[0].Name)" }
        elseif ($candidates.Count -eq 0) {
            # Already promoted by the interrupted run? Then it lives in docs/.
            $promoted = @(Get-ChildItem -Path (Join-Path $RepoPath "docs") -Filter *.md -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -ilike "$Spec-*" })
            if ($promoted.Count -eq 1) { $specRel = "docs/$($promoted[0].Name)" }
        }
    }
    if (-not $specRel) {
        # Fall back to the longest branch-name token overlap with a pending spec file name.
        $branchWords = ($branch -replace '^feature/','') -split '-' | Where-Object { $_.Length -gt 3 }
        $best = $null; $bestScore = 0
        foreach ($f in (Get-ChildItem -Path (Join-Path $RepoPath "docs/next") -Filter *.md -File -ErrorAction SilentlyContinue)) {
            $score = 0
            foreach ($w in $branchWords) { if ($f.BaseName -match [regex]::Escape($w)) { $score++ } }
            if ($score -gt $bestScore) { $bestScore = $score; $best = $f }
        }
        if ($best -and $bestScore -ge 2) { $specRel = "docs/next/$($best.Name)" }
    }

    if ($specRel) { Write-Host "Spec            : $specRel" }
    else          { Write-Host "Spec            : (could not infer - the resumed session will work it out from the diff)" -ForegroundColor Yellow }

    # --- build/test gate: find out where the interrupted run actually got to ---
    $gateSummary = "not run (-SkipGate)"
    if (-not $SkipGate) {
        Write-Section "Build/test gate (establishing where the interrupted run got to)"

        $buildOut = (& dotnet build Radar.sln -c Release 2>&1 | Out-String)
        $buildOk  = ($LASTEXITCODE -eq 0)
        if (-not $buildOk) {
            Write-Host "BUILD FAILED - the interrupted run stopped mid-implementation." -ForegroundColor Yellow
            $tail = ($buildOut -split "`n" | Select-Object -Last 15) -join "`n"
            Write-Host $tail
            $gateSummary = "BUILD FAILS - the previous session was interrupted MID-IMPLEMENTATION, so the working tree is incomplete. Finish the implementation first, then review."
        } else {
            Write-Host "Build: clean" -ForegroundColor Green
            $testOut = (& dotnet test Radar.sln -c Release --no-build 2>&1 | Out-String)
            $testOk  = ($LASTEXITCODE -eq 0)
            $passed = 0; $failed = 0
            foreach ($m in [regex]::Matches($testOut, 'Failed:\s+(\d+),\s+Passed:\s+(\d+)')) {
                $failed += [int]$m.Groups[1].Value
                $passed += [int]$m.Groups[2].Value
            }
            if ($testOk) {
                Write-Host "Tests: $passed passed, $failed failed" -ForegroundColor Green
                $gateSummary = "Build CLEAN and ALL TESTS PASS ($passed passed, 0 failed). The previous session finished implementing and was interrupted at or after the REVIEW step - do NOT re-implement, just review and finish."
            } else {
                Write-Host "Tests: $passed passed, $failed FAILED" -ForegroundColor Yellow
                $gateSummary = "Build clean but $failed test(s) FAIL ($passed passed). The previous session was interrupted before it got the suite green - fix the failures with the smallest correct change, then review."
            }
        }
    }
} finally {
    Pop-Location
}

if ($Inspect) {
    Write-Section "Inspect only - nothing dispatched"
    exit 0
}

# --- dispatch the continuation, IN PLACE ---
Write-Section "Dispatching claude to RESUME (no reset, no branch, no re-implementation)"

$specLine = "The spec being implemented is $specRel - read it first so you know what was asked."
if (-not $specRel) {
    $specLine = "The spec file could not be inferred automatically. Work out which docs/next/*.md spec this branch implements from the diff and the branch name, and read it first."
}
$prLine = "Open a PR with ``gh pr create``."
if ($existingPr) {
    $prLine = "A PR (#$existingPr) ALREADY EXISTS for this branch - push to it, do NOT open a new one."
}

$prompt = @"
You are RESUMING an interrupted unattended run in this git worktree, dispatched by continue-next.ps1. A
previous headless session was implementing a spec and was killed part-way through (usage limit, crash or
network failure). Its work is INTACT and UNCOMMITTED in this working tree on branch '$branch'.

CRITICAL - DO NOT DESTROY THE EXISTING WORK:
- Do NOT run git reset, git checkout --, git clean, or git stash.
- Do NOT create a new branch. You are already on the correct one ('$branch').
- Do NOT re-implement from scratch. Build on what is already in the working tree.
- Ignore CLAUDE.md Steps 0-2 entirely (spec selection, plan approval, branch creation) - they are already done.

MEASURED STATE (checked by continue-next.ps1 immediately before dispatching you):
- Uncommitted changes: $dirtyCount file(s)
- Commits ahead of origin/${DefaultBranch}: $ahead
- Gate: $gateSummary

$specLine

YOUR JOB - CLAUDE.md Step 3, then Step 4:
1. Read the spec, then review the existing uncommitted diff against it yourself. Identify anything the
   interrupted session had not finished, and complete it with the smallest correct change.
2. Keep the solution green: dotnet build Radar.sln -c Release, then dotnet test Radar.sln -c Release --no-build.
3. Invoke the radar-code-reviewer sub-agent. It MUST return APPROVED before anything is committed. If it
   returns ISSUES FOUND, fix and re-review, up to 3 iterations total. The reviewer step must not be skipped.
4. Then Step 4: git add -A, commit with a conventional message, push origin $branch. $prLine
   If the spec is still in docs/next/, promote it to docs/ with a follow-up commit and push.
5. If the reviewer cannot approve within 3 iterations, STOP and report - do not commit.

Report what you found still outstanding, what the reviewer said, and the PR URL.
"@

Push-Location $worktreePath
try {
    if ([string]::IsNullOrWhiteSpace($PermissionFlag)) {
        $null | & claude -p $prompt
    } else {
        $null | & claude -p $prompt $PermissionFlag
    }
    $code = $LASTEXITCODE
} finally {
    Pop-Location
}

if ($code -ne 0) {
    Write-Section "Resume run exited $code"
    Write-Host "The work is STILL in the worktree - it was not reset. Re-run continue-next.ps1 to pick up again." -ForegroundColor Yellow
    exit $code
}

# --- optional Copilot review + fix stage (same shape as run-next.ps1) ---
if ($CopilotReview) {
    Write-Section "Copilot review stage"
    Push-Location $worktreePath
    try {
        $nwo = (& gh repo view --json nameWithOwner -q .nameWithOwner 2>$null | Out-String).Trim()
        $pr  = (& gh pr view --json number -q .number 2>$null | Out-String).Trim()

        if ([string]::IsNullOrWhiteSpace($pr)) {
            Write-Host "No open PR found for branch '$branch'; skipping Copilot stage." -ForegroundColor Yellow
        } else {
            Write-Host "Waiting for Copilot's first review on PR #$pr (poll ${CopilotPollSeconds}s, timeout ${CopilotTimeoutSeconds}s)..." -ForegroundColor Yellow
            $found = $false; $elapsed = 0
            while ($elapsed -lt $CopilotTimeoutSeconds) {
                $reviewsRaw = (& gh api "repos/$nwo/pulls/$pr/reviews" 2>$null | Out-String)
                if ($reviewsRaw.Trim()) {
                    $reviews = $reviewsRaw | ConvertFrom-Json
                    if ($reviews | Where-Object { $_.user.login -like '*opilot*' }) { $found = $true; break }
                }
                Write-Host "  ...no Copilot review yet (${elapsed}s elapsed); checking again in ${CopilotPollSeconds}s"
                Start-Sleep -Seconds $CopilotPollSeconds
                $elapsed += $CopilotPollSeconds
            }

            if (-not $found) {
                Write-Host "Timed out after ${CopilotTimeoutSeconds}s waiting for Copilot; leaving PR #$pr as-is." -ForegroundColor Yellow
            } else {
                $commentsRaw = (& gh api "repos/$nwo/pulls/$pr/comments" 2>$null | Out-String)
                $copComments = @()
                if ($commentsRaw.Trim()) {
                    $copComments = @(($commentsRaw | ConvertFrom-Json) | Where-Object { $_.user.login -like '*opilot*' })
                }
                Write-Host "Copilot review landed with $($copComments.Count) inline comment(s)." -ForegroundColor Green

                if ($copComments.Count -eq 0) {
                    Write-Host "No actionable inline comments; nothing to fix." -ForegroundColor Green
                } else {
                    Write-Section "Dispatching claude to address Copilot comments"
                    $fixPrompt = @"
You are running UNATTENDED in a git worktree on branch '$branch', which already has open PR #$pr.
GitHub Copilot has left review comments on that PR. This is a FIX pass, not a new task:
- IGNORE CLAUDE.md Steps 0-2: do NOT pick a new spec and do NOT create a new branch.
- Fetch Copilot's inline review comments and address each actionable one with the smallest correct change:
    gh api repos/$nwo/pulls/$pr/comments --jq '.[] | "[\(.path):\(.line)] \(.body)"'
  If a comment is wrong or not actionable, do not change code - explain why in your final summary.
- Keep the solution green: dotnet build Radar.sln -c Release  and  dotnet test Radar.sln -c Release.
- Commit ("fix: address Copilot review comments") and push to origin/$branch.
- Do NOT open a new PR, do NOT request another review, and do NOT loop.
"@
                    if ([string]::IsNullOrWhiteSpace($PermissionFlag)) {
                        $null | & claude -p $fixPrompt
                    } else {
                        $null | & claude -p $fixPrompt $PermissionFlag
                    }
                    Write-Host "Copilot fix pass exit: $LASTEXITCODE" -ForegroundColor Green
                }
            }
        }
    } finally {
        Pop-Location
    }
}

Write-Section "Done (exit $code)"
Write-Host "Resumed in worktree: $worktreePath"
exit $code
