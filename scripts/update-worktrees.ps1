# Update All Worktrees to Latest Default Branch
# Run this script from the main repository directory
# It will update all Claude worktrees to the latest default branch

# ===== Project configuration (edit these for your project) =====
$projectName   = "radar"
$defaultBranch = "main"   # change to "master" if your repo uses master

function Invoke-Utility {
<#
.SYNOPSIS
Invokes an external utility, ensuring successful execution.

.DESCRIPTION
Invokes an external utility (program) and, if the utility indicates failure by 
way of a nonzero exit code, throws a script-terminating error.
This works around PowerShell treating Git's stderr output as errors.

.EXAMPLE
Invoke-Utility git checkout -b my-branch
#>
    $exe, $argsForExe = $Args
    $ErrorActionPreference = 'Continue'
    try { 
        & $exe $argsForExe 
    } catch { 
        Throw 
    }
    if ($LASTEXITCODE) { 
        Throw "$exe indicated failure (exit code $LASTEXITCODE; full command: $Args)." 
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Update All Worktrees to Latest Master" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Verify we're in a Git repository
if (-not (Test-Path ".git")) {
    Write-Host "ERROR: Not in a Git repository!" -ForegroundColor Red
    Write-Host "Please run this script from the repository root directory." -ForegroundColor Red
    exit 1
}

# Get the current directory (main repo)
$mainRepoPath = Get-Location

Write-Host "Main repository: $mainRepoPath" -ForegroundColor Yellow
Write-Host ""

# First, update the default branch in the main repository
Write-Host "--- Updating Main Repository ---" -ForegroundColor Cyan
$currentBranch = git rev-parse --abbrev-ref HEAD

if ($currentBranch -eq $defaultBranch) {
    Write-Host "  Currently on $defaultBranch branch" -ForegroundColor Green
    Write-Host "  Pulling latest changes..." -ForegroundColor White

    try {
        Invoke-Utility git pull
        Write-Host "  Main repository updated successfully" -ForegroundColor Green
    } catch {
        Write-Host "  Failed to update main repository: ${_}" -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "  Currently on branch: $currentBranch" -ForegroundColor Yellow
    Write-Host "  Fetching latest $defaultBranch..." -ForegroundColor White

    try {
        Invoke-Utility git fetch origin $defaultBranch
        Write-Host "  Fetched latest $defaultBranch" -ForegroundColor Green
    } catch {
        Write-Host "  Failed to fetch ${defaultBranch}: ${_}" -ForegroundColor Red
        exit 1
    }
}

Write-Host ""

# Get list of all worktrees
Write-Host "--- Finding Worktrees ---" -ForegroundColor Cyan
$worktreeList = git worktree list --porcelain

# Parse worktree list to get paths
$worktrees = @()
$currentWorktree = @{}

foreach ($line in $worktreeList -split "`n") {
    if ($line -match "^worktree (.+)$") {
        if ($currentWorktree.Count -gt 0) {
            $worktrees += $currentWorktree.Clone()
        }
        $currentWorktree = @{
            Path = $matches[1]
        }
    }
    elseif ($line -match "^branch (.+)$") {
        $currentWorktree.Branch = $matches[1]
    }
}
if ($currentWorktree.Count -gt 0) {
    $worktrees += $currentWorktree
}

# Filter out the main repository and find Claude worktrees
$claudeWorktrees = $worktrees | Where-Object {
    $_.Path -ne $mainRepoPath.Path -and
    $_.Path -match "$projectName-claude-\d+"
}

if ($claudeWorktrees.Count -eq 0) {
    Write-Host "  No Claude worktrees found" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    exit 0
}

Write-Host "  Found $($claudeWorktrees.Count) Claude worktree(s)" -ForegroundColor Green
Write-Host ""

# Update each worktree
foreach ($worktree in $claudeWorktrees) {
    $worktreePath = $worktree.Path
    $worktreeBranch = $worktree.Branch -replace "refs/heads/", ""
    
    Write-Host "--- Updating: $worktreePath ---" -ForegroundColor Cyan
    Write-Host "  Current branch: $worktreeBranch" -ForegroundColor White
    
    # Check if worktree directory exists
    if (-not (Test-Path $worktreePath)) {
        Write-Host "  WARNING: Directory not found, skipping..." -ForegroundColor Yellow
        Write-Host ""
        continue
    }
    
    # Change to worktree directory
    Push-Location $worktreePath
    
    try {
        # Check for uncommitted changes
        $status = git status --porcelain

        if ($status) {
            Write-Host "  WARNING: Uncommitted changes detected!" -ForegroundColor Yellow
            Write-Host "  Skipping update to avoid losing work" -ForegroundColor Yellow
            Write-Host "  Please commit or stash changes first" -ForegroundColor Yellow
        }
        else {
            # Compute the expected per-worktree base branch name
            $baseBranch = "$((Split-Path $worktreePath -Leaf))-$defaultBranch"

            # Check out the base branch (create it from origin/$defaultBranch if missing)
            $baseExists = git branch --list $baseBranch
            $checkoutOk = $false

            if ($baseExists) {
                Write-Host "  Checking out base branch: $baseBranch" -ForegroundColor White
                try {
                    Invoke-Utility git checkout $baseBranch
                    $checkoutOk = $true
                } catch {
                    Write-Host "  Failed to check out ${baseBranch}: ${_}" -ForegroundColor Red
                }
            }
            else {
                Write-Host "  Base branch $baseBranch not found; creating from origin/$defaultBranch..." -ForegroundColor White
                try {
                    Invoke-Utility git checkout -b $baseBranch origin/$defaultBranch
                    $checkoutOk = $true
                } catch {
                    Write-Host "  Failed to create ${baseBranch}: ${_}" -ForegroundColor Red
                }
            }

            if ($checkoutOk) {
                # Reset to latest origin/$defaultBranch (discarding any local changes)
                Write-Host "  Resetting to origin/$defaultBranch..." -ForegroundColor White

                try {
                    Invoke-Utility git reset --hard origin/$defaultBranch
                    Write-Host "  Updated successfully [OK]" -ForegroundColor Green
                } catch {
                    Write-Host "  Failed to update: ${_}" -ForegroundColor Red
                }
            }
            else {
                Write-Host "  Skipping update for this worktree" -ForegroundColor Yellow
            }
        }
    }
    finally {
        # Return to main repo directory
        Pop-Location
    }
    
    Write-Host ""
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Update Complete!" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "Summary:" -ForegroundColor Yellow
Write-Host "  Main repository: Updated" -ForegroundColor Green
Write-Host "  Claude worktrees: $($claudeWorktrees.Count) processed" -ForegroundColor Green
Write-Host ""
Write-Host "All worktrees are now synchronized with the latest $defaultBranch branch." -ForegroundColor White
Write-Host ""