$ErrorActionPreference = 'Stop'

$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\TaskbarPulse'
$executable = Join-Path $installDirectory 'TaskbarPulse.exe'
if (Test-Path -LiteralPath $executable) {
    Start-Process -FilePath $executable -ArgumentList '--uninstall-network-helper' -Verb RunAs -Wait
}

Get-Process TaskbarPulse -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'TaskbarPulse' -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\TaskbarPulse' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:LOCALAPPDATA 'TaskbarPulse') -Recurse -Force -ErrorAction SilentlyContinue

if (Test-Path -LiteralPath $installDirectory) {
    Remove-Item -LiteralPath $installDirectory -Recurse -Force
}

Write-Host 'TaskbarPulse has been removed.'
