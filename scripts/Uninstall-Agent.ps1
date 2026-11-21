#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Removes the OneDevice Master Client Windows service and program files.
.PARAMETER RemoveData
    Also delete configuration and logs in "%ProgramData%\OneDevice\MasterClient".
#>
param([switch] $RemoveData)

$ErrorActionPreference = 'Stop'
$serviceName = 'OneDeviceMasterClient'

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete $serviceName | Out-Null
    Write-Host "Service $serviceName removed."
}

$programDir = Join-Path $env:ProgramFiles 'OneDevice\MasterClient'
if (Test-Path $programDir) { Remove-Item -Recurse -Force $programDir; Write-Host "Removed $programDir" }

if ($RemoveData) {
    $dataDir = Join-Path $env:ProgramData 'OneDevice\MasterClient'
    if (Test-Path $dataDir) { Remove-Item -Recurse -Force $dataDir; Write-Host "Removed $dataDir" }
}
