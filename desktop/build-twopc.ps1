param(
    [string]$OutputDirectory = ""
)
$ErrorActionPreference = "Stop"
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
& $dotnet run --project (Join-Path $PSScriptRoot "DotaCoach.TwoPc.Tests") -c Release
if ($LASTEXITCODE -ne 0) { throw "Two-PC protocol tests failed." }
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
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-v046.md") -Destination (Join-Path $destination "README.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-v043.md") -Destination (Join-Path $destination "USAGE.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-status-only.md") -Destination (Join-Path $destination "NO-VIDEO.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/game-creep-hp.md") -Destination (Join-Path $destination "CREEP-HP.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-plus-alerts.md") -Destination (Join-Path $destination "PLUS-ALERTS.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-enemy-wards.md") -Destination (Join-Path $destination "ENEMY-WARDS.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-game-overlay.md") -Destination (Join-Path $destination "GAME-OVERLAY.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-v021-debug.md") -Destination (Join-Path $destination "DEBUG-DETAILS.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-v031-plus.md") -Destination (Join-Path $destination "PLUS-SPECTATOR.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-v041-minimap.md") -Destination (Join-Path $destination "PLAYER-MINIMAP.md")
    $unexpected = Get-ChildItem -LiteralPath $destination -Recurse -File | Where-Object {
        $_.Name -match '^\.env|\.env$|\.(dem|pfx|pem|key|cfg|secret|jsonl|dpapi)$|^(coach-settings|installed-gsi|session|summary)\.json$|^(model-frame|overlay-screen)-' -or
        $_.FullName -match '[\\/]frames[\\/]'
    }
    if ($unexpected) { throw "Unexpected private/configuration files in package." }
    $archive = Join-Path $output "$name-v$version-win-x64.zip"
    Compress-Archive -Path (Join-Path $destination "*") -DestinationPath $archive
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$archive.sha256" -Value "$hash  $([IO.Path]::GetFileName($archive))" -Encoding ascii
    $artifacts += [ordered]@{ role = $role; archive = [IO.Path]::GetFileName($archive); sha256 = $hash;
        bytes = (Get-Item -LiteralPath $archive).Length; selfContained = $true; signed = $false }
}
$manifest = [ordered]@{
    version = $version; builtAt = [DateTimeOffset]::UtcNow.ToString("o"); artifacts = $artifacts;
    # Video upload is off (Features.VideoUpload): the video fields below describe the kept, switched-off path.
    protocol = "paired-https-gsi-status"; videoUpload = $false; automaticFarmingEnabled = $false
    ordinaryMatchAdvice = $false; mapMarkersRequireConfirmedCalibration = $false
    maximumEncodedImage = "4096x2304"; defaultEncodedHeight = 1080; modelImageMaximum = "1600x900"; supports4kSource = $true
    videoFrameRates = @(30, 60, 90, 120); defaultVideoFps = 60; videoBitrate = "auto-or-manual"; videoCapture = "Windows.Graphics.Capture-window"
    videoScaling = "D3D11-video-processor"; videoEncoder = "GPU-MFT-with-Windows-software-fallback"; videoProtocol = "dota-h264-v3"
    extractionLocation = "coach"; coachDebugSampleIntervalMs = 1000; videoRecorded = $false
    defaultRequestIntervalSeconds = 20; defaultProcessRequestLimit = 180
    defaultSubtitleSeconds = 3; defaultModelTimeoutSeconds = 15; automaticMinimapLayout = $true; modelRouteMaxStops = 3
    debugRecordingOptIn = $true; debugMaximumBytes = 2L * 1024 * 1024 * 1024; debugMaximumMinutes = 120
    plusSpectatorObservationOnly = $true; plusSpectatorPort = 3220
    playerMinimapDebugIntervalMs = 1000; playerMinimapObservationOnly = $true
    enemyPanel = $true; bundledPanelIcons = $panelIcons; panelIconFallback = "coach"
    enemyWardGuesses = "coach-guess-game-minimap"; coachMapPatch = "7.42"
    creepHpDisplay = "game-local-gpu"; creepHpShown = "below-half"; creepHpLethal = "red-sure-kill-gsi-attack"
    towerPrepHits = "amber-count"; heroAttackDataBuild = 6989; gameStatusIntervalMs = 250
    sourceCommit = (& git -C (Join-Path $PSScriptRoot "..") rev-parse HEAD)
    sourceDirty = [bool](& git -C (Join-Path $PSScriptRoot "..") status --porcelain)
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output "manifest.json") -Encoding utf8
Write-Output "Unsigned self-contained two-PC packages: $output"
