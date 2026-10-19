#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param([ValidateSet('Config','Runtime','Hosts')][string]$Target = 'Config')
$ErrorActionPreference = 'Stop'
$data = Join-Path $env:ProgramData 'Porog'
Stop-Service Porog -ErrorAction SilentlyContinue
$service = Get-Service Porog -ErrorAction SilentlyContinue
if ($service) { $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20)) }
if ($Target -eq 'Hosts') {
    & "$env:ProgramFiles/Porog/Service/Porog.Service.exe" --cleanup-hosts
    if ($LASTEXITCODE -ne 0) { throw 'Invalid markers or insufficient access; inspect hosts manually. No full-file restore was attempted.' }
} else {
    $name = if ($Target -eq 'Config') { 'config.json' } else { 'runtime.json' }
    $path = Join-Path $data $name
    if (-not (Test-Path "$path.bak")) { throw "Backup missing: $path.bak. Service remains stopped." }
    $null = Get-Content "$path.bak" -Raw | ConvertFrom-Json
    if (Test-Path $path) { Copy-Item $path "$path.corrupt.$([DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff'))" }
    Copy-Item "$path.bak" $path -Force
}
if ($service) { Start-Service Porog }
Write-Host 'Recovery applied. Verify the service status in Porog.'
