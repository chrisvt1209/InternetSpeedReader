#requires -RunAsAdministrator
<#
.SYNOPSIS
  Publishes the agent and installs/updates it as the Windows Service "ThroughputMonitorAgent".

.DESCRIPTION
  - Publishes src\ThroughputMonitor.Agent (Release) to the install folder.
  - Registers the service to start automatically (delayed) as LocalSystem, which has the
    administrator rights that per-process tracking (ETW) needs.
  - Configures crash recovery: restart after 5 s, 30 s, 60 s.
  - Re-running the script updates an existing install. Your data (database + logs under
    C:\ProgramData\ThroughputMonitor) is never touched.
  - Put machine-specific settings in <InstallDir>\appsettings.local.json; the script never overwrites that file.

.EXAMPLE
  .\scripts\install-service.ps1
  .\scripts\install-service.ps1 -InstallDir "D:\Apps\ThroughputMonitor" -NoStart
#>
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'ThroughputMonitor\Agent'),
    [switch]$NoStart,
    # Also add the speedlog folder to the machine-wide PATH, so `speedlog` works in every new terminal.
    [switch]$AddToPath,
    # The web app runs as this user and needs to read the database the service writes (SQLite also
    # needs write access to the folder for its -shm file). Pass '' to skip changing permissions.
    [string]$DataAccessUser = "$env:USERDOMAIN\$env:USERNAME"
)

$ErrorActionPreference = 'Stop'

# Everything printed here is also saved, so the output survives a window that closes on its own.
$logFile = Join-Path $env:TEMP 'throughput-install.log'
Start-Transcript -Path $logFile -Force | Out-Null
Write-Host "Install log: $logFile"

try {
$serviceName = 'ThroughputMonitorAgent'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\ThroughputMonitor.Agent\ThroughputMonitor.Agent.csproj'

# 1. Stop the service if it exists, so its files are not locked.
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Host "Stopping $serviceName ..."
    Stop-Service -Name $serviceName -Force
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

# 2. Publish (framework-dependent: uses the installed .NET 11 runtime).
Write-Host "Publishing to $InstallDir ..."
dotnet publish $project -c Release -o $InstallDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)" }

$exe = Join-Path $InstallDir 'ThroughputMonitor.Agent.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe after publish" }

# 2b. Publish the speedlog command-line tool next to the agent folder.
$cliDir = Join-Path (Split-Path -Parent $InstallDir) 'Cli'
Write-Host "Publishing speedlog to $cliDir ..."
dotnet publish (Join-Path $repoRoot 'src\ThroughputMonitor.Cli\ThroughputMonitor.Cli.csproj') -c Release -o $cliDir --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (speedlog) failed (exit code $LASTEXITCODE)" }

if ($AddToPath) {
    $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    if (($machinePath -split ';') -notcontains $cliDir) {
        [Environment]::SetEnvironmentVariable('Path', ($machinePath.TrimEnd(';') + ';' + $cliDir), 'Machine')
        Write-Host "Added $cliDir to the machine PATH (open a new terminal to use 'speedlog')."
    }
}

# 3. Create or update the service.
if (-not $existing) {
    Write-Host "Creating service $serviceName ..."
    New-Service -Name $serviceName `
        -BinaryPathName "`"$exe`"" `
        -DisplayName 'Throughput Monitor Agent' `
        -Description 'Records network throughput (total and per program) to a local SQLite database.' `
        -StartupType Automatic | Out-Null
} else {
    Write-Host "Updating service $serviceName ..."
    & sc.exe config $serviceName binPath= "`"$exe`"" | Out-Null
}
& sc.exe config $serviceName start= delayed-auto | Out-Null
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
& sc.exe failureflag $serviceName 1 | Out-Null   # also restart when the process exits with an error code

# 3b. Data folder: created here (not by the service) so the web app's user may read the database.
$dataDir = Join-Path $env:ProgramData 'ThroughputMonitor'
New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
if ($DataAccessUser) {
    Write-Host "Granting $DataAccessUser modify access to $dataDir (for the web app) ..."
    & icacls.exe $dataDir /grant "${DataAccessUser}:(OI)(CI)M" /T /C | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning "Could not change permissions on $dataDir (icacls exit code $LASTEXITCODE)." }
}

# 4. Start.
if ($NoStart) {
    Write-Host "Installed. Start it with: Start-Service $serviceName"
} else {
    Start-Service -Name $serviceName
    Write-Host "Service started."
}

Write-Host ""
Write-Host "Install folder: $InstallDir"
Write-Host "Database:       $env:ProgramData\ThroughputMonitor\throughput.db"
Write-Host "Logs:           $env:ProgramData\ThroughputMonitor\logs"
Write-Host "Status:         Get-Service $serviceName"
Write-Host "Label sessions: & `"$cliDir\speedlog.exe`" mark `"Steam download`" --category `"game update`""
if (-not $AddToPath) { Write-Host "                (re-run with -AddToPath to type just 'speedlog')" }
}
catch {
    Write-Host ""
    Write-Host "INSTALL FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host $_.ScriptStackTrace
    Write-Host "Full output is saved in $logFile"
    Read-Host "Press Enter to close"
    exit 1
}
finally {
    Stop-Transcript | Out-Null
}
