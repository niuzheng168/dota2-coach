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
New-Item -ItemType Directory -Path $output | Out-Null
$artifacts = @()
foreach ($role in @("Game", "Coach")) {
    $name = "DotaCoach.${role}Client"
    $destination = Join-Path $output $name
    & $dotnet publish (Join-Path $PSScriptRoot "$name/$name.csproj") -c Release -r win-x64 `
        --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $destination
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $name" }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../docs/twopc-prototype.md") -Destination (Join-Path $destination "README.md")
    $unexpected = Get-ChildItem -LiteralPath $destination -Recurse -File | Where-Object {
        $_.Name -match '^\.env|^steam\.env$|\.(dem|pfx|pem|key|cfg|secret|jsonl)$'
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
    protocol = "paired-https-jpeg-v1"; automaticFarmingEnabled = $false
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output "manifest.json") -Encoding utf8
Write-Output "Unsigned self-contained two-PC packages: $output"
