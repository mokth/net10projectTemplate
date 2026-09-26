# Quick Configuration Test Script
# Run this to verify your configuration before deployment

Write-Host "=== ErpWeb Configuration Test ===" -ForegroundColor Cyan

# Test 1: Check if configuration files exist
Write-Host "`n[1/5] Checking configuration files..." -ForegroundColor Yellow

$configFiles = @(
    ".\ErpWeb\appsettings.json",
    ".\ErpWeb\appsettings.Production.json",
    ".\ErpWeb\appsettings.local.json"
)

foreach ($file in $configFiles) {
    if (Test-Path $file) {
        Write-Host "  ✓ $file exists" -ForegroundColor Green
    } else {
        Write-Host "  ✗ $file missing" -ForegroundColor Red
    }
}

# Test 2: Validate JSON syntax
Write-Host "`n[2/5] Validating JSON syntax..." -ForegroundColor Yellow

foreach ($file in $configFiles) {
    if (Test-Path $file) {
        try {
            $null = Get-Content $file -Raw | ConvertFrom-Json
            Write-Host "  ✓ $file - Valid JSON" -ForegroundColor Green
        } catch {
            Write-Host "  ✗ $file - Invalid JSON: $($_.Exception.Message)" -ForegroundColor Red
        }
    }
}

# Test 3: Check .NET SDK
Write-Host "`n[3/5] Checking .NET SDK..." -ForegroundColor Yellow

try {
    $dotnetVersion = dotnet --version
    Write-Host "  ✓ .NET SDK: $dotnetVersion" -ForegroundColor Green
} catch {
    Write-Host "  ✗ .NET SDK not found" -ForegroundColor Red
}

# Test 4: Check project structure
Write-Host "`n[4/5] Checking project structure..." -ForegroundColor Yellow

$projectFiles = @(
    ".\ErpWeb\ErpWeb.csproj",
    ".\ErpWeb\Program.cs",
    ".\ErpWeb\Menus\menus.xml"
)

foreach ($file in $projectFiles) {
    if (Test-Path $file) {
        Write-Host "  ✓ $file" -ForegroundColor Green
    } else {
        Write-Host "  ✗ $file missing" -ForegroundColor Red
    }
}

# Test 5: Check for required folders
Write-Host "`n[5/5] Checking required folders..." -ForegroundColor Yellow

$folders = @(
    ".\ErpWeb\wwwroot",
    ".\ErpWeb\Menus",
    ".\ErpWeb\Logs"
)

foreach ($folder in $folders) {
    if (Test-Path $folder) {
        Write-Host "  ✓ $folder exists" -ForegroundColor Green
    } else {
        Write-Host "  ✗ $folder missing" -ForegroundColor Red
    }
}

Write-Host "`n=== Test Complete ===" -ForegroundColor Cyan
Write-Host "`nIf all tests pass, you're ready to deploy!" -ForegroundColor Green
Write-Host "Run: .\deploy.ps1 -SqlServer 'YOUR_SERVER' -DbUser 'YOUR_USER' -DbPassword 'YOUR_PASSWORD'" -ForegroundColor Yellow