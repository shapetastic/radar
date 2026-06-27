# Git Worktree Setup Script for Claude Code Sessions
# Run this from PowerShell in your repository directory

# ===== Project configuration (edit these for your project) =====
$projectName      = "radar"
$repoPath         = "C:\Users\scm9d\source\repos\radar"
$worktreeBasePath = "C:\Users\scm9d\source\repos"
$defaultBranch    = "main"   # base branch new worktrees branch from (change to "master" if needed)

# Number of Claude sessions you want to create
$numSessions = 3

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Claude Code Worktree Setup" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "Repository: $repoPath" -ForegroundColor Yellow
Write-Host "Creating $numSessions isolated worktrees..." -ForegroundColor Yellow
Write-Host ""

# Change to the repository directory
Set-Location $repoPath

# Verify we're in a Git repository
$isGitRepo = Test-Path ".git"
if (-not $isGitRepo) {
    Write-Host "ERROR: Not a Git repository!" -ForegroundColor Red
    Write-Host "Please run this script from your Git repository directory." -ForegroundColor Red
    exit 1
}

# Get current branch
$currentBranch = git rev-parse --abbrev-ref HEAD
Write-Host "Current branch: $currentBranch" -ForegroundColor Green
Write-Host ""

# Create worktrees
for ($i = 1; $i -le $numSessions; $i++) {
    $worktreePath = "$worktreeBasePath\$projectName-claude-$i"
    $branchName = "$projectName-claude-$i-$defaultBranch"

    Write-Host "--- Session $i ---" -ForegroundColor Cyan
    Write-Host "  Path: $worktreePath"
    Write-Host "  Branch: $branchName"

    # Check if worktree already exists
    if (Test-Path $worktreePath) {
        Write-Host "  Status: Already exists (skipping)" -ForegroundColor Yellow
        continue
    }

    # Create the worktree with a new per-worktree base branch off the default branch
    git worktree add $worktreePath -b $branchName $defaultBranch 2>$null
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  Status: Created successfully [OK]" -ForegroundColor Green
    } else {
        Write-Host "  Status: Failed to create [ERROR]" -ForegroundColor Red
    }
    Write-Host ""
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Setup Complete!" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "All worktrees:" -ForegroundColor Yellow
git worktree list

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  How to Use" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "Open multiple PowerShell terminals and run:" -ForegroundColor White
Write-Host ""

for ($i = 1; $i -le $numSessions; $i++) {
    Write-Host "Terminal $i" -ForegroundColor Yellow -NoNewline
    Write-Host " - Navigate and start Claude:" -ForegroundColor White
    Write-Host "  cd $worktreeBasePath\$projectName-claude-$i" -ForegroundColor Cyan
    Write-Host "  claude" -ForegroundColor Cyan
    Write-Host ""
}

Write-Host "Each Claude session will work independently on its own branch!" -ForegroundColor Green
Write-Host ""
Write-Host "Useful commands:" -ForegroundColor Yellow
Write-Host "  git worktree list                    # List all worktrees" -ForegroundColor White
Write-Host "  git worktree remove <path>           # Remove a worktree" -ForegroundColor White
Write-Host "  git merge <branch>                   # Merge work back to main" -ForegroundColor White

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
