param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "../dist/windows-listener"),
    [string]$ShareDirectory = "/nas/share"
)
$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "DotaCoach.Desktop/DotaCoach.Desktop.csproj"
$tests = Join-Path $PSScriptRoot "DotaCoach.CoreTests/DotaCoach.CoreTests.csproj"
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
[xml]$projectXml = Get-Content -LiteralPath $project -Raw
$versionNode = $projectXml.SelectSingleNode("/Project/PropertyGroup/Version")
$version = if ($null -ne $versionNode) { $versionNode.InnerText.Trim() } else { "" }
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
    throw "Set a valid release <Version> in $project before packaging."
}
$packageName = "DotaCoach-v$version-win-x64"
$archiveName = "$packageName.zip"
$checksumName = "$packageName.sha256"
$publishDirectory = Join-Path $output "v$version/app"
$archive = Join-Path $output $archiveName
$checksumFile = Join-Path $output $checksumName
$sharedArchive = Join-Path $ShareDirectory $archiveName
$sharedChecksum = Join-Path $ShareDirectory $checksumName
if (!(Test-Path -LiteralPath $ShareDirectory -PathType Container)) {
    throw "Package share is unavailable: $ShareDirectory. Mount the share or specify -ShareDirectory."
}
if ((Test-Path -LiteralPath $sharedArchive) -or (Test-Path -LiteralPath $sharedChecksum)) {
    throw "Version $version is already published. Increase <Version>; published packages must not be overwritten."
}

dotnet run --project $tests -c Release
if ($LASTEXITCODE -ne 0) { throw "Desktop core tests failed." }
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw "Windows publish failed." }
Copy-Item (Join-Path $PSScriptRoot "../docs/desktop-v020.md") (Join-Path $publishDirectory "README.md") -Force
Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $archive -Force
$checksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $checksumFile -Value "$checksum  $archiveName" -Encoding ascii
$temporaryArchive = Join-Path $ShareDirectory (".$packageName." + [Guid]::NewGuid().ToString("N") + ".tmp")
$temporaryChecksum = "$temporaryArchive.sha256"
try {
    Copy-Item -LiteralPath $archive -Destination $temporaryArchive -Force
    if ((Get-FileHash -LiteralPath $temporaryArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $checksum) {
        throw "Shared package checksum verification failed."
    }
    Copy-Item -LiteralPath $checksumFile -Destination $temporaryChecksum
    Move-Item -LiteralPath $temporaryArchive -Destination $sharedArchive
    Move-Item -LiteralPath $temporaryChecksum -Destination $sharedChecksum
} finally {
    if (Test-Path -LiteralPath $temporaryArchive) { Remove-Item -LiteralPath $temporaryArchive -Force }
    if (Test-Path -LiteralPath $temporaryChecksum) { Remove-Item -LiteralPath $temporaryChecksum -Force }
}
Write-Host "Built unsigned Windows x64 package: $archive"
Write-Host "Copied and SHA256-verified package: $sharedArchive"
