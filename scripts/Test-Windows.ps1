#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param([string]$Package = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/Porog-win-x64'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$proof = Join-Path $root 'artifacts/windows-smoke'
New-Item $proof -ItemType Directory -Force | Out-Null
$checks = [System.Collections.Generic.List[string]]::new()
$target = $null
function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAILED: $Message" }
    $checks.Add("PASS: $Message"); Write-Host "PASS: $Message"
}
function Invoke-Porog([string]$Type, $Payload = @{}, [guid]$RequestId = [guid]::NewGuid()) {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new('.', 'Porog.v1', [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous, [System.Security.Principal.TokenImpersonationLevel]::Impersonation)
    try {
        $pipe.Connect(2000)
        $writer = [System.IO.StreamWriter]::new($pipe, [Text.UTF8Encoding]::new($false), 4096, $true)
        $writer.AutoFlush = $true
        $reader = [System.IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $false, 4096, $true)
        $writer.WriteLine((@{type=$Type;requestId=$RequestId.ToString();payload=$Payload} | ConvertTo-Json -Depth 20 -Compress))
        $response = $reader.ReadLineAsync()
        if (-not $response.Wait(4000)) { throw 'IPC response timed out' }
        return $response.Result | ConvertFrom-Json
    } finally { $pipe.Dispose() }
}
try {
    & "$Package/scripts/Install.ps1" -NoLaunch
    Assert-Check ((Get-Service Porog).Status -eq 'Running') 'Windows Service installed and running'
    $status = Invoke-Porog GetStatus
    Assert-Check ($status.ok -and $status.payload.serviceState -eq 'Running') 'Authenticated named pipe returns real protection state'
    $invalid = Invoke-Porog StartBreak @{durationMinutes=13}
    Assert-Check ($invalid.error.code -eq 'VALIDATION_ERROR') 'Invalid break rejected by service'
    $start = [DateTimeOffset]::Now.AddSeconds(5); $end = $start.AddSeconds(12)
    $schedule = @{id=[guid]::NewGuid().ToString();name='Integration focus';enabled=$true;
        startAt=$start.ToString('o');endAt=$end.ToString('o');domainTargets=@('porog-smoke.invalid');processTargets=@('Porog.TestTarget.exe');intention='Windows service smoke test'}
    $save = Invoke-Porog SaveConfig @{expectedVersion=0;schedules=@($schedule);settings=@{breakDurationMinutes=5;warningSeconds=60}}
    Assert-Check ($save.ok) 'Schedule persisted through IPC'
    $targetPath = Join-Path $root 'artifacts/test-target/Porog.TestTarget.exe'
    $target = Start-Process $targetPath -PassThru
    Start-Sleep -Seconds 7
    $active = Invoke-Porog GetStatus
    Assert-Check ($active.payload.activeSession.scheduleId -eq $schedule.id) 'Schedule activated automatically'
    Assert-Check ($target.HasExited) 'Real owner process terminated'
    Assert-Check ($active.payload.activeSession.attempts -ge 1) 'Observed process attempt counted'
    $hostsPath = Join-Path $env:SystemRoot 'System32/drivers/etc/hosts'
    Assert-Check ((Get-Content $hostsPath -Raw).Contains('porog-smoke.invalid')) 'Exact test domain added to hosts'
    $edit = Invoke-Porog SaveConfig @{expectedVersion=1;schedules=@();settings=@{breakDurationMinutes=5;warningSeconds=60}}
    Assert-Check ($edit.error.code -eq 'ACTIVE_SESSION_LOCKED') 'Direct IPC cannot cancel active interval'
    Restart-Service Porog
    Start-Sleep -Seconds 2
    $restored = Invoke-Porog GetStatus
    Assert-Check ($restored.payload.activeSession.scheduleId -eq $schedule.id) 'Service restart restores active interval'
    $left = [int][Math]::Ceiling(($end - [DateTimeOffset]::Now).TotalSeconds) + 2
    if ($left -gt 0) { Start-Sleep -Seconds $left }
    $done = Invoke-Porog GetStatus
    Assert-Check ($null -eq $done.payload.activeSession) 'Work interval ends automatically'
    Assert-Check (-not (Get-Content $hostsPath -Raw).Contains('porog-smoke.invalid')) 'Own hosts entries removed after interval'
    $future = $schedule.Clone(); $future.id = [guid]::NewGuid().ToString(); $future.name = 'Diploma focus'
    $future.startAt = [DateTimeOffset]::Now.AddMinutes(10).ToString('o'); $future.endAt = [DateTimeOffset]::Now.AddMinutes(55).ToString('o')
    $future.domainTargets = @('youtube.com','www.youtube.com','vk.com'); $future.processTargets = @('Telegram.exe')
    $null = Invoke-Porog SaveConfig @{expectedVersion=1;schedules=@($future);settings=@{breakDurationMinutes=5;warningSeconds=60}}
    $ui = Start-Process "$env:ProgramFiles/Porog/Desktop/Porog.Desktop.exe" -ArgumentList @('--capture-docs', ('"' + "$proof/screenshots" + '"')) -PassThru
    if (-not $ui.WaitForExit(30000)) { $ui.Kill(); throw 'UI capture timed out' }
    Assert-Check ((Get-ChildItem "$proof/screenshots/*.png").Count -eq 6) 'Six real WinForms screens rendered'
    Add-Type -AssemblyName System.Drawing
    $breakBitmap = [System.Drawing.Bitmap]::new("$proof/screenshots/06-break.png")
    try {
        $lightPixels = 0
        for ($x = 0; $x -lt $breakBitmap.Width; $x += 8) {
            for ($y = 0; $y -lt $breakBitmap.Height; $y += 8) {
                $pixel = $breakBitmap.GetPixel($x, $y)
                if ($pixel.R -gt 210 -and $pixel.G -gt 210 -and $pixel.B -gt 210) { $lightPixels++ }
            }
        }
        Assert-Check ($lightPixels -gt 40) 'Break form renders visible timer and instructions'
    } finally { $breakBitmap.Dispose() }
    $breakId = [guid]::NewGuid()
    $rest = Invoke-Porog StartBreak @{durationMinutes=3} $breakId
    Assert-Check ($rest.ok) 'Valid 3-minute break started'
    Restart-Service Porog; Start-Sleep -Seconds 2
    $restored = Invoke-Porog GetStatus
    Assert-Check ($restored.payload.breakSession.endsAt -eq $rest.payload.endsAt) 'Break absolute deadline survives service restart'
    $retry = Invoke-Porog StartBreak @{durationMinutes=3} $breakId
    Assert-Check ($retry.payload.sessionId -eq $rest.payload.sessionId) 'Break retry idempotent after restart'
} finally {
    if ($target -and -not $target.HasExited) { $target.Kill() }
    if (Get-Service Porog -ErrorAction SilentlyContinue) { & "$Package/scripts/Uninstall.ps1" -Recovery }
    Assert-Check (-not (Get-Service Porog -ErrorAction SilentlyContinue)) 'Service removed during test cleanup'
    $checks | Set-Content "$proof/results.txt" -Encoding UTF8
}
