param(
    [string]$OutputDirectory = "",
    [string]$ReplayToolsDirectory = ""
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'verify-match-release.ps1') -LoadPackageRules
$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnet = if ($dotnetCommand) { $dotnetCommand.Source } else { Join-Path $env:ProgramFiles "dotnet/dotnet.exe" }
if (!(Test-Path -LiteralPath $dotnet)) { throw ".NET 10 SDK is required for building." }
[xml]$gameProject = Get-Content -LiteralPath (Join-Path $PSScriptRoot "DotaCoach.GameClient/DotaCoach.GameClient.csproj") -Raw
[xml]$coachProject = Get-Content -LiteralPath (Join-Path $PSScriptRoot "DotaCoach.CoachClient/DotaCoach.CoachClient.csproj") -Raw
$version = [string]$gameProject.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $version -ne [string]$coachProject.Project.PropertyGroup.Version) {
    throw "Both client projects must have the same valid version."
}
if (!$OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot "../dist/twopc/v$version" }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) {
    throw "Output already exists. Choose a fresh OutputDirectory; published packages are not overwritten."
}
# Only the coach ships the audited portable DEM reader. The game binary gets no worker or history.
if (!$ReplayToolsDirectory) { $ReplayToolsDirectory = Join-Path $PSScriptRoot "../.tmp/postgame-replay-v2" }
$ReplayToolsDirectory = (Resolve-Path -LiteralPath $ReplayToolsDirectory -ErrorAction Stop).Path
$pinSource = Get-Content -Encoding UTF8 -Raw -LiteralPath (Join-Path $PSScriptRoot "DotaCoach.CoachClient/ReplayToolPin.cs")
$pin = [regex]::Match($pinSource, 'ManifestSha256\s*=\s*"([A-Fa-f0-9]{64})"').Groups[1].Value
$manifestFile = Join-Path $ReplayToolsDirectory "sha256.json"
if (!$pin -or (Get-FileHash -LiteralPath $manifestFile -Algorithm SHA256).Hash -ne $pin) {
    throw "Postgame worker manifest differs from the coach pin. Build the audited tools with scripts/replays/postgame/build_windows.py."
}
$files = (Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestFile | ConvertFrom-Json).PSObject.Properties
$stageItems = @(Get-ChildItem -LiteralPath $ReplayToolsDirectory -Recurse -Force)
if (@($stageItems | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count -or
    @($stageItems | Where-Object { !$_.PSIsContainer }).Count -ne @($files).Count + 1) { throw "Unexpected portable worker tree." }
$workerMembers = @{'replay-tools/sha256.json' = $true}
foreach ($entry in $files) {
    $workerMembers['replay-tools/' + $entry.Name.Replace('\','/')] = $true
    $file = [IO.Path]::GetFullPath((Join-Path $ReplayToolsDirectory $entry.Name))
    if (!$file.StartsWith($ReplayToolsDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Value) { throw "Worker payload does not match its pin." }
}
& $dotnet run --project (Join-Path $PSScriptRoot "DotaCoach.TwoPc.Tests") -c Release
if ($LASTEXITCODE -ne 0) { throw "Two-PC protocol tests failed." }
& $dotnet run --file (Join-Path $PSScriptRoot "../tests/match-witness.test.cs") -c Release
if ($LASTEXITCODE -ne 0) { throw "Participant GSI lifecycle tests failed." }
& $dotnet run --project (Join-Path $PSScriptRoot "DotaCoach.MatchReview.Tests") -c Release -- --bundle $ReplayToolsDirectory
if ($LASTEXITCODE -ne 0) { throw "Match review and packaged replay worker tests failed." }
$uiOutput = Join-Path $PSScriptRoot ("../.tmp/twopc-release-ui-" + [Guid]::NewGuid().ToString("N"))
& $dotnet run --project (Join-Path $PSScriptRoot "DotaCoach.TwoPc.UiTests") -c Release -- $uiOutput
if ($LASTEXITCODE -ne 0) { throw "Two-PC native UI tests failed." }
# Enemy panel icons for the game client: Dota's own artwork from this machine's local extraction (never committed).
# The game client asks the coach for any icon not bundled, e.g. items from a later Dota patch.
function Copy-PanelIcons([string]$destination) {
    $assets = Join-Path $PSScriptRoot "../.tmp/vision-assets"
    if (!(Test-Path -LiteralPath (Join-Path $assets "manifest.json"))) {
        Write-Warning "No .tmp/vision-assets: the game client ships without panel icons and asks the coach for each one."
        return 0
    }
    $extracted = Get-Content -LiteralPath (Join-Path $assets "manifest.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    $folder = (New-Item -ItemType Directory -Path (Join-Path $destination "icons")).FullName
    $count = 0
    foreach ($kind in @("heroes", "items")) {
        foreach ($entry in $extracted.$kind.PSObject.Properties) {
            # Recipes are shown as empty slots, never drawn.
            if (!$entry.Value.icon -or $entry.Value.recipe) { continue }
            Copy-Item -LiteralPath (Join-Path $assets $entry.Value.icon) -Destination (Join-Path $folder "$($entry.Name).png")
            $count++
        }
    }
    Set-Content -LiteralPath (Join-Path $folder "NOTICE.txt") -Encoding ascii -Value ("Dota 2 hero and item icons belong to Valve. " +
        "Extracted unmodified from a locally installed Dota 2 client (build $($extracted.serverBuild)) for this personal tool; do not redistribute.")
    return $count
}
New-Item -ItemType Directory -Path $output | Out-Null
$artifacts = @()
$panelIcons = 0
foreach ($role in @("Game", "Coach")) {
    $name = "DotaCoach.${role}Client"
    $destination = Join-Path $output $name
    & $dotnet publish (Join-Path $PSScriptRoot "$name/$name.csproj") -c Release -r win-x64 `
        --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $destination
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $name" }
    if ($role -eq "Game") { $panelIcons = Copy-PanelIcons $destination }
    if ($role -eq "Coach") {
        Copy-Item -LiteralPath $ReplayToolsDirectory -Destination (Join-Path $destination "replay-tools") -Recurse
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../benchmark-results/match-full-journey-9097559249-v2/comparison.png") -Destination (Join-Path $destination "MATCH-REVIEW-EXAMPLE.png")
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-match-information.md") -Destination (Join-Path $destination "README.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-model-providers.md") -Destination (Join-Path $destination "MODEL-PROVIDERS.md")
    Add-Content -LiteralPath (Join-Path $destination "README.md") -Encoding UTF8 -Value "`n[v$version - Foundry / Codey (GitHub Copilot)](MODEL-PROVIDERS.md)"
    # Keep the no-video, local HP, enemy HUD and ward guides introduced in v0.4.3–8 alongside the new live/review guide.
    $guides = [ordered]@{
        'twopc-v043.md' = 'USAGE.md'; 'twopc-status-only.md' = 'NO-VIDEO.md'; 'game-creep-hp.md' = 'CREEP-HP.md'
        'twopc-plus-alerts.md' = 'PLUS-ALERTS.md'; 'twopc-enemy-wards.md' = 'ENEMY-WARDS.md'; 'twopc-game-overlay.md' = 'GAME-OVERLAY.md'
    }
    foreach ($guide in $guides.GetEnumerator()) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot ("../docs/" + $guide.Key)) -Destination (Join-Path $destination $guide.Value)
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-minute-farm-742.md") -Destination (Join-Path $destination "MINUTE-FARM.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../benchmark-results/minute-farm-742-overlay-example.png") -Destination (Join-Path $destination "MINUTE-OVERLAY-EXAMPLE.png")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-v042-ui.md") -Destination (Join-Path $destination "CLIENT-UI.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-v040.md") -Destination (Join-Path $destination "VIDEO-v040.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-v021-debug.md") -Destination (Join-Path $destination "DEBUG-DETAILS.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-v031-plus.md") -Destination (Join-Path $destination "PLUS-SPECTATOR.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-v041-minimap.md") -Destination (Join-Path $destination "PLAYER-MINIMAP.md")
    # Apply the same name-only privacy checks before compression and to the final ZIP. Include hidden files.
    $sdk = Get-Item -LiteralPath (Join-Path $destination "$name.deps.json")
    if (($sdk.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $sdk.Length -gt 4MB) { throw "Invalid staged SDK inventory." }
    $runtimeDlls = Get-PublicClientDlls (Get-Content -Raw -Encoding UTF8 -LiteralPath $sdk.FullName) $name
    foreach ($staged in Get-ChildItem -LiteralPath $destination -Recurse -Force) {
        if ($staged.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Package must not contain links to local files." }
        if ($staged.PSIsContainer) { continue }
        $member = $staged.FullName.Substring($destination.Length + 1).Replace('\','/')
        Assert-PublicClientMember $member $name $runtimeDlls
        if ($member -like 'replay-tools/*' -and !$workerMembers.ContainsKey($member)) { throw "Unexpected staged worker member." }
    }
    $archive = Join-Path $output "$name-v$version-win-x64.zip"
    Compress-Archive -Path (Join-Path $destination "*") -DestinationPath $archive
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$archive.sha256" -Value "$hash  $([IO.Path]::GetFileName($archive))" -Encoding ascii
    $artifacts += [ordered]@{ role = $role; archive = [IO.Path]::GetFileName($archive); sha256 = $hash;
        bytes = (Get-Item -LiteralPath $archive).Length; selfContained = $true; signed = $false }
}
$manifest = [ordered]@{
    version = $version; builtAt = [DateTimeOffset]::UtcNow.ToString("o"); artifacts = $artifacts;
    protocol = "paired-wss-h264-v3"; automaticFarmingEnabled = $true
    # The game client has a single picture-free connection; the other lanes exist only for UI tests and the bot E2E.
    defaultConnectionMode = "participant-gsi-local-hp"; videoUploadDefault = $false; gameVideoSelectable = $false
    connectionModes = @("participant-gsi-local-hp")
    developerConnectionModes = @("participant-gsi", "basic-status-local-hp", "diagnostic-status", "dota-video", "diagnostic-video")
    modelProviders = Get-PublicModelProviders
    minuteFarm = [ordered]@{ patch = "7.42"; map = "dota"; build = 6989; defaultOn = $false;
        requiresMatchConfirmation = $true; requiresConfirmedMinimap = $true; modelInput = "frozen-match-state-and-simulated-options";
        schedule = "one-attempt-per-in-game-minute"; heroes = @("luna", "troll_warlord"); maximumStops = 3 }
    ordinaryMatchAdvice = $true; mapMarkersRequireConfirmedCalibration = $false
    maximumEncodedImage = "4096x2304"; defaultEncodedHeight = 1080; modelImageMaximum = "1600x900"; supports4kSource = $true
    videoFrameRates = @(30, 60, 90, 120); defaultVideoFps = 60; videoBitrate = "auto-or-manual"; videoCapture = "Windows.Graphics.Capture-window"
    videoScaling = "D3D11-video-processor"; videoEncoder = "GPU-MFT-with-Windows-software-fallback"; videoProtocol = "dota-h264-v3"
    extractionLocation = "coach"; coachDebugSampleIntervalMs = 1000; videoRecorded = $false
    defaultRequestIntervalSeconds = 20; defaultProcessRequestLimit = 180
    defaultSubtitleSeconds = 3; defaultModelTimeoutSeconds = 15; automaticMinimapLayout = $true; modelRouteMaxStops = 3
    debugRecordingOptIn = $true; debugMaximumBytes = 2L * 1024 * 1024 * 1024; debugMaximumMinutes = 120
    plusSpectatorObservationOnly = $true; plusSpectatorPort = 3220
    matchArchive = [ordered]@{ live = "participant-and-matched-team-vision"; clock = "event-and-first-known";
        wardEvidence = @("seen", "inferred", "postgame-replay"); gameProjection = "selected-equipment-wards-and-calibrated-lane-symbols";
        laneHud = @{ build = 6989; defaultAfterCalibration = $true; individualMemoryGameSeconds = 12;
            lastContactMemoryGameSeconds = 30; rawSpectatorOrReplaySent = $false };
        extraTeamContextForModelDefault = $false; replayCanReachLive = $false }
    postgameReplay = [ordered]@{ package = "coach-only"; portableWorkerManifestSha256 = $pin; requiresVerifiedEnd = $true }
    playerMinimapDebugIntervalMs = 1000; playerMinimapObservationOnly = $true
    enemyPanel = $true; enemyPanelHold = "last-known-until-match-end"; bundledPanelIcons = $panelIcons; panelIconFallback = "coach"
    wardMarkLayout = "confirmed-box-or-game-hud-setting"; plusAlertsOverGsi = $true
    enemyWardGuesses = "coach-guess-game-minimap"; coachMapPatch = "7.42"
    creepHpDisplay = "game-local-gpu"; creepHpShown = "below-half"; creepHpLethal = "red-sure-kill-gsi-attack"
    towerPrepHits = "amber-count"; heroAttackDataBuild = 6989; gameStatusIntervalMs = 250
    visionScanButton = $true; wardMarksMaxUnseenSeconds = 15
    clientLayout = [ordered]@{ home = "status-and-everyday-switches"; coachSettings = @("connection", "plus-and-scanner", "ai-model", "minimap-calibration", "debug");
        gameSettings = @("overlay-style", "creep-hp-help", "dota-and-gsi", "debug") }
    wardReads = "sub-second-per-change"; wardCarrierItemsEverySeconds = 0.4; wardInSightMark = "hero-ahead-350"
    wardSpotWeights = "fitted-47-replays"
    sourceCommit = (& git -C (Join-Path $PSScriptRoot "..") rev-parse HEAD)
    sourceDirty = [bool](& git -C (Join-Path $PSScriptRoot "..") status --porcelain)
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output "manifest.json") -Encoding utf8
& (Join-Path $PSScriptRoot 'verify-match-release.ps1') -Directory $output
Write-Output "Unsigned self-contained two-PC packages: $output"
