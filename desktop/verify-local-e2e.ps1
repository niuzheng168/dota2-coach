param(
    [Parameter(Mandatory=$true)][string]$RunDirectory,
    [ValidateRange(1,6)][int]$MinimumCompleteChains = 1
)
$ErrorActionPreference = "Stop"
$run = [IO.Path]::GetFullPath($RunDirectory)
$clients = Get-Content -LiteralPath (Join-Path $run "clients.json") -Raw -Encoding UTF8 | ConvertFrom-Json
# Windows PowerShell 5 emits a JSON array as one pipeline object. Do not wrap that
# pipeline in @(...), which nests the array and makes both roles select both PIDs.
$coachRows = @(foreach ($client in $clients) { if ($client.role -eq "Coach") { $client } })
$gameRows = @(foreach ($client in $clients) { if ($client.role -eq "Game") { $client } })
if ($coachRows.Count -ne 1 -or $gameRows.Count -ne 1) { throw "Missing identities of the latest launched client pair." }
$coachProcess = $coachRows[0].pid
$gameProcess = $gameRows[0].pid
$coach = @(Get-Content -LiteralPath (Join-Path $run "coach.jsonl") -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json })
$game = @(Get-Content -LiteralPath (Join-Path $run "game.jsonl") -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json } |
    Where-Object { $_.processId -eq $gameProcess })
Add-Type -AssemblyName System.Drawing
function GoldPixels($bitmap, [int]$left, [int]$top, [int]$right, [int]$bottom) {
    $count = 0
    for ($y=[Math]::Max(0,$top); $y -lt [Math]::Min($bitmap.Height,$bottom); $y++) {
        for ($x=[Math]::Max(0,$left); $x -lt [Math]::Min($bitmap.Width,$right); $x++) {
            $p=$bitmap.GetPixel($x,$y)
            if ($p.R -gt 170 -and $p.G -gt 140 -and $p.B -lt 120) { $count++ }
        }
    }
    return $count
}
$proofs = @()
foreach ($published in $coach | Where-Object { $_.processId -eq $coachProcess -and $_.stage -eq "model-published" -and $_.details.reminder.marker }) {
    $r = $published.details.reminder
    $model = $coach | Where-Object {
        $_.stage -eq "model-result" -and $_.processId -eq $published.processId -and
        $_.details.frame -eq $r.frameSequence -and $_.details.sessionId -eq $r.sessionId -and
        $_.details.result.advice.hero -eq "npc_dota_hero_luna"
    } | Select-Object -Last 1
    $start = $coach | Where-Object {
        $_.stage -eq "model-start" -and $_.processId -eq $published.processId -and
        $_.details.frame -eq $r.frameSequence -and $_.details.sessionId -eq $r.sessionId
    } | Select-Object -Last 1
    $received = $game | Where-Object { $_.stage -eq "reminder-received" -and $_.details.reminder.id -eq $r.id } | Select-Object -First 1
    $rendered = $game | Where-Object { $_.stage -eq "overlay-rendered" -and $_.details.id -eq $r.id } | Select-Object -First 1
    $screen = $game | Where-Object { $_.stage -eq "overlay-screen-captured" -and $_.details.id -eq $r.id } | Select-Object -First 1
    $clear = $game | Where-Object { $_.stage -eq "overlay-cleared" -and $_.details.id -eq $r.id } | Select-Object -First 1
    if (!$model -or !$start -or !$received -or !$rendered -or !$screen -or !$clear) { continue }
    $player = $start.details.player
    if ($player.matchId -ne "0" -or $player.hero -ne "npc_dota_hero_luna" -or
        $player.items.name -notcontains "item_power_treads" -or $player.items.name -notcontains "item_mask_of_madness" -or
        !($player.abilities | Where-Object { $_.name -eq "luna_moon_glaive" -and $_.level -eq 4 })) { continue }
    if ($model.details.result.responseId -notlike "resp_*" -or !$model.details.result.advice.targetId -or
        $r.kind -ne "experimental-bot-advice" -or $r.message -ne $model.details.result.advice.message -or
        $rendered.details.message -ne $r.message -or $rendered.details.elements -ne 3) { continue }
    $age = ([DateTimeOffset]$rendered.at - [DateTimeOffset]$start.at).TotalMilliseconds
    $duration = ([DateTimeOffset]$clear.at - [DateTimeOffset]$rendered.at).TotalMilliseconds
    if ($age -lt 0 -or $age -ge 12000 -or $duration -lt 0 -or $duration -gt 8300) { continue }
    $sourceFile = Join-Path $run ("model-frame-" + $r.sessionId + "-" + $r.frameSequence + ".jpg")
    $screenFile = Join-Path $run ([IO.Path]::GetFileName([string]$screen.details.artifact))
    if (!(Test-Path -LiteralPath $sourceFile) -or !(Test-Path -LiteralPath $screenFile)) { continue }
    $source = [Drawing.Bitmap]::new($sourceFile)
    $image = [Drawing.Bitmap]::new($screenFile)
    try {
        if ($image.Width -ne 1280 -or $image.Height -ne 720 -or $source.Size -ne $image.Size) { continue }
        $x = [int]($r.marker.x * $image.Width); $y = [int]($r.marker.y * $image.Height)
        $markerDifference = (GoldPixels $image ($x-18) ($y-35) ($x+18) ($y+18)) -
            (GoldPixels $source ($x-18) ($y-35) ($x+18) ($y+18))
        $captionDifference = (GoldPixels $image 180 65 1100 215) - (GoldPixels $source 180 65 1100 215)
        if ($markerDifference -lt 40 -or $captionDifference -lt 200) { continue }
        $proofs += [ordered]@{
            reminderId=$r.id; sessionId=$r.sessionId; sourceFrame=$r.frameSequence
            responseId=$model.details.result.responseId; model=$model.details.result.model
            hero=$player.hero; team=$player.team; sourceGameClock=$player.clock
            targetId=$model.details.result.advice.targetId; markerPixel=@($x,$y)
            modelMs=$model.details.result.elapsedMs; modelStartToRenderedMs=[Math]::Round($age)
            visibleMs=[Math]::Round($duration); markerGoldPixelIncrease=$markerDifference
            captionGoldPixelIncrease=$captionDifference
            inputTokens=$model.details.result.inputTokens; outputTokens=$model.details.result.outputTokens
            sourceArtifact=[IO.Path]::GetFileName($sourceFile); screenArtifact=[IO.Path]::GetFileName($screenFile)
            screenSha256=(Get-FileHash -LiteralPath $screenFile -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    } finally { $source.Dispose(); $image.Dispose() }
}
if ($proofs.Count -lt $MinimumCompleteChains) { throw "Not enough complete Luna model -> original-frame validation -> client receive -> rendered caption+marker pixels -> expiry chains. E2E NOT passed." }
$report = [ordered]@{
    checkedAt=[DateTimeOffset]::UtcNow.ToString("o")
    result="passed-local-bot-model-caption-and-calibrated-minimap-target"
    coachProcess=$coachProcess; gameProcess=$gameProcess
    completedChains=$proofs.Count; proofs=$proofs
    limits=@("same physical machine, separate clients","controlled level/equipment fixture, not natural GPM",
        "Foundry cloud model, not local 3090 Ti inference","1280x720 default left minimap calibration only",
        "conditional next target, not a validated full navigation path or highest-profit proof")
}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $run "e2e-report.json") -Encoding UTF8
$report | ConvertTo-Json -Depth 12
