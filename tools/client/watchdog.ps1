# Guards one CS2 client session for its whole life (README.md, "The watchdog"). Started detached by Start-Cs2Client: no window, no
# console, nothing inherited from the caller. Ends when the session's cs2 has exited and the clean-up has run.
#
#   every 100 ms   the cs2 process's audio sessions: any peak above 0.0005 -> kill OUR cs2 at once (AUDIBLE)
#   every second   the user's foreground and their desktop's windows: a cs2 window there, or cs2 in front -> kill at once; a hook
#                  attempt in Discord's discord_hook.log -> kill at once
#   every 2 s      another cs2.exe (the user's), a launch of app 730 in Steam's console log, "logged in elsewhere" in the game's
#                  log -> yield: quit ours (WM_CLOSE, then terminate after 20 s), never touching the other one;
#                  the lease (no harness command for N minutes) or the hard limit -> quit ours
#   on exit        Invoke-Cs2PostExit: console log copied, boot cfg and loose HUD files removed, settings restored, client lock freed
param([Parameter(Mandatory)][string]$StateDir)

$ErrorActionPreference = 'Continue'
Import-Module (Join-Path $PSScriptRoot 'Cs2Client.psm1') -Force
Set-Cs2StateDir $StateDir
Import-Cs2Native
$Env = Get-Cs2Env
$s = Read-Cs2Session
if (-not $s -or $s.state -ne 'running') { exit 0 }
$sessionId = $s.id
$cs2Pid = [uint32]$s.cs2Pid
$logFile = Join-Path $s.runDir 'watchdog.log'
function W([string]$m) { Add-Content -LiteralPath $logFile -Value ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $m) }

$events = [Collections.Generic.List[string]]::new()
$stats = @{ samples = 0; maxPeak = 0.0; sessionsSeen = 0; fgSamples = 0; fgOurs = 0 }
$steamPos = [long]$s.steamLogPos
$gamePos = [long]0
# Discord's overlay hooks games it sees (discord_hook.log: "PollAttach", "Hook Helper Process Injected"; an attempt on a visible CS2
# was seen to fail with code 113). It has never been seen trying a hidden-desktop session (it reports the game as visibleGame=null);
# if it ever does, ours ends at once - nothing may hook a cs2 this harness runs.
$discordHook = Join-Path $env:APPDATA 'discord\logs\discord_hook.log'
$discordPos = if (Test-Path -LiteralPath $discordHook) { (Get-Item -LiteralPath $discordHook).Length } else { [long]0 }
$reason = $null
$killed = $false

function Save-Guard([string]$state) {
    Write-Json (Join-Path $s.runDir 'guard.json') ([ordered]@{
            state = $state; at = (Get-Date).ToString('HH:mm:ss'); samples = $stats.samples; maxPeak = ('{0:0.000000}' -f $stats.maxPeak)
            audioSessions = $stats.sessionsSeen; fgSamples = $stats.fgSamples; fgOurs = $stats.fgOurs; events = @($events)
        })
}

function Kill-Ours([string]$why) {
    $script:events.Add("$(Get-Date -Format HH:mm:ss) $why - cs2 terminated")
    W "KILL: $why"
    Write-Json (Join-Path $s.runDir 'stop.json') @{ reason = "watchdog: $why"; at = (Get-Date).ToString('s'); by = $PID }
    Stop-Process -Id $cs2Pid -Force -ErrorAction SilentlyContinue
    $script:killed = $true
}

function Yield-Ours([string]$why) {
    $script:events.Add("$(Get-Date -Format HH:mm:ss) $why - quitting")
    W "STOP: $why"
    W ("  " + (Stop-Cs2Process $s "watchdog: $why" 20))
    $script:reason = "watchdog: $why"
}

# New text appended to a log since $pos (the file is shared with its writer). Returns the text and moves $pos.
function Read-NewText([string]$path, [ref]$pos, [int]$max = 4MB) {
    if (-not (Test-Path -LiteralPath $path)) { return '' }
    $len = (Get-Item -LiteralPath $path).Length
    if ($len -lt $pos.Value) { $pos.Value = 0 }
    if ($len -eq $pos.Value) { return '' }
    $start = [Math]::Max($pos.Value, $len - $max)
    $fs = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        [void]$fs.Seek($start, [IO.SeekOrigin]::Begin)
        $buf = New-Object byte[] ($len - $start)
        $n = $fs.Read($buf, 0, $buf.Length)
    }
    finally { $fs.Close() }
    $pos.Value = $start + $n
    [Text.Encoding]::UTF8.GetString($buf, 0, $n)
}

