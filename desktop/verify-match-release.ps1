param(
    [Parameter(Mandatory, ParameterSetName = 'Verify')][string]$Directory,
    [Parameter(Mandatory, ParameterSetName = 'Rules')][switch]$LoadPackageRules
)
# Shared with the builder. This is the entire public provider contract, never a dump of local settings.
function Get-PublicModelProviders {
    [ordered]@{
        location = 'coach'; default = 'foundry'; modes = @('ordinary-image', 'minute-farm'); automaticFallback = $false
        foundry = [ordered]@{ id = 'foundry'; inference = 'POST /openai/v1/responses'; auth = 'api-key'; model = 'configured-deployment' }
        codey = [ordered]@{
            id = 'codey-copilot'; documentedVersion = '0.3.43'; defaultBaseUrl = 'http://127.0.0.1:4141/v1'
            loopbackOnly = $true; startsGateway = $false; catalog = 'GET /v1/models'; inference = 'POST /v1/responses'
            auth = 'Bearer'; keyEnvironmentVariable = 'CODEY_MODEL_API_KEY'; optionalDedicatedKeyFile = $true
            targetModels = @('gpt-6.2-astra', 'gpt-6.2-sol'); requiredOrigin = 'github-copilot'
            requiredApi = 'native-http-responses'; ordinaryImageRequiresVision = $true
        }
    }
}
function Get-PublicClientDlls([string]$Json, [string]$Client) {
    # Use the SDK-generated inventory: an arbitrary loose file isn't public just because it ends in .dll.
    try { $deps = ConvertFrom-Json -InputObject $Json -ErrorAction Stop } catch { throw 'Invalid SDK dependency inventory.' }
    $target = $deps.targets.PSObject.Properties[$deps.runtimeTarget.name].Value
    $dlls = @{}
    foreach ($lib in $target.PSObject.Properties) {
        foreach ($group in @('runtime', 'native')) {
            foreach ($asset in $lib.Value.$group.PSObject.Properties) {
                if ($asset.Name -like '*.dll') { $dlls[[IO.Path]::GetFileName($asset.Name)] = $true }
            }
        }
    }
    if (!$dlls.ContainsKey("$Client.dll") -or !$dlls.ContainsKey('DotaCoach.TwoPc.Core.dll')) { throw 'Incomplete SDK dependency inventory.' }
    return $dlls
}
function Assert-PublicClientMember([string]$Path, [string]$Client, [hashtable]$RuntimeDlls) {
    # Check relative names only. A dedicated key file can also be named notes.txt or settings.json:
    # allowlist loose content rather than opening files to look for tokens or trusting extensions.
    if ($Path -match '(^|/)\.?(codey|(?:github[-_.]?)?copilot|codex)([-._/]|$)' -or
        $Path -match '(^|/)frames/|(^|/)(\.env[^/]*|\.writer\.lock|(coach-settings|installed-gsi|sealed|replay|session|summary)\.json|(model-frame|overlay-screen)-[^/]*)$' -or
        $Path -match '\.(dem|bz2|pfx|pem|key|env|secret|jsonl|dpapi)$' -or
        ($Path -match '\.cfg$' -and $Path -ne 'replay-tools/runtime/packages/numpy/f2py/setup.cfg')) {
        throw 'Private configuration, gateway files or session data are not permitted in either client.'
    }
    if ($Path -in @("$Client.exe", "$Client.deps.json", "$Client.runtimeconfig.json", 'createdump.exe',
            'README.md', 'MODEL-PROVIDERS.md', 'MINUTE-FARM.md', 'MINUTE-OVERLAY-EXAMPLE.png', 'CLIENT-UI.md',
            'VIDEO-v040.md', 'DEBUG-DETAILS.md', 'PLUS-SPECTATOR.md', 'PLAYER-MINIMAP.md',
            'USAGE.md', 'NO-VIDEO.md', 'CREEP-HP.md', 'PLUS-ALERTS.md', 'ENEMY-WARDS.md', 'GAME-OVERLAY.md')) { return }
    if ($RuntimeDlls.ContainsKey($Path) -or
        ($Path -match '^[a-z]{2}(?:-[a-z]+)?/([^/]+)\.resources\.dll$' -and $RuntimeDlls.ContainsKey($Matches[1] + '.dll'))) { return }
    if ($Client -eq 'DotaCoach.GameClient' -and ($Path -ceq 'data/dota2-hero-attack.json' -or
            $Path -match '^icons/((npc_dota_hero_|item_)[a-z0-9_]+\.png|NOTICE\.txt)$')) { return }
    if ($Client -eq 'DotaCoach.CoachClient' -and (
            $Path -like 'replay-tools/*' -or # All members must also match the separately pinned worker inventory.
            $Path -in @('ENEMY-WARDS.md', 'hero-scan.md', 'MATCH-REVIEW-EXAMPLE.png') -or
            $Path -match '^data/(dota2-(coach-map|dictionary|map-reference|ward-spots-742)|luna-live-lessons)\.json$' -or
            $Path -match '^scripts/vision/((capture_replay|hero_scan|hud_recognizer|wgc)\.py|requirements\.txt)$')) { return }
    throw 'Unlisted package member; refusing to ship possible private configuration or credentials.'
}
if ($LoadPackageRules) { return }

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = (Resolve-Path -LiteralPath $Directory).Path
$release = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'manifest.json') | ConvertFrom-Json
# v0.6.0 is the archived, pre-provider release documented in the match review guide.
$needsProviderGuide = $release.version -cne '0.6.0'
if ($needsProviderGuide) {
    # Require the generated public metadata exactly: no credential values, private paths or extra settings.
    if ((ConvertTo-Json $release.modelProviders -Compress -Depth 5) -cne (ConvertTo-Json (Get-PublicModelProviders) -Compress -Depth 5)) {
        throw 'Missing or unexpected public model provider metadata.'
    }
    $guideHash = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot '../docs/twopc-model-providers.md') -Algorithm SHA256).Hash
} elseif ($release.PSObject.Properties['modelProviders']) {
    throw 'Unexpected provider metadata in a pre-provider release.'
}
$pinFile = Join-Path $PSScriptRoot 'DotaCoach.CoachClient/ReplayToolPin.cs'
$pin = [regex]::Match((Get-Content -LiteralPath $pinFile -Encoding UTF8 -Raw), 'ManifestSha256\s*=\s*"([A-Fa-f0-9]{64})"').Groups[1].Value
if (!$pin -or $pin -ne $release.postgameReplay.portableWorkerManifestSha256 -or @($release.artifacts).Count -ne 2) {
    throw "Unexpected two-PC release or unpinned replay worker."
}
$sha = [Security.Cryptography.SHA256]::Create()
function Entry-Hash($entry) {
    $stream = $entry.Open()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') } finally { $stream.Dispose() }
}
$core = @{}
try {
    foreach ($role in @('Game', 'Coach')) {
        $name = "DotaCoach.${role}Client"
        $item = @($release.artifacts | Where-Object role -eq $role)
        if ($item.Count -ne 1 -or $item[0].archive -cne "$name-v$($release.version)-win-x64.zip") { throw "Unexpected $role archive." }
        $zipPath = Join-Path $root $item[0].archive
        if ((Get-Item -LiteralPath $zipPath).Length -ne $item[0].bytes -or
            (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash -ne $item[0].sha256) { throw "$role ZIP hash/size mismatch." }
        $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
        try {
            $entries = @{}
            $sdk = $zip.GetEntry("$name.deps.json")
            if (!$sdk -or $sdk.Length -gt 4MB) { throw "$role missing a bounded SDK dependency inventory." }
            $reader = New-Object IO.StreamReader($sdk.Open())
            try { $runtimeDlls = Get-PublicClientDlls ($reader.ReadToEnd()) $name } finally { $reader.Dispose() }
            foreach ($e in $zip.Entries) {
                $path = $e.FullName.Replace('\','/')
                if ($path -match '(^[/\\]|:|(^|/)\.\.?(/|$)|//)') { throw "Unexpected ZIP path." }
                if ($path.EndsWith('/')) { continue }
                Assert-PublicClientMember $path $name $runtimeDlls
                if ($entries.ContainsKey($path)) { throw "Duplicate package member: $path" }
                $entries[$path] = $e
            }
            foreach ($required in @("$name.exe", "$name.dll", 'DotaCoach.TwoPc.Core.dll', 'README.md', 'MINUTE-FARM.md')) {
                if (!$entries.ContainsKey($required)) { throw "$role missing $required" }
            }
            if ($needsProviderGuide -and (!$entries.ContainsKey('MODEL-PROVIDERS.md') -or (Entry-Hash $entries['MODEL-PROVIDERS.md']) -ne $guideHash)) {
                throw "$role missing the release version of MODEL-PROVIDERS.md."
            }
            # Older match-review bundles predate the main-branch local HP/ward features. For
            # integrated releases, require the advertised offline guides and exact public attack data.
            if ($release.PSObject.Properties['heroAttackDataBuild']) {
                foreach ($doc in @('USAGE.md', 'NO-VIDEO.md', 'CREEP-HP.md', 'PLUS-ALERTS.md', 'ENEMY-WARDS.md', 'GAME-OVERLAY.md')) {
                    if (!$entries.ContainsKey($doc)) { throw "$role missing $doc" }
                }
                if ($role -eq 'Game') {
                    $attack = 'data/dota2-hero-attack.json'
                    if (!$entries.ContainsKey($attack) -or (Entry-Hash $entries[$attack]) -ne
                        (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot "../$attack") -Algorithm SHA256).Hash) {
                        throw 'Game ZIP missing the offline hero attack data used for lethal and tower-prep hints.'
                    }
                }
            }
            $core[$role] = Entry-Hash $entries['DotaCoach.TwoPc.Core.dll']
            $tools = @($entries.Keys | Where-Object { $_.StartsWith('replay-tools/', [StringComparison]::OrdinalIgnoreCase) })
            if ($role -eq 'Game') {
                if ($tools.Count -or $entries.ContainsKey('PostgameReplay.exe')) { throw "Replay parser leaked into game ZIP." }
            } else {
                foreach ($required in @('replay-tools/sha256.json','replay-tools/PostgameReplay.exe', 'MATCH-REVIEW-EXAMPLE.png',
                    'ENEMY-WARDS.md','scripts/vision/hero_scan.py','data/dota2-ward-spots-742.json')) {
                    if (!$entries.ContainsKey($required)) { throw "Coach missing $required" }
                }
                $member = $entries['replay-tools/sha256.json']
                if ($member.Length -gt 1MB -or (Entry-Hash $member) -ne $pin) { throw "The archived worker differs from the coach's compile-time pin." }
                $reader = New-Object IO.StreamReader($member.Open())
                try { $expected = ($reader.ReadToEnd() | ConvertFrom-Json).PSObject.Properties } finally { $reader.Dispose() }
                if ($tools.Count -ne @($expected).Count + 1) { throw "Missing or extra member in archived portable runtime." }
                foreach ($entry in $expected) {
                    $key = 'replay-tools/' + $entry.Name
                    if (!$entries.ContainsKey($key) -or (Entry-Hash $entries[$key]) -ne $entry.Value) { throw "Bad archived runtime member: $key" }
                }
                Write-Output "Verified all $(@($expected).Count) pinned files inside the coach ZIP."
            }
            Write-Output "$role ZIP: $($item[0].bytes) bytes; SHA-256 $($item[0].sha256)."
        } finally { $zip.Dispose() }
    }
    if ($core.Game -ne $core.Coach) { throw "The two clients contain different protocol assemblies." }
} finally { $sha.Dispose() }
