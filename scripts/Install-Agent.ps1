#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs (or updates) the OneDevice Master Client as a Windows service.
.DESCRIPTION
    Copies the published agent to "%ProgramFiles%\OneDevice\MasterClient", registers the Windows service
    "OneDeviceMasterClient" (automatic start) and opens the local setup page.
    Configuration and logs live in "%ProgramData%\OneDevice\MasterClient" and are kept on update.
.PARAMETER Source
    Folder with the published agent (contains OneDevice.MasterClient.Agent.exe). Default: the folder of this script.
.EXAMPLE
    .\Install-Agent.ps1
#>
param(
    [string] $Source = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
$serviceName = 'OneDeviceMasterClient'
$target = Join-Path $env:ProgramFiles 'OneDevice\MasterClient'
$exe = Join-Path $target 'OneDevice.MasterClient.Agent.exe'

if (-not (Test-Path (Join-Path $Source 'OneDevice.MasterClient.Agent.exe'))) {
    throw "OneDevice.MasterClient.Agent.exe not found in '$Source'."
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Host "Stopping $serviceName ..."
    Stop-Service -Name $serviceName -Force
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

Write-Host "Copying agent to $target ..."
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item -Path (Join-Path $Source '*') -Destination $target -Recurse -Force -Exclude 'Install-Agent.ps1', 'Uninstall-Agent.ps1'

if (-not $existing) {
    Write-Host "Registering service $serviceName ..."
    New-Service -Name $serviceName -BinaryPathName "`"$exe`"" -DisplayName 'OneDevice Master Client' `
        -Description 'Connects IO-Link masters on this PC to the OneDevice cloud. Setup page: http://localhost:5080' `
        -StartupType Automatic | Out-Null
    # Restart automatically after a crash (5 s, 30 s, 60 s).
    & sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null
}

Start-Service -Name $serviceName
Write-Host "OneDevice Master Client is running. Opening http://localhost:5080 ..."
Start-Process 'http://localhost:5080'