W "watching cs2 pid $cs2Pid (session $sessionId, '$($s.purpose)'); input desktop: $([Cs2Harness.Native]::InputDesktopName())"
# The game dies with this process, however this process dies: it can never run on unguarded.
$tie = [Cs2Harness.Native]::TieToThisProcess($cs2Pid)
W "job tie (kill cs2 if the watchdog dies): $tie"
if ($tie -ne 'ok') { $events.Add("job tie failed: $tie") }
Save-Guard 'watching'
$tick = 0
while ($true) {
    if (-not (Test-ProcessAlive $cs2Pid 'cs2' $s.cs2StartedUnix)) { W 'cs2 has exited'; break }

    # Sound: the peak meter of every session the process has, ten times a second.
    try {
        $sessions = [Cs2Harness.Native]::AudioSessions($cs2Pid)
        if ($sessions.Count -gt $stats.sessionsSeen) { $stats.sessionsSeen = $sessions.Count }
        foreach ($a in $sessions) {
            $stats.samples++
            if ($a.Peak -gt $stats.maxPeak) { $stats.maxPeak = [double]$a.Peak }
            if ($a.Peak -gt 0.0005) { Kill-Ours "AUDIBLE ($($a.ToString()))"; break }
        }
    }
    catch { if ($tick % 100 -eq 0) { W "audio read failed: $($_.Exception.Message)" } }
    if ($killed) { Start-Sleep -Milliseconds 500; continue }

    if ($tick % 10 -eq 0) {
        # Focus and visibility, as the user sees them.
        $fg = [Cs2Harness.Native]::ForegroundPid()
        $stats.fgSamples++
        if ($fg -eq $cs2Pid) { $stats.fgOurs++; Kill-Ours 'cs2 took the user''s foreground'; continue }
        $own = [Cs2Harness.Native]::WindowsOfPidOnOwnDesktop($cs2Pid)
        if ($own.Count -gt 0) { Kill-Ours "a cs2 window is on the user's desktop: $($own[0].ToString())"; continue }
        if (($h = Read-NewText $discordHook ([ref]$discordPos)) -and $h -match '(?m)^.*(PollAttach|Hook Helper Process Injected|FinishAttach, success = true).*$') {
            Kill-Ours "Discord's overlay tried to hook a game process: $($Matches[0].Trim())"; continue
        }
    }

    if ($tick % 20 -eq 0 -and -not $reason) {
        # Below-normal priority, kept: the game may raise its own.
        try { $gp = Get-Process -Id $cs2Pid -ErrorAction Stop; if ($gp.PriorityClass -ne 'BelowNormal') { $old = $gp.PriorityClass; $gp.PriorityClass = 'BelowNormal'; W "priority $old -> BelowNormal" } } catch { }
        $cur = Read-Cs2Session
        if (-not $cur -or $cur.id -ne $sessionId -or $cur.state -eq 'done') { W 'session ended elsewhere'; exit 0 }
        $other = @(Get-Process cs2 -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $cs2Pid })
        if ($other.Count) { Yield-Ours "another cs2.exe started (pid $($other.Id -join ', ')) - the user's game; it is left alone" }
        elseif (($t = Read-NewText $Env.SteamConsoleLog ([ref]$steamPos)) -and $t -match '(?m)^.*(GameAction \[AppID 730\b|ExecCommandLine:.*(applaunch\s+730\b|rungameid/730\b|run/730\b)).*$') {
            Yield-Ours "Steam was asked to launch CS2: $($Matches[0].Trim())"
        }
        elseif (($g = Read-NewText $Env.LogPath ([ref]$gamePos)) -and $g -match '(?mi)^.*(logged in elsewhere|LoggedInElsewhere|STEAM_LOGON|STEAM_INUSE|NoSteamLogin).*$') {
            Yield-Ours "the account is in use elsewhere: $($Matches[0].Trim())"
        }
        else {
            $now = Get-UnixNow
            $lease = Read-Json (Join-Path $s.runDir 'lease.json')
            if ($lease -and $now -gt [double]$lease.leaseUntil) { Yield-Ours "lease expired (no harness command since $(Format-Unix ([double]$lease.leaseUntil - 60 * [int]$s.leaseMinutes)))" }
            elseif ($now -gt [double]$s.maxUntil) { Yield-Ours 'hard time limit reached' }
        }
    }

    if ($tick % 50 -eq 0) { Save-Guard 'watching' }
    Start-Sleep -Milliseconds 100
    $tick++
}

Save-Guard 'ended'
W ("guard totals: {0} audio samples over {1} session(s), max peak {2:0.000000}; user's foreground sampled {3} times, ours {4}" -f $stats.samples, $stats.sessionsSeen, $stats.maxPeak, $stats.fgSamples, $stats.fgOurs)
$s = Read-Cs2Session
if ($s -and $s.id -eq $sessionId -and $s.state -ne 'done') {
    Invoke-Cs2PostExit $s $(if ($reason) { $reason } else { 'cs2 exited' })
    $r = Read-Json (Join-Path $s.runDir 'result.json')
    if ($r) { W "clean-up done: $($r.reason); loose HUD: $($r.hudLocal); settings: $(@($r.settings | Where-Object { $_.state -ne 'unchanged' } | ForEach-Object { "$($_.file) $($_.state)" }) -join '; ')" }
}
