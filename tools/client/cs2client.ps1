<#
CS2 client test harness - the user's own CS2 client with no window on their screen, no sound and no focus. README.md is the manual.

  pwsh -NoProfile -File tools\client\cs2client.ps1 <verb> [options]      (the server's side from the private config: deploy\README.md)

  start    [-Connect default|<ip:port>] [-Cmd "<console commands; separated by ;>"] [-Purpose "<what>"] [-WaitFor menu|ingame|window|none]
           [-HudLocal [-HudSource <dir> | -HudAddon <name>] [-IncludeThumbs]] [-Insecure [-Console]] [-Resolution 1920x1080|native] [-LeaseMinutes 15]
           [-MaxMinutes 60] [-AllowServerLock]          -HudLocal implies -Insecure; an insecure session joins only a server without VAC
  check    [-Insecure]   the VAC-safety self-check alone (start always runs it)
  cmd      -Cmd "<console command>" [-WaitMs 1500]   insecure sessions started with -Console only: run it, print its output
  wait     [-WaitFor menu|ingame] | -Pattern <regex> [-FromLine <n>] [-TimeoutSec 60]
  shot     [-Name <file>] [-MaxWidth 1920 (0 = full size)] [-OutDir <dir>]
  log      [-Tail 40] [-Pattern <regex>] [-FromLine <n>]
  status   the session, the window, the guard's audio and focus figures, the server lock
  quiet    proof of quiet right now: input desktop, the user's foreground, cs2 windows on their desktop, cs2 audio sessions
  stop     [-Reason "<why>"]   quit cs2 (WM_CLOSE: a normal quit), then restore settings, remove loose HUD files, free the lock
  server   what answers on the server's address (the containers publishing its port, by the Docker API; RCON status) and the server lock
  rcon     -Rcon "<server console command>"   (a production action: anything that changes the server needs the server lock)
  cleanup  finish a session whose watchdog died; remove leftover boot cfg / loose HUD files

Exit codes: 0 ok, 1 error, 2 refused (CS2 or the harness busy, server lock held), 3 timed out, 4 the Steam overlay over the game (the frame not written).
#>
param(
    [Parameter(Position = 0)][string]$Verb = 'help',
    [string]$Connect,
    [string]$Cmd,
    [string]$Purpose = 'client test',
    [switch]$AllowServerLock,
    [int]$LeaseMinutes = 15,
    [int]$MaxMinutes = 60,
    [ValidateSet('none', 'window', 'menu', 'ingame')][string]$WaitFor = 'menu',
    [int]$TimeoutSec = 0,
    [string]$Resolution,
    [string]$ExtraArgs,
    [switch]$HudLocal,
    [string]$HudSource,
    [string]$HudAddon,
    [switch]$IncludeThumbs,
    [string]$Name,
    [int]$MaxWidth = 1920,
    [string]$OutDir,
    [int]$Tail = 40,
    [string]$Pattern,
    [int]$FromLine = 0,
    [string]$Reason = 'stop requested',
    [string]$Rcon,
    [switch]$Insecure,
    [switch]$Console,
    [int]$WaitMs = 1500,
    [string]$StateDir
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Cs2Client.psm1') -Force
if ($StateDir) { Set-Cs2StateDir $StateDir }

try {
    switch ($Verb) {
        'start' {
            $p = @{ Connect = $Connect; Purpose = $Purpose; AllowServerLock = $AllowServerLock; LeaseMinutes = $LeaseMinutes; MaxMinutes = $MaxMinutes
                WaitFor = $(if ($PSBoundParameters.ContainsKey('WaitFor')) { $WaitFor } elseif ($Connect) { 'ingame' } else { 'menu' })
                TimeoutSec = $(if ($TimeoutSec) { $TimeoutSec } else { 240 }); HudLocal = $HudLocal; HudSource = $HudSource; HudAddon = $HudAddon; IncludeThumbs = $IncludeThumbs
                Insecure = $Insecure; Console = $Console }
            if ($Resolution) { $p.Resolution = $Resolution }
            if ($Cmd) { $p.Commands = @($Cmd) }
            if ($ExtraArgs) { $p.ExtraArgs = @($ExtraArgs -split '\s+' | Where-Object { $_ }) }
            Start-Cs2Client @p
        }
        'wait' {
            $t = if ($TimeoutSec) { $TimeoutSec } else { 60 }
            if ($Pattern) { Wait-Cs2Log $Pattern $t $FromLine } else { Wait-Cs2Ready $WaitFor $t }
        }
        'shot' { Save-Cs2Screenshot $Name $MaxWidth $OutDir }
        'cmd' { if (-not $Cmd) { throw '-Cmd "<console command>" is required' }; Invoke-Cs2Console $Cmd $WaitMs }
        'check' {
            Import-Cs2Native
            $r = Test-Cs2Integrity (Get-Cs2Env) @('-steam') -Insecure:$Insecure
            "Steam build $($r.BuildId); $(if ($Insecure) { 'as an insecure session' } else { 'as a secure (plain) session' })"
            foreach ($w in $r.Warnings) { "warning: $w" }
            if ($r.Violations.Count) { 'REFUSED:'; $r.Violations | ForEach-Object { "  - $_" }; exit 2 } else { 'OK: nothing that could put the account at risk' }
        }
        'log' { Get-Cs2Log $Tail $Pattern $FromLine }
        'status' { Get-Cs2ClientStatus }
        'quiet' {
            Import-Cs2Native
            $s = Read-Cs2Session
            "input desktop: $([Cs2Harness.Native]::InputDesktopName())"
            $fg = [Cs2Harness.Native]::ForegroundPid()
            "user's foreground: pid $fg ($((Get-Process -Id $fg -ErrorAction SilentlyContinue).ProcessName))"
            if ($s -and $s.state -eq 'running') {
                "foreground as seen from desktop $($s.desktop): pid $([Cs2Harness.Native]::ForegroundPidSeenFromDesktop($s.desktop)) (0 = nothing has focus there)"
                $gp = Get-Process -Id $s.cs2Pid -ErrorAction SilentlyContinue
                if ($gp) { "cs2 priority: $($gp.PriorityClass)" }
                $own = [Cs2Harness.Native]::WindowsOfPidOnOwnDesktop([uint32]$s.cs2Pid)
                "cs2 windows on the user's desktop: $($own.Count)"
                $hid = @([Cs2Harness.Native]::WindowsOnDesktop($s.desktop) | Where-Object { $_.Pid -eq [uint32]$s.cs2Pid -and $_.Visible })
                "cs2 visible windows on $($s.desktop): $($hid.Count) - $(($hid | ForEach-Object { "[$($_.Class)] $($_.Width)x$($_.Height)" }) -join ', ')"
                foreach ($a in [Cs2Harness.Native]::AudioSessions([uint32]$s.cs2Pid)) { "audio: $($a.ToString())" }
                $g = Read-Json (Join-Path $s.runDir 'guard.json')
                if ($g) { "guard so far: $($g.samples) audio samples, max peak $($g.maxPeak); user's foreground sampled $($g.fgSamples) times, cs2 $($g.fgOurs)" }
            }
            else { 'no running session' }
        }
        'stop' { Stop-Cs2Client $Reason }
        'server' {
            $lock = Get-Cs2ServerLock
            if ($lock) { "server lock: HELD`n$lock" } else { 'server lock: free' }
            $id = Get-Cs2ServerIdentity
            "containers publishing the server's port (and the game container $((Get-Cs2Config).game_container)):"
            foreach ($c in @($id.Containers)) { "  $($c.name)  $($c.state)  $($c.status)  image $($c.image)  ports $($c.ports -join ',')$(if ($c.test) { '  TEST COPY' })" }
            'rcon status:'
            ($id.RconStatus -split "`n" | Where-Object { $_ -match '\S' } | Select-Object -First 25 | ForEach-Object { "  $_" })
        }
        'rcon' { if (-not $Rcon) { throw '-Rcon "<command>" is required' }; Invoke-Cs2Rcon @($Rcon) }
        'cleanup' {
            $s = Read-Cs2Session
            if ($s -and $s.state -ne 'done' -and (Test-ProcessAlive $s.cs2Pid 'cs2' $s.cs2StartedUnix)) { throw "session $($s.id) is still running - use 'stop'" }
            $r = @(Invoke-Cs2Leftovers (Get-Cs2Env))
            if ($r.Count) { $r } else { 'nothing to clean up' }
        }
        default { ((Get-Content -LiteralPath $PSCommandPath -TotalCount 22) | Select-Object -Skip 1) -join "`n" }
    }
    exit 0
}
catch {
    $m = $_.Exception.Message
    Write-Output "ERROR: $m"
    if ($m -match 'already running|in use|server lock is held') { exit 2 }
    if ($m -match 'timed out') { exit 3 }
    if ($m -match 'the Steam overlay is over the game') { exit 4 }
    exit 1
}
