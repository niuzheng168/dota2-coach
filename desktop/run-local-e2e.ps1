param(
    [ValidateSet("Start", "Arm", "Stop")][string]$Action = "Start",
    [string]$RunDirectory = "",
    [string]$FoundryEnvFile = "",
    [string]$DotaInstall = "C:\Steam\steamapps\common\dota 2 beta",
    [string]$Model = "gpt-6-astra",
    [ValidateRange(1,6)][int]$MaxModelCalls = 3
)
$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if (!$RunDirectory) { $RunDirectory = Join-Path $root (".tmp\twopc-model-e2e-" + (Get-Date -Format "yyyyMMdd-HHmmss")) }
$run = [IO.Path]::GetFullPath($RunDirectory)
$allowed = [IO.Path]::GetFullPath((Join-Path $root ".tmp")) + [IO.Path]::DirectorySeparatorChar
if (!$run.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw "Local acceptance artifacts must stay in this workspace's .tmp directory." }
$config = Join-Path $run "config.json"
function OwnedClients {
    @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -in @("DotaCoach.CoachClient.exe","DotaCoach.GameClient.exe") -and
        $_.CommandLine -and $_.CommandLine.Contains($config)
    })
}
if ($Action -eq "Arm") {
    if (!(Test-Path -LiteralPath $config) -or !(OwnedClients).Count) { throw "Start the named acceptance run first." }
    New-Item -ItemType File -Path (Join-Path $run "enable-model") -Force | Out-Null
    Write-Output "Armed bounded paid-model calls for fresh real frames in local bot matches only."
    return
}
if ($Action -eq "Stop") {
    if (!(Test-Path -LiteralPath $config)) { throw "Unknown acceptance run." }
    New-Item -ItemType File -Path (Join-Path $run "stop-requested") -Force | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    while ((OwnedClients).Count -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if ((OwnedClients).Count) { throw "Clients have not exited; configuration and credentials preserved." }
    $options = Get-Content -LiteralPath $config -Raw -Encoding UTF8 | ConvertFrom-Json
    $gsi = [IO.Path]::GetFullPath([string]$options.gsiConfigPath)
    $gsiRoot = [IO.Path]::GetFullPath((Join-Path $DotaInstall "game\dota\cfg\gamestate_integration")) + [IO.Path]::DirectorySeparatorChar
    if (!$gsi.StartsWith($gsiRoot,[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($gsi) -notlike "gamestate_integration_coach_e2e_*.cfg") { throw "Unexpected GSI cleanup target." }
    if (Test-Path -LiteralPath $gsi) {
        $expected = (Get-Content -LiteralPath (Join-Path $run "gsi-sha256.txt") -Raw).Trim()
        if ((Get-FileHash -LiteralPath $gsi -Algorithm SHA256).Hash -ne $expected) { throw "GSI file changed; preserved." }
        Remove-Item -LiteralPath $gsi
    }
    $pair = Join-Path $run "pairing.secret"
    if (Test-Path -LiteralPath $pair) { Remove-Item -LiteralPath $pair }
    Write-Output "Acceptance clients stopped; unchanged generated GSI and ephemeral pairing removed. Dota was not stopped."
    return
}
if ((OwnedClients).Count) { throw "This acceptance run is already active." }
New-Item -ItemType Directory -Path $run -Force | Out-Null
if (!(Test-Path -LiteralPath $config)) {
    if (!$FoundryEnvFile -or !(Test-Path -LiteralPath $FoundryEnvFile)) { throw "An explicit Foundry credential file is required. Its contents are never printed." }
    $gsi = Join-Path $DotaInstall ("game\dota\cfg\gamestate_integration\gamestate_integration_coach_e2e_" + (Get-Date -Format "yyyyMMdd_HHmmss") + ".cfg")
    [ordered]@{
        directory=$run; gsiConfigPath=[IO.Path]::GetFullPath($gsi)
        foundryEnvFile=[IO.Path]::GetFullPath($FoundryEnvFile); model=$Model
        lessonsPath=(Join-Path $root "data\luna-live-lessons.json")
        mapReferencePath=(Join-Path $root "data\dota2-map-reference.json")
        maxModelCalls=$MaxModelCalls; captureOverlayEvidence=$true
    } | ConvertTo-Json | Set-Content -LiteralPath $config -Encoding UTF8
}
$dotnet = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
foreach ($role in @("Coach","Game")) {
    & $dotnet build (Join-Path $PSScriptRoot "DotaCoach.${role}Client") -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed; no clients started." }
}
# Never let the game read a previous server's credential while the new server starts.
foreach ($name in @("pairing.secret","stop-requested","enable-model")) {
    $file = Join-Path $run $name
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
}
$processes = @()
foreach ($role in @("Coach","Game")) {
    $exe = Join-Path $PSScriptRoot "DotaCoach.${role}Client\bin\Release\net10.0-windows\DotaCoach.${role}Client.exe"
    # These are interactive acceptance windows, intentionally visible.
    $p = Start-Process -FilePath $exe -ArgumentList @("--local-bot-e2e",('"' + $config + '"')) -WorkingDirectory $root -WindowStyle Normal -PassThru
    $processes += [ordered]@{role=$role;pid=$p.Id}
    if ($role -eq "Coach") {
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while (!(Test-Path -LiteralPath (Join-Path $run "pairing.secret")) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        if (!(Test-Path -LiteralPath (Join-Path $run "pairing.secret"))) { throw "Coach startup failed; no game client started." }
    }
}
$processes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run "clients.json") -Encoding UTF8
Write-Output "Ready: $run"
Write-Output "Load the generated GSI in an insecure local bot game, confirm the scene, then run -Action Arm for this directory."
