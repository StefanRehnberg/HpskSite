# Production Deployment Script for HpskSite
# This script prepares a complete deployment package for Simply.com hosting

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  HPSK Site - Production Deployment" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Clean Release build
Write-Host "[1/7] Cleaning Release build..." -ForegroundColor Yellow
dotnet clean HpskSite.csproj -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Clean failed!" -ForegroundColor Red
    exit 1
}
Write-Host "Clean completed successfully" -ForegroundColor Green
Write-Host ""

# Step 2: Build Release
Write-Host "[2/7] Building Release..." -ForegroundColor Yellow
dotnet build HpskSite.csproj -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Build failed!" -ForegroundColor Red
    exit 1
}
Write-Host "Build completed successfully" -ForegroundColor Green
Write-Host ""

# Step 3: Clean publish folder
Write-Host "[3/7] Cleaning publish folder..." -ForegroundColor Yellow
if (Test-Path 'C:\temp\publish') {
    Remove-Item -Path 'C:\temp\publish\*' -Recurse -Force
}
Write-Host "Publish folder cleaned" -ForegroundColor Green
Write-Host ""

# Step 4: Publish self-contained build
Write-Host "[4/7] Publishing self-contained build..." -ForegroundColor Yellow
Write-Host "    (This may take 30-60 seconds...)" -ForegroundColor Gray
dotnet publish HpskSite.csproj -c Release -r win-x86 --self-contained -o "C:/temp/publish"
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Publish failed!" -ForegroundColor Red
    exit 1
}
Write-Host "Publish completed successfully" -ForegroundColor Green
Write-Host ""

# Step 5: Copy production config
Write-Host "[5/7] Copying production configuration..." -ForegroundColor Yellow
Copy-Item 'appsettings.Production.json' -Destination 'C:\temp\publish\' -Force
Write-Host "Production config copied" -ForegroundColor Green
Write-Host ""

# Step 6: Remove folders that should persist on server (CRITICAL!)
Write-Host "[6/7] Removing folders that should persist on server..." -ForegroundColor Yellow

# Remove media folder (user uploads)
if (Test-Path 'C:\temp\publish\wwwroot\media') {
    Remove-Item -Path 'C:\temp\publish\wwwroot\media' -Recurse -Force
    Write-Host "  - Media folder removed" -ForegroundColor Green
} else {
    Write-Host "  - Media folder not found (OK)" -ForegroundColor Gray
}

# Remove App_Data folder (Firebase credentials, Umbraco runtime data)
if (Test-Path 'C:\temp\publish\App_Data') {
    Remove-Item -Path 'C:\temp\publish\App_Data' -Recurse -Force
    Write-Host "  - App_Data folder removed" -ForegroundColor Green
} else {
    Write-Host "  - App_Data folder not found (OK)" -ForegroundColor Gray
}
Write-Host ""

# Step 7: Create app_offline.htm (for graceful deployment)
Write-Host "[7/7] Creating app_offline.htm..." -ForegroundColor Yellow
$appOfflineContent = @"
<!DOCTYPE html>
<html>
<head>
    <meta charset="utf-8">
    <title>HPSK - Underhåll pågår</title>
    <style>
        body { font-family: Arial, sans-serif; text-align: center; padding: 50px; background: #f5f5f5; }
        .container { max-width: 500px; margin: 0 auto; background: white; padding: 40px; border-radius: 8px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); }
        h1 { color: #333; }
        p { color: #666; }
        .icon { font-size: 48px; margin-bottom: 20px; }
    </style>
</head>
<body>
    <div class="container">
        <div class="icon">🔧</div>
        <h1>Underhåll pågår</h1>
        <p>Sidan uppdateras just nu och är tillbaka om en stund.</p>
        <p><small>Vi ber om ursäkt för eventuella besvär.</small></p>
    </div>
</body>
</html>
"@
$appOfflineContent | Out-File -FilePath 'C:\temp\publish\app_offline.htm' -Encoding utf8
Write-Host "app_offline.htm created" -ForegroundColor Green
Write-Host ""

# Display summary
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  DEPLOYMENT PACKAGE READY!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Location: C:\temp\publish\" -ForegroundColor White
Write-Host ""
Write-Host "Next Steps:" -ForegroundColor Yellow
Write-Host "  1. Open FileZilla and connect to Simply.com (pistol.nu)" -ForegroundColor White
Write-Host "  2. Upload app_offline.htm FIRST to public_html/" -ForegroundColor White
Write-Host "     (This shows maintenance page to users)" -ForegroundColor Gray
Write-Host "  3. Upload ALL other files from C:\temp\publish\ to public_html/" -ForegroundColor White
Write-Host "  4. DELETE app_offline.htm from server when done" -ForegroundColor White
Write-Host "     (This brings the site back online)" -ForegroundColor Gray
Write-Host "  5. Test the site" -ForegroundColor White
Write-Host ""
Write-Host "PRESERVED ON SERVER (not in package):" -ForegroundColor Yellow
Write-Host "  - wwwroot/media/  (user uploads)" -ForegroundColor Gray
Write-Host "  - App_Data/       (Firebase credentials)" -ForegroundColor Gray
Write-Host ""
Write-Host "Deployment package ready!" -ForegroundColor Green
