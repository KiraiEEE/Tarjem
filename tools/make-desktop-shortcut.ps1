# Creates (or repairs) the "Tarjem (Latest)" shortcut on the desktop. It runs run-latest.ps1,
# which rebuilds the current source in Release and starts it.

$repo = Split-Path -Parent $PSScriptRoot
$desktop = [Environment]::GetFolderPath('Desktop')
$shell = New-Object -ComObject WScript.Shell

$link = $shell.CreateShortcut((Join-Path $desktop 'Tarjem (Latest).lnk'))
$link.TargetPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$link.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $PSScriptRoot 'run-latest.ps1')`""
$link.WorkingDirectory = $repo
$link.IconLocation = Join-Path $repo 'Assets\icon.ico'
$link.Description = 'Build the latest Tarjem source (Release) and run it'
$link.Save()

Write-Host "Created $(Join-Path $desktop 'Tarjem (Latest).lnk')"
