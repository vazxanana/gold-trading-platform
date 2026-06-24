# Gold Trading Platform - GitHub Setup Script
# This script creates the GitHub repo and pushes your code

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Gold Trading Platform - GitHub Setup" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$repoPath = "C:\Users\Xanan\Documents\gold-trading-platform"
$repoName = "gold-trading-platform"
$githubUser = "vazxanana"
$repoUrl = "https://github.com/$githubUser/$repoName.git"

Write-Host "Repository setup details:" -ForegroundColor Green
Write-Host "  User: $githubUser"
Write-Host "  Repo: $repoName"
Write-Host "  URL: $repoUrl"
Write-Host ""

# Step 1: Navigate to repo
cd $repoPath
Write-Host "[1/4] Navigated to repository" -ForegroundColor Yellow

# Step 2: Create repo on GitHub (requires user to have created it manually)
Write-Host "[2/4] Check: Does the repository exist on GitHub?" -ForegroundColor Yellow
Write-Host "      Visit: https://github.com/new" -ForegroundColor Cyan
Write-Host "      If repo doesn't exist, create it now and come back." -ForegroundColor Magenta
Read-Host "      Press Enter when done, or Ctrl+C to cancel"

# Step 3: Add remote
Write-Host "[3/4] Adding remote origin..." -ForegroundColor Yellow
git remote remove origin 2>$null
git remote add origin $repoUrl
Write-Host "      Remote added!" -ForegroundColor Green

# Step 4: Push code
Write-Host "[4/4] Pushing code to GitHub..." -ForegroundColor Yellow
Write-Host "      (You may be prompted to authenticate)" -ForegroundColor Cyan
Write-Host ""

git branch -M main
git push -u origin main

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "SUCCESS! Your repo is live:" -ForegroundColor Green
    Write-Host "  $repoUrl" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "Push failed. Check the error above." -ForegroundColor Red
}

Read-Host "Press Enter to exit"
