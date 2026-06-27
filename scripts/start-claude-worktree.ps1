# Interactive Claude Worktree Launcher
# Select a worktree, create a branch, and start Claude with a custom prompt

# ===== Project configuration (edit these for your project) =====
$projectName = "radar"

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
    # Workaround: Prevents 2> redirections applied to calls to this function
    #             from accidentally triggering a terminating error.
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
Write-Host "  Claude Worktree Launcher" -ForegroundColor Cyan
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

# Get list of all worktrees
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
        $currentWorktree.Branch = $matches[1] -replace "refs/heads/", ""
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
    Write-Host "No Claude worktrees found!" -ForegroundColor Red
    Write-Host "Please run setup-claude-worktrees.ps1 first." -ForegroundColor Yellow
    exit 1
}

# Display worktree options
Write-Host "Select a worktree:" -ForegroundColor Yellow
Write-Host ""

for ($i = 0; $i -lt $claudeWorktrees.Count; $i++) {
    $worktree = $claudeWorktrees[$i]
    $folderName = Split-Path $worktree.Path -Leaf
    $branch = $worktree.Branch
    
    Write-Host "  [$($i + 1)] " -ForegroundColor Cyan -NoNewline
    Write-Host "$folderName " -ForegroundColor White -NoNewline
    Write-Host "(branch: $branch)" -ForegroundColor Gray
}

Write-Host ""
Write-Host "  [0] Cancel" -ForegroundColor DarkGray
Write-Host ""

# Get user selection
do {
    $selection = Read-Host "Enter your choice (0-$($claudeWorktrees.Count))"
    $selectionNum = [int]$selection
} while ($selectionNum -lt 0 -or $selectionNum -gt $claudeWorktrees.Count)

# Check for cancel
if ($selectionNum -eq 0) {
    Write-Host ""
    Write-Host "Cancelled." -ForegroundColor Yellow
    exit 0
}

# Get selected worktree
$selectedWorktree = $claudeWorktrees[$selectionNum - 1]
$worktreePath = $selectedWorktree.Path
$currentBranch = $selectedWorktree.Branch

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Selected: $(Split-Path $worktreePath -Leaf)" -ForegroundColor Green
Write-Host "Current branch: $currentBranch" -ForegroundColor White
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Get branch name
Write-Host "Enter a name for your new branch:" -ForegroundColor Yellow
Write-Host "(e.g., add-scoring-engine, fix-evidence-dedup, add-rss-collector)" -ForegroundColor Gray
Write-Host ""
$branchName = Read-Host "Branch name"

# Validate branch name
if ([string]::IsNullOrWhiteSpace($branchName)) {
    Write-Host ""
    Write-Host "ERROR: Branch name cannot be empty!" -ForegroundColor Red
    exit 1
}

# Remove any spaces and special characters
$branchName = $branchName -replace '\s+', '-' -replace '[^a-zA-Z0-9\-_]', ''

Write-Host ""
Write-Host "Creating branch: $branchName" -ForegroundColor Cyan
Write-Host "(Note: Git may show red text below - this is normal)" -ForegroundColor DarkGray
Write-Host ""

# Change to worktree directory
Push-Location $worktreePath

try {
    # Check if branch already exists
    $branchExists = git branch --list $branchName
    
    if ($branchExists) {
        Write-Host "WARNING: Branch '$branchName' already exists!" -ForegroundColor Yellow
        $overwrite = Read-Host "Do you want to switch to it? (y/n)"
        
        if ($overwrite -eq 'y' -or $overwrite -eq 'Y') {
            Invoke-Utility git checkout $branchName
            Write-Host "Switched to existing branch: $branchName" -ForegroundColor Green
        } else {
            Write-Host "Cancelled." -ForegroundColor Yellow
            Pop-Location
            exit 0
        }
    }
    else {
        # Create new branch from current branch (which should be main after reset)
        Invoke-Utility git checkout -b $branchName
        Write-Host "Branch '$branchName' created successfully!" -ForegroundColor Green
    }
    
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "  Claude Prompt" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Enter your task for Claude:" -ForegroundColor Yellow
    Write-Host "(CLAUDE.md will be automatically referenced)" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Example: 'run next' or 'Implement docs/next/01-solution-skeleton.md'" -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "Enter your task (press Enter twice when done):" -ForegroundColor Yellow
    
    # Read multi-line prompt
    $promptLines = @()
    $emptyLineCount = 0
    
    while ($true) {
        $line = Read-Host
        
        if ([string]::IsNullOrWhiteSpace($line)) {
            $emptyLineCount++
            if ($emptyLineCount -ge 2) {
                break
            }
            $promptLines += ""
        }
        else {
            $emptyLineCount = 0
            $promptLines += $line
        }
    }
    
    $userTask = $promptLines -join "`n"
    $userTask = $userTask.Trim()
    
    if ([string]::IsNullOrWhiteSpace($userTask)) {
        Write-Host ""
        Write-Host "No task provided. Starting Claude in interactive mode..." -ForegroundColor Yellow
        Write-Host ""
        Write-Host "========================================" -ForegroundColor Cyan
        Write-Host "Opening new PowerShell window with Claude..." -ForegroundColor Green
        Write-Host "========================================" -ForegroundColor Cyan
        Write-Host ""
        
        # Start Claude in a new window
        Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$worktreePath'; claude"
    }
    else {
        # Build the full prompt with CLAUDE.md reference
        $fullPrompt = "Read CLAUDE.md first, then: $userTask"
        
        Write-Host ""
        Write-Host "========================================" -ForegroundColor Cyan
        Write-Host "Prompt copied to clipboard!" -ForegroundColor Green
        Write-Host "========================================" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Your prompt:" -ForegroundColor Yellow
        Write-Host $fullPrompt -ForegroundColor White
        Write-Host ""
        
        # Copy prompt to clipboard
        $fullPrompt | Set-Clipboard
        
        Write-Host "Opening new PowerShell window with Claude..." -ForegroundColor Green
        Write-Host "Paste your prompt (Ctrl+V) when Claude starts!" -ForegroundColor Yellow
        Write-Host ""
        
        # Start Claude in a new window
        Start-Process powershell -ArgumentList "-NoExit", "-Command", "cd '$worktreePath'; Write-Host 'Paste your prompt now (Ctrl+V)' -ForegroundColor Yellow; Write-Host ''; claude"
    }
    
} finally {
    # Return to main repo directory
    Pop-Location
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Session ended." -ForegroundColor White
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""