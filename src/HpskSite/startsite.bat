@echo off
echo Starting HpskSite...
echo.
cd /d C:\Repos\HpskSite
dotnet run --urls "https://localhost:44317"
pause
