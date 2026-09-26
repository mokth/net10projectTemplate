# ErpWeb Deployment Script
# This script helps deploy ErpWeb to production

param(
    [string]$SqlServer,
    [string]$DbUser,
    [string]$DbPassword,
    [string]$OutputPath = ".\publish",
    [switch]$SkipBuild,
    [switch]$SkipPublish
)

Write-Host "=== ErpWeb Deployment Script ===" -ForegroundColor Cyan

# Step 1: Update configuration
Write-Host "`n[1/4] Updating production configuration..." -ForegroundColor Yellow

$appsettingsPath = ".\ErpWeb\appsettings.Production.json"
if (Test-Path $appsettingsPath) {
    $config = Get-Content $appsettingsPath -Raw | ConvertFrom-Json
    
    if ($SqlServer) {
        $config.ConnectionStrings.DefaultConnection = "Server=$SqlServer;Database=ERPWeb;User Id=$DbUser;Password=$DbPassword;TrustServerCertificate=True;MultipleActiveResultSets=true;Encrypt=True"
        Write-Host "  ✓ Connection string updated" -ForegroundColor Green
    }
    
    $config | ConvertTo-Json -Depth 10 | Set-Content $appsettingsPath
    Write-Host "  ✓ Configuration file updated" -ForegroundColor Green
} else {
    Write-Host "  ✗ Configuration file not found" -ForegroundColor Red
}

# Step 2: Restore packages
if (-not $SkipBuild) {
    Write-Host "`n[2/4] Restoring NuGet packages..." -ForegroundColor Yellow
    Push-Location ".\ErpWeb"
    dotnet restore
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  ✗ Package restore failed" -ForegroundColor Red
        Pop-Location
        exit 1
    }
    Write-Host "  ✓ Packages restored" -ForegroundColor Green
    Pop-Location
}

# Step 3: Build and Publish
if (-not $SkipPublish) {
    Write-Host "`n[3/4] Building and publishing..." -ForegroundColor Yellow
    Push-Location ".\ErpWeb"
    
    # Clean previous build
    if (Test-Path $OutputPath) {
        Remove-Item $OutputPath -Recurse -Force
    }
    
    # Publish
    dotnet publish -c Release -o $OutputPath
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  ✗ Build failed" -ForegroundColor Red
        Pop-Location
        exit 1
    }
    Write-Host "  ✓ Published to $OutputPath" -ForegroundColor Green
    Pop-Location
}

# Step 4: Create deployment package
Write-Host "`n[4/4] Creating deployment package..." -ForegroundColor Yellow

$deployPackage = "ErpWeb_Deploy_$(Get-Date -Format 'yyyyMMdd_HHmmss').zip"
if (Test-Path $OutputPath) {
    Compress-Archive -Path "$OutputPath\*" -DestinationPath $deployPackage -Force
    Write-Host "  ✓ Deployment package created: $deployPackage" -ForegroundColor Green
} else {
    Write-Host "  ✗ Publish output not found" -ForegroundColor Red
}

Write-Host "`n=== Deployment Complete ===" -ForegroundColor Cyan
Write-Host "`nNext Steps:" -ForegroundColor Yellow
Write-Host "1. Upload '$deployPackage' to your server" -ForegroundColor White
Write-Host "2. Extract to IIS application directory" -ForegroundColor White
Write-Host "3. Set environment variable: ASPNETCORE_ENVIRONMENT=Production" -ForegroundColor White
Write-Host "4. Configure IIS Application Pool: .NET CLR Version = 'No Managed Code'" -ForegroundColor White
Write-Host "5. Test: https://werp3.wincomcloud.com/erpweb" -ForegroundColor White

Write-Host "`nFor detailed instructions, see: DEPLOYMENT_GUIDE.md" -ForegroundColor Cyan