# run-next.ps1 - Radar autonomous "run next" driver
#
# Behaviour:
#   - If docs/next/ has a pending spec:
#       1. pick the next spec (lowest-numbered .md)
#       2. reset a worktree hard to origin/<default branch>
#       3. dispatch a HEADLESS, UNATTENDED claude session that implements that spec
#          (plan -> coder/reviewer loop -> commit -> push -> PR -> promote spec to docs/)
#   - If docs/next/ is empty (or -Plan is passed):
#       launch claude interactively in work-planner mode to generate the next specs.
#
# Run this from anywhere; paths default to this repo (the script lives in <repo>/scripts).
# Prerequisites: `claude` and `gh` on PATH, gh authenticated, push access to origin.

[CmdletBinding()]
param(
    [string]$ProjectName   = "radar",
    [string]$RepoPath      = (Split-Path $PSScriptRoot -Parent),
    [string]$WorktreeBase  = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
    [string]$DefaultBranch = "main",
    [int]   $WorktreeIndex = 1,        # which <project>-claude-N worktree to drive
    [switch]$Plan,                     # force work-planner mode even if specs exist
    [string]$PermissionFlag = "--dangerously-skip-permissions", # set "" to be prompted
    [switch]$CopilotReview,            # after the PR opens, wait for Copilot's FIRST review and fix its comments
    [int]   $CopilotPollSeconds = 180, # poll interval while waiting for the review (default 3 min)
    [int]   $CopilotTimeoutSeconds = 1200 # give up waiting after this long (default 20 min)
)

# Native tools (git, claude) write progress to stderr. Under 'Stop' that stderr gets turned into
# a terminating error, so we use 'Continue' and gate on exit codes via Invoke-Git instead.
$ErrorActionPreference = 'Continue'

function Write-Section($t) { Write-Host ""; Write-Host "==== $t ====" -ForegroundColor Cyan }

# Run git and fail only on a non-zero exit code (not on stderr/progress output).
function Invoke-Git {
    & git @args
    if ($LASTEXITCODE) { throw "git $($args -join ' ') failed (exit $LASTEXITCODE)" }
}

# --- sanity checks ---
if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
    throw "claude CLI not found on PATH."
}
if (-not (Test-Path (Join-Path $RepoPath ".git"))) {
    throw "Not a git repository: $RepoPath"
}
Set-Location $RepoPath

# --- find the next pending spec ---
$nextDir = Join-Path $RepoPath "docs/next"
$specs = @()
if (Test-Path $nextDir) {
    $specs = Get-ChildItem -Path $nextDir -Filter *.md -File | Sort-Object Name
}

# --- planner mode: no specs (or forced) ---
if ($Plan -or $specs.Count -eq 0) {
    if ($Plan) {
        Write-Section "Work-planner mode (forced)"
    } else {
        Write-Section "No pending specs in docs/next - launching work-planner"
    }
    $plannerPrompt = "Act as the Radar work planner defined in .claude/agents/radar-work-planner.md. " +
        "Inspect docs/ and docs/next/, do a gap analysis against the master specs, and generate the " +
        "next 1-3 small implementation specs into docs/next/. Do not write production code. " +
        "When done, commit the new specs and push to origin/$DefaultBranch so the next run can pick them up."
    # Interactive and in the main repo so a human can review the generated specs.
    & claude $plannerPrompt
    exit $LASTEXITCODE
}

$spec    = $specs[0]
$specRel = "docs/next/$($spec.Name)"
Write-Section "Next spec: $specRel"

# --- ensure the target worktree exists ---
$worktreePath = Join-Path $WorktreeBase "$ProjectName-claude-$WorktreeIndex"
$baseBranch   = "$ProjectName-claude-$WorktreeIndex-$DefaultBranch"
if (-not (Test-Path $worktreePath)) {
    Write-Host "Worktree not found; creating $worktreePath" -ForegroundColor Yellow
    Invoke-Git worktree add $worktreePath -b $baseBranch $DefaultBranch
}

# --- always start the worktree clean from origin/<default branch> ---
Write-Section "Resetting worktree to origin/$DefaultBranch"
Invoke-Git fetch origin $DefaultBranch
Push-Location $worktreePath
try {
    # Get onto the per-worktree base branch (create it from origin if missing), discarding any state.
    & git checkout -f $baseBranch *> $null
    if ($LASTEXITCODE) { Invoke-Git checkout -f -b $baseBranch "origin/$DefaultBranch" }
    Invoke-Git reset --hard "origin/$DefaultBranch"
    Invoke-Git clean -fd       # remove untracked cruft, but NOT gitignored local settings
    Write-Host "Worktree at: $(git rev-parse --short HEAD)" -ForegroundColor Green
} finally {
    Pop-Location
}

# --- dispatch the headless, unattended run ---
Write-Section "Dispatching claude (headless, unattended)"
$prompt = @"
run next: implement the spec $specRel.
You are running UNATTENDED in a git worktree, dispatched by run-next.ps1. Follow CLAUDE.md, but do
NOT pause for human plan approval - record your plan and proceed straight through Step 2 (branch),
Step 3 (coder/reviewer loop), and Step 4 (commit, push the feature branch, open a PR, and promote
the spec from docs/next/ to docs/). If the reviewer cannot approve within REVIEW_LOOPS, stop and
report instead of committing.
"@

Push-Location $worktreePath
try {
    # Pipe $null to give claude an immediate stdin EOF (PowerShell 5.1 has no `< /dev/null`),
    # otherwise headless `claude -p` waits ~3s for piped stdin before proceeding.
    if ([string]::IsNullOrWhiteSpace($PermissionFlag)) {
        $null | & claude -p $prompt
    } else {
        $null | & claude -p $prompt $PermissionFlag
    }
    $code = $LASTEXITCODE
} finally {
    Pop-Location
}

# --- optional: wait for Copilot's FIRST review and fix its inline comments (one cycle, then exit) ---
if ($CopilotReview) {
    if ($code -ne 0) {
        Write-Section "Copilot stage skipped (implementation run exited $code)"
    } else {
        Write-Section "Copilot review stage"
        Push-Location $worktreePath
        try {
            $branch = (& git rev-parse --abbrev-ref HEAD).Trim()
            $nwo    = (& gh repo view --json nameWithOwner -q .nameWithOwner 2>$null | Out-String).Trim()
            $pr     = (& gh pr view --json number -q .number 2>$null | Out-String).Trim()

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
- IGNORE CLAUDE.md Steps 0-2: do NOT pick a new spec and do NOT create a new branch - you are
  already on the correct branch for the existing PR.
- Fetch Copilot's inline review comments and address each actionable one with the smallest correct
  change:
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
}

Write-Section "Done (exit $code)"
Write-Host "Dispatched: $specRel  (worktree: $worktreePath)"
Write-Host "Merge that spec's PR before the next run, or the same spec will be picked again." -ForegroundColor Yellow
exit $code
