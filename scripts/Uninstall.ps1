#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param([switch]$Recovery)
$ErrorActionPreference = 'Stop'
$destination = Join-Path $env:ProgramFiles 'Porog'
$data = Join-Path $env:ProgramData 'Porog'
if (-not $Recovery) {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'Porog.v1', [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous, [System.Security.Principal.TokenImpersonationLevel]::Impersonation)
    try {
        $pipe.Connect(2000)
        $writer = New-Object System.IO.StreamWriter($pipe); $writer.AutoFlush = $true
        $reader = New-Object System.IO.StreamReader($pipe)
        $writer.WriteLine((@{ type='GetStatus'; requestId=[guid]::NewGuid().ToString(); payload=@{} } | ConvertTo-Json -Compress))
        $task = $reader.ReadLineAsync()
        if (-not $task.Wait(2000)) { throw 'Service response timed out' }
        $reply = $task.Result | ConvertFrom-Json
        if (-not $reply.ok) { throw 'Service rejected status query' }
        $now = [DateTimeOffset]::UtcNow
        if (($reply.payload.activeSession -and [DateTimeOffset]$reply.payload.activeSession.endsAt -gt $now) -or
            ($reply.payload.breakSession -and [DateTimeOffset]$reply.payload.breakSession.endsAt -gt $now)) {
            throw 'Wait until the active interval ends before uninstalling.'
        }
    } catch { throw "Cannot safely uninstall: $($_.Exception.Message). For explicit administrator recovery use -Recovery." }
    finally { $pipe.Dispose() }
}
Stop-Service Porog -ErrorAction SilentlyContinue
$service = Get-Service Porog -ErrorAction SilentlyContinue
if ($service) { $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20)) }
Unregister-ScheduledTask -TaskName 'Porog Desktop' -Confirm:$false -ErrorAction SilentlyContinue
Get-Process -Name Porog.Desktop -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq "$destination\Desktop\Porog.Desktop.exe" } | Stop-Process -Force
& "$destination/Service/Porog.Service.exe" --cleanup-hosts
if ($LASTEXITCODE -ne 0) { throw 'hosts cleanup failed. Service files and data retained for recovery.' }
if ($service) { & sc.exe delete Porog | Out-Null }
Remove-Item (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Porog.lnk') -ErrorAction SilentlyContinue
Remove-Item $destination -Recurse -Force
Write-Host "Uninstalled. Local settings and backups were preserved in $data."
