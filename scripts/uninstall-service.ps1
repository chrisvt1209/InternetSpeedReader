#requires -RunAsAdministrator
<#
.SYNOPSIS
  Stops and removes the "ThroughputMonitorAgent" Windows Service.

.DESCRIPTION
  By default only the service registration is removed. Use -RemoveFiles to also delete the
  install folder. The database and logs in C:\ProgramData\ThroughputMonitor are NEVER deleted by this script.
#>
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'ThroughputMonitor\Agent'),
    [switch]$RemoveFiles
)

$ErrorActionPreference = 'Stop'
$serviceName = 'ThroughputMonitorAgent'

$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') {
        Write-Host "Stopping $serviceName ..."
        Stop-Service -Name $serviceName -Force
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete $serviceName | Out-Null
    Write-Host "Service removed."
} else {
    Write-Host "Service $serviceName is not installed."
}

if ($RemoveFiles) {
    if (Test-Path $InstallDir) {
        Remove-Item -Path $InstallDir -Recurse -Force
        Write-Host "Removed $InstallDir"
    }
    $cliDir = Join-Path (Split-Path -Parent $InstallDir) 'Cli'
    if (Test-Path $cliDir) {
        Remove-Item -Path $cliDir -Recurse -Force
        Write-Host "Removed $cliDir"
    }
    # Undo -AddToPath from the installer, if it was used.
    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $kept = ($machinePath -split ';') | Where-Object { $_ -and $_ -ne $cliDir }
    if ($kept.Count -ne (($machinePath -split ';') | Where-Object { $_ }).Count) {
        [Environment]::SetEnvironmentVariable('Path', ($kept -join ';'), 'Machine')
        Write-Host "Removed $cliDir from the machine PATH."
    }
}

Write-Host "Your data in $env:ProgramData\ThroughputMonitor was left untouched."
