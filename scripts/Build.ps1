#Requires -Version 5.1
[CmdletBinding()]
param([string]$Configuration = 'Release', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    dotnet restore Porog.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    dotnet build Porog.sln -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    if (-not $SkipTests) {
        dotnet test tests/Porog.Core.Tests -c $Configuration --no-build --logger 'trx;LogFileName=core.trx'
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    }
    $out = Join-Path $root 'artifacts/Porog-win-x64'
    foreach ($project in @('Service', 'Desktop')) {
        dotnet publish "src/Porog.$project" -c $Configuration -r win-x64 --self-contained true -o "$out/$project" -p:DebugType=None -p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) { throw "Publish $project failed" }
    }
    New-Item "$out/scripts" -ItemType Directory -Force | Out-Null
    Copy-Item scripts/Install.ps1,scripts/Uninstall.ps1,scripts/Repair.ps1 "$out/scripts"
    Copy-Item README.md "$out/README.md"
    Copy-Item docs/USER_GUIDE.md "$out/USER_GUIDE.md"
    Get-ChildItem $out -Recurse -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
        ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.FullName.Substring($out.Length + 1) } |
        Set-Content "$out/SHA256SUMS.txt" -Encoding UTF8
    Compress-Archive -Path "$out/*" -DestinationPath 'artifacts/Porog-win-x64.zip' -Force
    Write-Host 'Ready: artifacts/Porog-win-x64.zip'
} finally { Pop-Location }
