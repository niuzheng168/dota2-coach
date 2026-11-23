$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$data = 'Z:\dota2coach\data'
$root = Join-Path $data 'daily-study'
$mutex = [System.Threading.Mutex]::new($false, 'Local\DotaCoach-Daily-Replay-Study-v1')
$locked = $false
$rc = 1
try {
    try { $locked = $mutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { exit 0 }
    if (-not (Test-Path -LiteralPath 'Z:\' -PathType Container)) { throw 'Required Z: share is unavailable; no fallback.' }
    $day = [DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(8)).ToString('yyyy-MM-dd')
    $logs = [System.IO.Directory]::CreateDirectory((Join-Path $root "runs\$day")).FullName
    $log = Join-Path $logs 'launcher.log'
    $env:TEMP = [System.IO.Directory]::CreateDirectory((Join-Path $root 'tmp')).FullName
    $env:TMP = $env:TEMP
    $env:TMPDIR = $env:TEMP
    $env:PYTHONDONTWRITEBYTECODE = '1'
    $env:NODE_DISABLE_COMPILE_CACHE = '1'
    $env:DOTA_COACH_REPLAY_WORK_ROOT = $data
    if (-not $env:CODEY_MODEL_API_KEY) {
        $env:CODEY_MODEL_API_KEY = [Environment]::GetEnvironmentVariable('CODEY_MODEL_API_KEY', 'User')
    }
    $node = Join-Path $repo '.tmp\postgame-replay-v2\node\node.exe'
    if (-not (Test-Path -LiteralPath $node -PathType Leaf)) { throw 'Trusted local Node runtime not found.' }
    $py = Join-Path $repo '.tmp\replay-tools\Scripts\python.exe'
    if (-not (Test-Path -LiteralPath $py -PathType Leaf)) { throw 'Trusted local replay parser not found.' }
    Add-Content -LiteralPath $log -Value ("{0:o} started" -f [DateTimeOffset]::Now) -Encoding UTF8
    Set-Location -LiteralPath $repo
    # Native stderr is not a terminating PowerShell ErrorRecord. Node also writes structured status on Z:.
    $ErrorActionPreference = 'Continue'
    & $node --max-old-space-size=4096 (Join-Path $repo 'scripts\replays\daily-run.ts') 2>&1 |
        ForEach-Object { Add-Content -LiteralPath $log -Value $_.ToString() -Encoding UTF8 }
    $rc = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    Add-Content -LiteralPath $log -Value ("{0:o} exit={1}" -f [DateTimeOffset]::Now, $rc) -Encoding UTF8
} catch {
    if ($log) { Add-Content -LiteralPath $log -Value ("{0:o} startup failure: {1}" -f [DateTimeOffset]::Now, $_.Exception.Message) -Encoding UTF8 }
    Write-Error $_ -ErrorAction Continue
} finally {
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
exit $rc
