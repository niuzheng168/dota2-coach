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
New-Item -ItemType Directory -Path $output | Out-Null
$artifacts = @()
foreach ($role in @("Game", "Coach")) {
    $name = "DotaCoach.${role}Client"
    $destination = Join-Path $output $name
    & $dotnet publish (Join-Path $PSScriptRoot "$name/$name.csproj") -c Release -r win-x64 `
        --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $destination
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $name" }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-v040.md") -Destination (Join-Path $destination "README.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-v030-video.md") -Destination (Join-Path $destination "VIDEO-ARCHITECTURE-v030.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-v021-debug.md") -Destination (Join-Path $destination "DEBUG-DETAILS.md")
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-v020.md") -Destination (Join-Path $destination "NORMAL-USE.md")
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
    protocol = "paired-wss-h264-v3"; automaticFarmingEnabled = $true
    ordinaryMatchAdvice = $true; mapMarkersRequireConfirmedCalibration = $false
    maximumEncodedImage = "4096x2304"; defaultEncodedHeight = 1080; modelImageMaximum = "1600x900"; supports4kSource = $true
    videoFrameRates = @(30, 60, 90, 120); defaultVideoFps = 60; videoBitrate = "auto-or-manual"; videoCapture = "Windows.Graphics.Capture-window"
    videoScaling = "D3D11-video-processor"; videoEncoder = "GPU-MFT-with-Windows-software-fallback"; videoProtocol = "dota-h264-v3"
    extractionLocation = "coach"; coachDebugSampleIntervalMs = 1000; videoRecorded = $false
    defaultRequestIntervalSeconds = 20; defaultProcessRequestLimit = 180
    defaultSubtitleSeconds = 3; defaultModelTimeoutSeconds = 15; automaticMinimapLayout = $true; modelRouteMaxStops = 3
    debugRecordingOptIn = $true; debugMaximumBytes = 2L * 1024 * 1024 * 1024; debugMaximumMinutes = 120
    plusSpectatorObservationOnly = $true; plusSpectatorPort = 3220
    playerMinimapDebugIntervalMs = 1000; playerMinimapObservationOnly = $true
    sourceCommit = (& git -C (Join-Path $PSScriptRoot "..") rev-parse HEAD)
    sourceDirty = [bool](& git -C (Join-Path $PSScriptRoot "..") status --porcelain)
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output "manifest.json") -Encoding utf8
Write-Output "Unsigned self-contained two-PC packages: $output"
