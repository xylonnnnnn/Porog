#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$Source = (Split-Path $PSScriptRoot -Parent),
    [string]$OwnerSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value,
    [switch]$NoLaunch
)
$ErrorActionPreference = 'Stop'
$null = New-Object System.Security.Principal.SecurityIdentifier($OwnerSid)
$destination = Join-Path $env:ProgramFiles 'Porog'
$data = Join-Path $env:ProgramData 'Porog'
if (Get-Service Porog -ErrorAction SilentlyContinue) { throw 'Porog is already installed. Uninstall before upgrading; data will be preserved.' }
foreach ($file in @('Service/Porog.Service.exe', 'Desktop/Porog.Desktop.exe')) {
    if (-not (Test-Path (Join-Path $Source $file))) { throw "Missing $file. Run scripts/Build.ps1 or extract the release ZIP first." }
}
if (Test-Path "$data/owner.json") {
    $previous = Get-Content "$data/owner.json" -Raw | ConvertFrom-Json
    if ($previous.sid -ne $OwnerSid) { throw 'The existing data belongs to another Windows user. Refusing to reassign it.' }
}
New-Item $destination,$data -ItemType Directory -Force | Out-Null
foreach ($folder in @($destination, $data)) {
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            (New-Object System.Security.Principal.SecurityIdentifier($sid)), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        (New-Object System.Security.Principal.SecurityIdentifier($OwnerSid)), 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    Set-Acl $folder $acl
}
Copy-Item "$Source/Service", "$Source/Desktop" $destination -Recurse -Force
@{ sid = $OwnerSid } | ConvertTo-Json | Set-Content "$data/owner.json" -Encoding UTF8
$binary = '"' + (Join-Path $destination 'Service/Porog.Service.exe') + '"'
New-Service -Name Porog -DisplayName 'Porog Focus Service' -BinaryPathName $binary -StartupType Automatic | Out-Null
try {
    & sc.exe description Porog 'Local schedule, domain and process protection for Porog.' | Out-Null
    & sc.exe failure Porog reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
    Start-Service Porog
    (Get-Service Porog).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
    $account = (New-Object System.Security.Principal.SecurityIdentifier($OwnerSid)).Translate([System.Security.Principal.NTAccount]).Value
    $action = New-ScheduledTaskAction -Execute "$destination/Desktop/Porog.Desktop.exe" -Argument '--tray'
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $account
    $principal = New-ScheduledTaskPrincipal -UserId $account -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName 'Porog Desktop' -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Porog.lnk'))
    $shortcut.TargetPath = "$destination/Desktop/Porog.Desktop.exe"; $shortcut.Save()
    if (-not $NoLaunch) { Start-ScheduledTask -TaskName 'Porog Desktop' }
    Write-Host "Installed for $account. Service: Porog. Data: $data"
} catch {
    Stop-Service Porog -ErrorAction SilentlyContinue
    & sc.exe delete Porog | Out-Null
    throw
}
