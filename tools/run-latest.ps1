# Builds the current source in Release and starts it - what the "Tarjem (Latest)" desktop
# shortcut runs, so testing always means testing the code as it is right now, never a stale
# Debug or installed copy. Recreate the shortcut with tools\make-desktop-shortcut.ps1.

$ErrorActionPreference = 'Stop'
$Host.UI.RawUI.WindowTitle = 'Tarjem - building latest'

$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'bin\Release\net8.0-windows10.0.19041.0\Tarjem.exe'

# Any running copy has to go first: it locks the files the build overwrites, and the app's
# single-instance guard would hand the launch over to the old copy instead of starting the new one.
$running = Get-Process -Name Tarjem -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'Closing the running Tarjem...'
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

Write-Host 'Building the latest source (Release)...'
$output = & dotnet build (Join-Path $repo 'Tarjem.csproj') -c Release -nologo -v q 2>&1
$built = $LASTEXITCODE -eq 0

if ($built) {
    $stamp = (Get-Item $exe).LastWriteTime.ToString('d MMM HH:mm')
    Write-Host "Starting Tarjem (Release build, $stamp)" -ForegroundColor Green
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
    Start-Sleep -Milliseconds 800
    exit 0
}

$output | Where-Object { $_ -match 'error' } | Select-Object -Unique | ForEach-Object { Write-Host $_ -ForegroundColor Red }
Write-Host ''
Write-Host 'The build failed, so the latest code is NOT what would run.' -ForegroundColor Yellow

if (Test-Path $exe) {
    $stamp = (Get-Item $exe).LastWriteTime.ToString('d MMM HH:mm')
    $answer = Read-Host "Press Enter to start the last good build ($stamp) anyway, or close this window"
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
} else {
    Read-Host 'Press Enter to close'
}
