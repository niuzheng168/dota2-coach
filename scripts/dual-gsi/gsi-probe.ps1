#Requires -Version 5.1
<#
双机 GSI 对照采样器。X = 账号 A 打游戏的机器；Y = 账号 B 做 Plus 实时观战的机器。
只在 127.0.0.1 监听（不触发防火墙），只记录 Dota 发来的 GSI，不调用任何模型，不接入教练。
用法见 docs/dual-gsi-probe.md：
  powershell -ExecutionPolicy Bypass -File .\gsi-probe.ps1 -Role X
  powershell -ExecutionPolicy Bypass -File .\gsi-probe.ps1 -Role Y
  powershell -ExecutionPolicy Bypass -File .\gsi-probe.ps1 -Role X -Uninstall
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][ValidateSet('X', 'Y')][string]$Role,
  [string]$DotaPath = '',
  [int]$Port = 0,
  [int]$Minutes = 150,
  [string]$OutRoot = (Join-Path $env:USERPROFILE 'DotaGsiProbe'),
  [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$ProbeVersion = 1
if ($Port -le 0) { $Port = if ($Role -eq 'X') { 3291 } else { 3292 } }
$Components = @('provider', 'map', 'player', 'hero', 'abilities', 'items', 'buildings', 'league', 'draft',
  'wearables', 'events', 'minimap', 'roshan', 'couriers', 'neutralitems')
$NtpServers = @('ntp.aliyun.com', 'time.windows.com', 'cn.pool.ntp.org', 'time.cloudflare.com')
$Utf8 = New-Object System.Text.UTF8Encoding $false
$Latin1 = [System.Text.Encoding]::GetEncoding(28591)
$Invariant = [System.Globalization.CultureInfo]::InvariantCulture

# Shared with the background sidecar job, which cannot see functions of this script.
$NtpCode = {
  function ConvertFrom-NtpTime([byte[]]$Bytes, [int]$Offset) {
    [double]$sec = 0; [double]$frac = 0
    for ($i = 0; $i -lt 4; $i++) { $sec = $sec * 256 + $Bytes[$Offset + $i]; $frac = $frac * 256 + $Bytes[$Offset + 4 + $i] }
    return ([DateTime]::new(1900, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)).AddMilliseconds($sec * 1000 + $frac * 1000 / 4294967296)
  }
  # offsetMs = NTP server time - local clock; corrected time = local + offsetMs.
  function Get-NtpSample([string]$Server) {
    $udp = [System.Net.Sockets.UdpClient]::new()
    try {
      $udp.Client.ReceiveTimeout = 1500
      $udp.Connect($Server, 123)
      $request = New-Object byte[] 48; $request[0] = 0x1B
      $t1 = [DateTime]::UtcNow
      [void]$udp.Send($request, $request.Length)
      $remote = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)
      $response = $udp.Receive([ref]$remote)
      $t4 = [DateTime]::UtcNow
      if ($response.Length -lt 48) { throw 'short NTP response' }
      $t2 = ConvertFrom-NtpTime $response 32; $t3 = ConvertFrom-NtpTime $response 40
      $offset = (($t2 - $t1).TotalMilliseconds + ($t3 - $t4).TotalMilliseconds) / 2
      $rtt = ($t4 - $t1).TotalMilliseconds - ($t3 - $t2).TotalMilliseconds
      return [ordered]@{ server = $Server; localMs = ([DateTimeOffset]$t4).ToUnixTimeMilliseconds(); offsetMs = [math]::Round($offset, 1); rttMs = [math]::Round($rtt, 1) }
    } catch { return [ordered]@{ server = $Server; localMs = ([DateTimeOffset][DateTime]::UtcNow).ToUnixTimeMilliseconds(); error = $_.Exception.Message } }
    finally { $udp.Close() }
  }
  function Write-NtpRound([string]$Path, [string[]]$Servers, [string]$Phase) {
    $enc = New-Object System.Text.UTF8Encoding $false
    $results = @()
    foreach ($server in $Servers) {
      $sample = Get-NtpSample $server; $sample.phase = $Phase
      [System.IO.File]::AppendAllText($Path, ($sample | ConvertTo-Json -Compress) + "`n", $enc)
      $results += , $sample
    }
    return $results
  }
}
. $NtpCode

