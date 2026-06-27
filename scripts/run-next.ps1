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
    [string]$PermissionFlag = "--dangerously-skip-permissions"  # set "" to be prompted
)

$ErrorActionPreference = 'Stop'

function Write-Section($t) { Write-Host ""; Write-Host "==== $t ====" -ForegroundColor Cyan }

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
    & git worktree add $worktreePath -b $baseBranch $DefaultBranch
    if ($LASTEXITCODE) { throw "Failed to create worktree $worktreePath" }
}

# --- always start the worktree clean from origin/<default branch> ---
Write-Section "Resetting worktree to origin/$DefaultBranch"
& git fetch origin $DefaultBranch
Push-Location $worktreePath
try {
    # Get onto the per-worktree base branch (create it from origin if missing), discarding any state.
    & git checkout -f $baseBranch 2>$null
    if ($LASTEXITCODE) { & git checkout -f -b $baseBranch "origin/$DefaultBranch" }
    & git reset --hard "origin/$DefaultBranch"
    & git clean -fd            # remove untracked cruft, but NOT gitignored local settings
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
    if ([string]::IsNullOrWhiteSpace($PermissionFlag)) {
        & claude -p $prompt
    } else {
        & claude -p $prompt $PermissionFlag
    }
    $code = $LASTEXITCODE
} finally {
    Pop-Location
}

Write-Section "Done (exit $code)"
Write-Host "Dispatched: $specRel  (worktree: $worktreePath)"
Write-Host "Merge that spec's PR before the next run, or the same spec will be picked again." -ForegroundColor Yellow
exit $code