# Periodic NTP samples in a separate process so the HTTP receive timestamps are never delayed
# by them. (No screen capture here: use Steam's F12 screenshots on Y instead.)
$Sidecar = {
  param([string]$OutDir, [int]$NtpSec, [int]$ParentPid, [string[]]$Servers, [string]$NtpText)
  . ([scriptblock]::Create($NtpText))
  $log = Join-Path $OutDir 'sidecar.log'
  $nextNtp = [DateTime]::UtcNow.AddSeconds($NtpSec)
  while (-not (Test-Path -LiteralPath (Join-Path $OutDir 'STOP'))) {
    if (-not (Get-Process -Id $ParentPid -ErrorAction SilentlyContinue)) { break }
    $now = [DateTime]::UtcNow
    if ($now -ge $nextNtp) {
      $nextNtp = $now.AddSeconds($NtpSec)
      try { [void](Write-NtpRound (Join-Path $OutDir 'ntp.jsonl') $Servers 'periodic') } catch { Add-Content -LiteralPath $log -Value "ntp: $($_.Exception.Message)" }
    }
    Start-Sleep -Milliseconds 200
  }
}

function Find-DotaGameDir {
  $roots = @()
  if ($DotaPath) { $roots += $DotaPath }
  else {
    $steam = $null
    try { $steam = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath } catch {}
    if ($steam) {
      $libraries = @($steam)
      $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
      if (Test-Path -LiteralPath $vdf) {
        foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) { $libraries += ($m.Groups[1].Value -replace '\\\\', '\') }
      }
      foreach ($library in $libraries) { $roots += (Join-Path $library 'steamapps\common\dota 2 beta') }
    }
  }
  foreach ($root in $roots) {
    foreach ($candidate in @((Join-Path $root 'game\dota'), $root)) {
      if (Test-Path -LiteralPath (Join-Path $candidate 'cfg')) { return (Resolve-Path -LiteralPath $candidate).Path }
    }
  }
  return $null
}

function New-Token {
  $bytes = New-Object byte[] 24
  [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
  return (($bytes | ForEach-Object { $_.ToString('x2') }) -join '')
}

function Read-GsiBody([System.Net.Sockets.TcpClient]$Client) {
  $Client.ReceiveTimeout = 3000
  $stream = $Client.GetStream()
  $buffer = New-Object byte[] 65536
  $data = New-Object System.IO.MemoryStream
  $headerEnd = -1; $length = -1
  while ($true) {
    $n = $stream.Read($buffer, 0, $buffer.Length)
    if ($n -le 0) { break }
    $data.Write($buffer, 0, $n)
    if ($headerEnd -lt 0) {
      $text = $Latin1.GetString($data.GetBuffer(), 0, [int]$data.Length)
      $headerEnd = $text.IndexOf("`r`n`r`n")
      if ($headerEnd -ge 0) {
        $m = [regex]::Match($text.Substring(0, $headerEnd), '(?im)^Content-Length:\s*(\d+)')
        if (-not $m.Success) { throw 'request has no Content-Length' }
        $length = [int]$m.Groups[1].Value
      } elseif ($data.Length -gt 65536) { throw 'request header too large' }
    }
    if ($headerEnd -ge 0 -and $data.Length -ge $headerEnd + 4 + $length) { break }
    if ($data.Length -gt 16MB) { throw 'request too large' }
  }
  if ($headerEnd -lt 0 -or $data.Length -lt $headerEnd + 4 + $length) { throw 'incomplete request' }
  return $Utf8.GetString($data.GetBuffer(), $headerEnd + 4, $length)
}

function Send-Status([System.Net.Sockets.TcpClient]$Client, [int]$Code) {
  $reason = if ($Code -eq 200) { 'OK' } else { 'Unauthorized' }
  $bytes = $Latin1.GetBytes("HTTP/1.1 $Code $reason`r`nContent-Length: 0`r`nConnection: close`r`n`r`n")
  $stream = $Client.GetStream(); $stream.Write($bytes, 0, $bytes.Length); $stream.Flush()
}

# Keeps the payload verbatim except the auth token, Steam IDs and chat text.
function Protect-Body([string]$Body) {
  $b = $Body -replace '[\r\n]+', ''
  $b = [regex]::Replace($b, '("token"\s*:\s*)"[^"]*"', '$1"[removed]"')
  $b = [regex]::Replace($b, '("(?:steamid|accountid)"\s*:\s*)"[^"]*"', '$1"[redacted]"')
  $b = [regex]::Replace($b, '("message"\s*:\s*)"(?:[^"\\]|\\.)*"', '$1"[redacted]"')
  $t = $b.Trim()
  if ($t.StartsWith('{') -and $t.EndsWith('}')) { return $t }
  return ($b | ConvertTo-Json -Compress)
}

function Format-Clock($Seconds) {
  if ($null -eq $Seconds) { return '-' }
  $s = [int]$Seconds; $sign = if ($s -lt 0) { '-' } else { '' }; $a = [math]::Abs($s)
  return ('{0}{1}:{2:00}' -f $sign, [math]::Floor($a / 60), ($a % 60))
}

function Get-Names($Object) { if ($null -eq $Object) { return @() } return @($Object.PSObject.Properties | ForEach-Object { $_.Name }) }

function Show-Status {
  $line = '[{0} {1}] 包 {2}' -f $Role, (Get-Date -Format 'HH:mm:ss'), $script:Seq
  if ($script:Rejected -gt 0) { $line += "  拒绝 $script:Rejected" }
  if ($script:Errors -gt 0) { $line += "  错误 $script:Errors" }
  if (-not $script:LastBody) {
    Write-Host ($line + ' | 还没收到 Dota 数据：确认 Dota 带 -gamestateintegration，并且是在本窗口启动之后才启动的') -ForegroundColor Yellow
    return
  }
  try {
    $j = $script:LastBody | ConvertFrom-Json
    $state = if ($j.map) { ([string]$j.map.game_state) -replace '^DOTA_GAMERULES_STATE_', '' } else { '菜单' }
    $groups = @(Get-Names $j.player | Where-Object { $_ -match '^team\d+$' }).Count
    $hero = if ($j.hero -and $j.hero.name) { $j.hero.name } elseif ($groups -gt 0) { '(10人分组)' } else { '(空)' }
    $items = @(Get-Names $j.items | Where-Object { $_ -match '^(slot|stash|teleport|neutral)\d+$' -and $j.items.$_.name -and $j.items.$_.name -ne 'empty' }).Count
    $abilities = @(Get-Names $j.abilities | Where-Object { $_ -match '^ability\d+$' }).Count
    $minimap = @(Get-Names $j.minimap).Count
    $line += ' | {0} {1} | 身份 {2} | 英雄 {3} 装备 {4} 技能 {5} | 小地图 {6} | 10人分组 {7}' -f `
      $state, (Format-Clock $j.map.clock_time), $j.player.team_name, $hero, $items, $abilities, $minimap, $(if ($groups -gt 0) { '有' } else { '无' })
    Write-Host $line
  } catch { Write-Host ($line + ' | 最新包无法解析') -ForegroundColor Yellow }
}

# ---------------------------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null
$statePath = Join-Path $OutRoot "state-$Role.json"
$saved = $null
if (Test-Path -LiteralPath $statePath) { try { $saved = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json } catch { $saved = $null } }

if ($Uninstall) {
  if ($saved -and $saved.cfgPath -and (Test-Path -LiteralPath $saved.cfgPath)) {
    Remove-Item -LiteralPath $saved.cfgPath; Write-Host "已删除 GSI 配置：$($saved.cfgPath)"
  } else { Write-Host '没有找到本工具安装的 GSI 配置。' }
  if (Test-Path -LiteralPath $statePath) { Remove-Item -LiteralPath $statePath }
  Write-Host '采样记录保留在' $OutRoot '，Steam 启动选项里的 -gamestateintegration 可以保留。'
  return
}

$game = Find-DotaGameDir
if (-not $game) { throw '找不到 Dota 2 安装目录。请加参数 -DotaPath "D:\SteamLibrary\steamapps\common\dota 2 beta"' }
$token = if ($saved -and $saved.token -and $saved.port -eq $Port) { [string]$saved.token } else { New-Token }
$cfgDir = Join-Path $game 'cfg\gamestate_integration'
$cfgPath = Join-Path $cfgDir ("gamestate_integration_dual_probe_{0}.cfg" -f $Role.ToLower())
$dataLines = ($Components | ForEach-Object { "    `"$_`" `"1`"" }) -join "`r`n"
# throttle 0.2 / buffer 0: about 5 packets per second, enough to time clock transitions.
$cfg = "`"Dota GSI Dual Probe $Role`"`r`n{`r`n  `"uri`" `"http://127.0.0.1:$Port/`"`r`n  `"timeout`" `"2.0`"`r`n  `"buffer`" `"0.0`"`r`n" +
  "  `"throttle`" `"0.2`"`r`n  `"heartbeat`" `"5.0`"`r`n  `"auth`"`r`n  {`r`n    `"token`" `"$token`"`r`n  }`r`n  `"data`"`r`n  {`r`n" +
  "$dataLines`r`n  }`r`n}`r`n"
$cfgChanged = -not ((Test-Path -LiteralPath $cfgPath) -and ([System.IO.File]::ReadAllText($cfgPath) -eq $cfg))
if ($cfgChanged) { New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null; [System.IO.File]::WriteAllText($cfgPath, $cfg, $Utf8) }
[System.IO.File]::WriteAllText($statePath, ([ordered]@{ role = $Role; port = $Port; token = $token; cfgPath = $cfgPath } | ConvertTo-Json), $Utf8)

$outDir = Join-Path $OutRoot ('{0}-{1}' -f $Role, (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$gsiPath = Join-Path $outDir 'gsi.jsonl'; $ntpPath = Join-Path $outDir 'ntp.jsonl'; $stopFile = Join-Path $outDir 'STOP'

Write-Host ''
Write-Host "=== 双机 GSI 对照采样器 v$ProbeVersion  角色 $Role（$(if ($Role -eq 'X') { '打游戏的机器' } else { 'Plus 观战机器' })）===" -ForegroundColor Cyan
Write-Host "Dota 目录：$game"
Write-Host "GSI 配置：$cfgPath $(if ($cfgChanged) { '(新写入)' } else { '(已存在，未改动)' })"
Write-Host "记录目录：$outDir"

$dotaLaunch = 'not-running'
$cfgTime = (Get-Item -LiteralPath $cfgPath).LastWriteTime
foreach ($p in @(Get-CimInstance -ClassName Win32_Process -Filter "Name='dota2.exe'" -ErrorAction SilentlyContinue)) {
  $commandLine = [string]$p.CommandLine
  if (-not $commandLine) { $dotaLaunch = 'unknown'; Write-Host '检测到 Dota 正在运行，但读不到它的启动参数（可能以管理员身份运行）。' -ForegroundColor Yellow }
  elseif ($commandLine -notmatch '-gamestateintegration') {
    $dotaLaunch = 'missing-flag'
    Write-Host 'Dota 正在运行，但启动参数里没有 -gamestateintegration，收不到数据。请退出 Dota，在 Steam 启动选项里加上后重新启动。' -ForegroundColor Red
  } elseif ($p.CreationDate -lt $cfgTime) {
    $dotaLaunch = 'started-before-config'
    Write-Host 'Dota 是在这份配置写入之前启动的，不会加载它。请完全退出 Dota，再从 Steam 启动。' -ForegroundColor Red
  } else { $dotaLaunch = 'ok'; Write-Host 'Dota 已带 -gamestateintegration 启动，并且晚于配置写入。' -ForegroundColor Green }
}
if ($dotaLaunch -eq 'not-running') { Write-Host '现在可以从 Steam 启动 Dota（启动选项需包含 -gamestateintegration）。' -ForegroundColor Green }

Write-Host '测量本机时钟偏差（NTP）...'
$startNtp = Write-NtpRound $ntpPath $NtpServers 'start'
$okNtp = @($startNtp | Where-Object { $_.Contains('offsetMs') })
if ($okNtp.Count -gt 0) { Write-Host ('时钟偏差：' + (($okNtp | ForEach-Object { '{0} {1:+0;-0}ms(往返 {2:0}ms)' -f $_.server, $_.offsetMs, $_.rttMs }) -join '  ')) }
else { Write-Host '连不上任何 NTP 服务器：仍会记录，但两台机器之间的延迟无法校准。' -ForegroundColor Yellow }

$session = [ordered]@{
  probeVersion = $ProbeVersion; role = $Role; machine = $env:COMPUTERNAME; port = $Port; cfgPath = $cfgPath
  throttle = 0.2; buffer = 0.0; components = $Components; dotaLaunch = $dotaLaunch
  startedMs = ([DateTimeOffset][DateTime]::UtcNow).ToUnixTimeMilliseconds(); paidModelRequests = 0; forwardsToCoach = $false
}
[System.IO.File]::WriteAllText((Join-Path $outDir 'session.json'), ($session | ConvertTo-Json), $Utf8)

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
try { $listener.Start() } catch { throw "端口 $Port 被占用。换一个端口：加参数 -Port 3299（会重写配置，需要重启 Dota）" }
$ntpText = $NtpCode.ToString()
$job = Start-Job -ScriptBlock $Sidecar -ArgumentList $outDir, 300, $PID, $NtpServers, $ntpText
$writer = New-Object System.IO.StreamWriter($gsiPath, $true, $Utf8)
$writer.AutoFlush = $true
$monotonic = [System.Diagnostics.Stopwatch]::StartNew()
$script:Seq = 0; $script:Rejected = 0; $script:Errors = 0; $script:LastBody = $null
$deadline = [DateTime]::UtcNow.AddMinutes($Minutes)
$nextStatus = [DateTime]::UtcNow
Write-Host "开始监听 127.0.0.1:$Port，最长 $Minutes 分钟。结束时按 Ctrl+C（不要直接关窗口，否则不会压缩和收尾）。" -ForegroundColor Cyan
if ($Role -eq 'Y') { Write-Host '观战时每做一项界面检查，按 F12 存一张 Steam 截图（文件名带时间，事后和数据对齐）。' }

$accept = $listener.AcceptTcpClientAsync()
try {
  while ($true) {
    if ($accept.Wait(200)) {
      $received = [DateTime]::UtcNow
      $mono = $monotonic.Elapsed.TotalMilliseconds
      $client = $accept.Result
      $accept = $listener.AcceptTcpClientAsync()
      try {
        $body = Read-GsiBody $client
        $auth = [regex]::Match($body, '"token"\s*:\s*"([^"]*)"')
        if (-not $auth.Success -or $auth.Groups[1].Value -ne $token) { $script:Rejected++; Send-Status $client 401 }
        else {
          Send-Status $client 200
          $clean = Protect-Body $body
          $script:Seq++
          $writer.WriteLine(('{{"seq":{0},"recvMs":{1},"monoMs":{2},"bytes":{3},"body":{4}}}' -f $script:Seq,
            ([DateTimeOffset]$received).ToUnixTimeMilliseconds(), $mono.ToString('0.0', $Invariant), $body.Length, $clean))
          $script:LastBody = $clean
        }
      } catch {
        $script:Errors++
        Add-Content -LiteralPath (Join-Path $outDir 'errors.log') -Value ('{0} {1}' -f (Get-Date -Format o), $_.Exception.Message)
      } finally { $client.Close() }
    }
    $now = [DateTime]::UtcNow
    if ($now -ge $nextStatus) { Show-Status; $nextStatus = $now.AddSeconds(5) }
    if ((Test-Path -LiteralPath $stopFile) -or $now -ge $deadline) { break }
  }
} finally {
  try { $listener.Stop() } catch {}
  $writer.Dispose()
  New-Item -ItemType File -Force -Path $stopFile | Out-Null
  if ($job) { [void](Wait-Job $job -Timeout 15); Receive-Job $job -ErrorAction SilentlyContinue | Out-Host; Remove-Job $job -Force }
  Write-Host '结束测量时钟偏差...'
  [void](Write-NtpRound $ntpPath $NtpServers 'end')
  $session.endedMs = ([DateTimeOffset][DateTime]::UtcNow).ToUnixTimeMilliseconds()
  $session.packets = $script:Seq; $session.rejected = $script:Rejected; $session.errors = $script:Errors
  [System.IO.File]::WriteAllText((Join-Path $outDir 'session.json'), ($session | ConvertTo-Json), $Utf8)
  if ((Test-Path -LiteralPath $gsiPath) -and (Get-Item -LiteralPath $gsiPath).Length -gt 0) {
    Write-Host '压缩记录（大文件可能要几十秒）...'
    $source = [System.IO.File]::OpenRead($gsiPath); $target = [System.IO.File]::Create("$gsiPath.gz")
    $gzip = New-Object System.IO.Compression.GZipStream($target, [System.IO.Compression.CompressionLevel]::Optimal)
    $source.CopyTo($gzip); $gzip.Dispose(); $target.Dispose(); $source.Dispose()
    Remove-Item -LiteralPath $gsiPath
  }
  Write-Host ''
  Write-Host "已停止。共 $script:Seq 包，记录在：$outDir" -ForegroundColor Green
  if ($Role -eq 'X') { Write-Host '请把整个文件夹拷到装有 dota2-coach 仓库的电脑。' }
  Write-Host 'GSI 配置仍保留（下次运行不用重启 Dota）。测试全部结束后运行同一命令加 -Uninstall 删除。'
}
