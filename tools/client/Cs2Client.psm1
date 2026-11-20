# CS2 client test harness: the CS2 client of whoever works at this PC, run with no window on their screen, no sound and no focus
# (README.md).
#
# How each of those is achieved (README.md, "Why it is quiet"):
#   - no window, no focus: cs2.exe is started directly (not through Steam) on a Win32 desktop of its own, WinSta0\Cs2Harness, that
#     is created and never switched to (Cs2Native.cs). It renders normally there; PrintWindow reads the frames.
#   - no sound: -nosound (soundsystem.dll checks it before mixing), snd_mute_losefocus (the window never has focus), and the
#     watchdog, which reads the session's peak meter ten times a second and kills OUR cs2 at the first audible sample.
#   - no lasting change to the user's settings: nothing archived is set; the config files CS2 rewrites anyway (cs2_machine_convars.vcfg
#     gains ui_playsettings_custom_preset on every start) are backed up before the launch and restored after the exit.
#
# Never touches a cs2.exe it did not start. Never launches Steam's -applaunch (a Steam dialog can want a click), Hammer or the
# Workshop Tools.

$script:Here = $PSScriptRoot
$script:RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
# The server half: the private config, the server lock, the Docker API and RCON - os2-deploy's probe (deploy\OverStrike2.Deploy\Client\Probe.cs).
# It holds every deployment detail - the address, the lock file, the containers - and reads the credentials from the environment; it never prints
# one. It runs from a build, never through dotnet run: os2-deploy's own docshots spawns this harness, and a build under a running os2-deploy would
# write over its own files.
$script:DeployProject = Join-Path $script:RepoRoot 'deploy\OverStrike2.Deploy'
$script:Deploy = $null

# The repository's main working tree: a worktree's .git file names <main>\.git\worktrees\<name>, so every worktree shares one state
# (os2-deploy's Harness.MainCheckout in Probe.cs, alike).
function Get-Cs2MainCheckout([string]$Root) {
    $dotgit = Join-Path $Root '.git'
    if (Test-Path -LiteralPath $dotgit -PathType Leaf) {
        $line = Get-Content -LiteralPath $dotgit -TotalCount 1
        if ($line -match '^gitdir:\s*(.+)$') {
            $gd = $Matches[1].Trim()
            if (-not [IO.Path]::IsPathRooted($gd)) { $gd = Join-Path $Root $gd }
            $common = Split-Path -Parent (Split-Path -Parent ([IO.Path]::GetFullPath($gd)))
            if ((Split-Path -Leaf $common) -eq '.git') { return (Split-Path -Parent $common) }
        }
    }
    $Root
}

$script:StateDir = if ($env:CS2CLIENT_STATE) { $env:CS2CLIENT_STATE } else { Join-Path (Get-Cs2MainCheckout $script:RepoRoot) '.work\cs2-client' }
$script:Desktop = 'Cs2Harness'
$script:LogName = 'cs2harness_console.log'          # -con_logfile: game\csgo\<this>, truncated at every start; the user's console.log is left alone
$script:BootCfg = 'cs2harness_boot.cfg'             # game\csgo\cfg\<this>, run with +exec, deleted when the session ends
$script:Config = $null

function Set-Cs2StateDir([string]$Path) { if ($Path) { $script:StateDir = $Path } }
function Get-Cs2StateDir { $script:StateDir }

# The os2-deploy build the server half runs from, as a command line: $env:OS2_DEPLOY when it is set (os2-deploy's docshots sets it to
# itself, a .exe or a .dll), else this checkout's newest build (bin\Release or bin\Debug), built anew with dotnet build -c Release when
# none is there or one of the program's sources is newer than it. A build that cannot be redone (one of its files in use) is run as it is.
function Get-Cs2Deploy {
    if ($script:Deploy) { return $script:Deploy }
    $set = $env:OS2_DEPLOY
    if ($set -and (Test-Path -LiteralPath $set)) {
        $script:Deploy = if ($set -like '*.dll') { @('dotnet', $set) } else { @($set) }
        return $script:Deploy
    }
    $builds = @('Release', 'Debug') | ForEach-Object { Join-Path $script:DeployProject "bin\$_\net10.0\os2-deploy.exe" } |
        Where-Object { Test-Path -LiteralPath $_ } | Sort-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } -Descending
    $newest = $null
    foreach ($dir in @($script:DeployProject, (Join-Path $script:RepoRoot 'core'))) {
        $f = Get-ChildItem -LiteralPath $dir -Recurse -File -Include '*.cs', '*.csproj' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($f -and (-not $newest -or $f.LastWriteTimeUtc -gt $newest)) { $newest = $f.LastWriteTimeUtc }
    }
    $stale = -not $builds -or ($newest -and (Get-Item -LiteralPath @($builds)[0]).LastWriteTimeUtc -lt $newest)
    if ($stale) {
        $null = & dotnet build $script:DeployProject -c Release --nologo -v q 2>&1
        $release = Join-Path $script:DeployProject 'bin\Release\net10.0\os2-deploy.exe'
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath $release)) { $builds = @($release) }
        elseif (-not $builds) { throw "os2-deploy did not build (dotnet build $script:DeployProject -c Release)" }
        else { Write-Warning "os2-deploy could not be built anew; running $(@($builds)[0]) as it is" }
    }
    $script:Deploy = @(@($builds)[0])
    $script:Deploy
}

# The server half, a process of its own (os2-deploy probe ...): its output as text, and whether it ended well.
function Invoke-Cs2Probe([string[]]$ProbeArgs) {
    $cmd = @(Get-Cs2Deploy)
    $rest = @($cmd | Select-Object -Skip 1) + @('probe') + $ProbeArgs
    $out = & $cmd[0] @rest 2>&1 | Out-String
    [pscustomobject]@{ Ok = ($LASTEXITCODE -eq 0); Text = ($out -replace "`0", '') }
}

# What the harness reads from the private config (os2-deploy probe config): the server's address, the lock file, the containers. Read once a
# process, and only by the verbs that need it - a shot or a log line never waits for it.
function Get-Cs2Config {
    if ($script:Config) { return $script:Config }
    $r = Invoke-Cs2Probe @('config')
    if (-not $r.Ok) { throw "the private config cannot be read (os2-deploy probe config): $($r.Text.Trim())" }
    $script:Config = $r.Text | ConvertFrom-Json
    $script:Config
}

function Get-UnixNow { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() / 1000.0 }
function Format-Unix([double]$t) { [DateTimeOffset]::FromUnixTimeMilliseconds([long]($t * 1000)).ToLocalTime().ToString('yyyy-MM-dd HH:mm:ss') }

function Write-Utf8([string]$Path, [string]$Text) {
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $tmp = "$Path.$PID.tmp"
    [IO.File]::WriteAllText($tmp, $Text, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $tmp -Destination $Path -Force
}

function Read-Json([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json) } catch { return $null }
}

function Write-Json([string]$Path, $Object) { Write-Utf8 $Path ($Object | ConvertTo-Json -Depth 8) }

# ---------------------------------------------------------------------------------------------------------------- environment

function Get-Cs2Env {
    $steamKey = Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop
    $steam = ($steamKey.SteamPath -replace '/', '\')
    $active = Get-ItemProperty 'HKCU:\Software\Valve\Steam\ActiveProcess' -ErrorAction SilentlyContinue
    $account = if ($active -and $active.ActiveUser) { [uint32]$active.ActiveUser } else { [uint32]0 }
    $roots = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path -LiteralPath $vdf) { foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"(.+?)"')) { $roots += $m.Groups[1].Value.Replace('\\', '\') } }
    $cs2 = $null
    foreach ($r in $roots) { $c = Join-Path $r 'steamapps\common\Counter-Strike Global Offensive'; if (Test-Path -LiteralPath (Join-Path $c 'game\bin\win64\cs2.exe')) { $cs2 = $c; break } }
    if (-not $cs2) { throw "Counter-Strike 2 not found in Steam's library folders" }
    [pscustomobject]@{
        SteamRoot       = $steam
        AccountId       = $account
        Cs2Root         = $cs2
        Exe             = Join-Path $cs2 'game\bin\win64\cs2.exe'
        GameDir         = Join-Path $cs2 'game\csgo'
        CfgDir          = Join-Path $cs2 'game\csgo\cfg'
        LogPath         = Join-Path $cs2 "game\csgo\$script:LogName"
        UserCfgDir      = Join-Path $steam "userdata\$account\730\local\cfg"
        RemoteDir       = Join-Path $steam "userdata\$account\730\remote"
        SteamConsoleLog = Join-Path $steam 'logs\console_log.txt'
        HudBuildDir     = Join-Path $cs2 'game\csgo_addons\gungame_hud'
    }
}

function Import-Cs2Native {
    if ('Cs2Harness.Native' -as [type]) { return }
    $src = Join-Path $script:Here 'Cs2Native.cs'
    $hash = (Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash.Substring(0, 12)
    $binDir = Join-Path $script:StateDir 'bin'
    $dll = Join-Path $binDir "Cs2Native-$hash-net$([Environment]::Version.Major).dll"
    if (-not (Test-Path -LiteralPath $dll)) {
        New-Item -ItemType Directory -Force -Path $binDir | Out-Null
        $tmp = Join-Path $binDir "Cs2Native-$hash-$PID.dll"
        $refs = @('System.Drawing.Common', 'System.Drawing.Primitives', 'Microsoft.Win32.Primitives', 'System.ComponentModel.Primitives',
            'System.Diagnostics.Process', 'System.Collections', 'System.Linq', 'System.Runtime.InteropServices', 'System.Threading.Thread',
            'System.Collections.NonGeneric')
        $extra = @('System.Private.Windows.GdiPlus', 'System.Private.Windows.Core') # .NET 9+: where System.Drawing's internals live
        try { Add-Type -TypeDefinition (Get-Content -LiteralPath $src -Raw) -OutputAssembly $tmp -OutputType Library -ReferencedAssemblies ($refs + $extra) -ErrorAction Stop }
        catch { Add-Type -TypeDefinition (Get-Content -LiteralPath $src -Raw) -OutputAssembly $tmp -OutputType Library -ReferencedAssemblies $refs -ErrorAction Stop }
        if (-not (Test-Path -LiteralPath $dll)) { Move-Item -LiteralPath $tmp -Destination $dll -Force } else { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    }
    Add-Type -Path $dll
}

# ---------------------------------------------------------------------------------------------------------------- processes

# Every cs2.exe: pid and start time only. Not WMI's Win32_Process: its CommandLine is read out of the target's memory, and nothing
# here reads cs2's memory (VAC safety) - not even the user's own game's.
function Get-Cs2Processes {
    @(Get-Process -Name cs2 -ErrorAction SilentlyContinue | ForEach-Object {
            $st = $null; try { $st = $_.StartTime } catch { }
            [pscustomobject]@{ Pid = [int]$_.Id; Started = $st }
        })
}

function Test-ProcessAlive([int]$ProcessId, [string]$Name, [double]$StartedUnix = 0) {
    if ($ProcessId -le 0) { return $false }
    $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if (-not $p -or $p.HasExited) { return $false }
    if ($Name -and $p.ProcessName -ne $Name) { return $false }
    if ($StartedUnix -gt 0) {
        try { $st = ([DateTimeOffset]$p.StartTime).ToUnixTimeMilliseconds() / 1000.0; if ([Math]::Abs($st - $StartedUnix) -gt 5) { return $false } } catch { }
    }
    return $true
}

function Get-SteamState {
    $steamRunning = [bool](Get-Process steam -ErrorAction SilentlyContinue)
    $active = Get-ItemProperty 'HKCU:\Software\Valve\Steam\ActiveProcess' -ErrorAction SilentlyContinue
    $running = Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue
    [pscustomobject]@{
        Running      = $steamRunning
        ActiveUser   = if ($active -and $active.ActiveUser) { [uint32]$active.ActiveUser } else { [uint32]0 }
        RunningAppId = if ($running -and $running.RunningAppID) { [uint32]$running.RunningAppID } else { [uint32]0 }
    }
}

# The user's own CS2 being launched, as Steam's console log says it before the process exists and while it starts: its "GameAction
# [AppID 730, ...] : LaunchApp ..." steps (Play, a steam:// link, a shortcut), an applaunch or rungameid command line, or a CS2 game process
# added that is not this harness's own (ours carries -con_logfile cs2harness_console.log). The watchdog reads it every 100 ms, a start
# before it launches anything (Test-Cs2SteamLaunching).
$script:SteamLaunchPattern = '(?m)^.*(GameAction \[AppID 730\b|ExecCommandLine:.*(applaunch\s+730\b|rungameid/730\b|run/730\b)|Game process added : AppID 730\b(?!.*cs2harness_console\.log)).*$'

# The first launch line in a piece of Steam's console log, or $null.
function Find-Cs2SteamLaunch([string]$Text) {
    if ($Text -and $Text -match $script:SteamLaunchPattern) { return $Matches[0].Trim() }
    $null
}

# New text appended to a log since $Pos (a file shared with its writer: opened for reading only, never locked). Returns the text and moves $Pos.
function Read-Cs2NewText([string]$Path, [ref]$Pos, [int]$Max = 4MB) {
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    $len = (Get-Item -LiteralPath $Path).Length
    if ($len -lt $Pos.Value) { $Pos.Value = 0 }
    if ($len -eq $Pos.Value) { return '' }
    $start = [Math]::Max($Pos.Value, $len - $Max)
    $fs = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        [void]$fs.Seek($start, [IO.SeekOrigin]::Begin)
        $buf = New-Object byte[] ($len - $start)
        $n = $fs.Read($buf, 0, $buf.Length)
    }
    finally { $fs.Close() }
    $Pos.Value = $start + $n
    [Text.Encoding]::UTF8.GetString($buf, 0, $n)
}

# Whether Steam is launching (or running) CS2 now - a start's preflight, and its last look right before the launch: Steam's own running
# app, or a launch line stamped within the last $WithinSec seconds of its console log (the lines carry the local time). The reason, or $null.
function Test-Cs2SteamLaunching($Env, [int]$WithinSec = 180, $SteamState = $null) {
    $st = if ($SteamState) { $SteamState } else { Get-SteamState }
    if ($st.RunningAppId -eq 730) { return 'Steam reports CS2 (app 730) as its running game' }
    if (-not $Env.SteamConsoleLog -or -not (Test-Path -LiteralPath $Env.SteamConsoleLog)) { return $null }
    $pos = [long][Math]::Max(0, (Get-Item -LiteralPath $Env.SteamConsoleLog).Length - 1MB)
    $text = Read-Cs2NewText $Env.SteamConsoleLog ([ref]$pos)
    $since = (Get-Date).AddSeconds(-$WithinSec)
    foreach ($m in [regex]::Matches($text, $script:SteamLaunchPattern)) {
        if ($m.Value -match '^\s*\[(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)\]') {
            $t = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
            if ($t -ge $since) { return "Steam was asked to launch CS2 at $($t.ToString('HH:mm:ss')): $($m.Value.Trim())" }
        }
    }
    $null
}

# ---------------------------------------------------------------------------------------------------------------- session state

function Get-SessionPath { Join-Path $script:StateDir 'session.json' }
function Read-Cs2Session { Read-Json (Get-SessionPath) }
function Write-Cs2Session($s) { Write-Json (Get-SessionPath) $s }

function Test-Cs2SessionLive($s) {
    if (-not $s -or $s.state -eq 'done') { return $false }
    return (Test-ProcessAlive $s.cs2Pid 'cs2' $s.cs2StartedUnix) -or (Test-ProcessAlive $s.watchdogPid 'pwsh')
}

function Update-Cs2Lease([int]$Minutes = 0) {
    $s = Read-Cs2Session
    if (-not $s -or $s.state -eq 'done') { return }
    if ($Minutes -le 0) { $Minutes = [int]$s.leaseMinutes }
    Write-Json (Join-Path $s.runDir 'lease.json') @{ leaseUntil = (Get-UnixNow) + 60 * $Minutes; by = $PID }
}

function Enter-Cs2ClientLock([string]$Purpose) {
    New-Item -ItemType Directory -Force -Path $script:StateDir | Out-Null
    $lock = Join-Path $script:StateDir 'client.lock'
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        try {
            $fs = [IO.File]::Open($lock, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            $bytes = [Text.Encoding]::UTF8.GetBytes((@{ pid = $PID; purpose = $Purpose; since = (Get-Date).ToString('s') } | ConvertTo-Json))
            $fs.Write($bytes, 0, $bytes.Length); $fs.Close()
            return
        }
        catch [IO.IOException] {
            $holder = Read-Json $lock
            $s = Read-Cs2Session
            $live = (Test-Cs2SessionLive $s) -or ($holder -and $holder.pid -ne $PID -and (Test-ProcessAlive ([int]$holder.pid) 'pwsh'))
            if ($live) { throw "the CS2 client harness is in use ($((Get-Content -LiteralPath $lock -Raw) -replace '\s+', ' ')) - wait, or ask whoever runs that session" }
            Remove-Item -LiteralPath $lock -Force   # stale: nothing of that session is alive
        }
    }
    throw "could not take $lock"
}

function Exit-Cs2ClientLock { Remove-Item -LiteralPath (Join-Path $script:StateDir 'client.lock') -Force -ErrorAction SilentlyContinue }

# ---------------------------------------------------------------------------------------------------------------- the user's settings

function Get-SettingsFiles($Env) {
    $list = @()
    foreach ($f in @(Get-ChildItem -LiteralPath $Env.UserCfgDir -File -ErrorAction SilentlyContinue)) { $list += [pscustomobject]@{ Set = 'local'; Path = $f.FullName } }
    foreach ($f in @(Get-ChildItem -LiteralPath $Env.RemoteDir -File -Filter '*.vcfg' -ErrorAction SilentlyContinue)) { $list += [pscustomobject]@{ Set = 'cloud'; Path = $f.FullName } }
    foreach ($f in @(Get-ChildItem -LiteralPath $Env.CfgDir -File -Filter '*.vcfg' -ErrorAction SilentlyContinue)) { $list += [pscustomobject]@{ Set = 'game'; Path = $f.FullName } }
    $list
}

function Save-Cs2Settings($Env, [string]$BackupDir) {
    New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null
    $manifest = @()
    $i = 0
    foreach ($f in Get-SettingsFiles $Env) {
        $i++
        $copy = Join-Path $BackupDir ("{0:000}_{1}_{2}" -f $i, $f.Set, (Split-Path -Leaf $f.Path))
        Copy-Item -LiteralPath $f.Path -Destination $copy
        $item = Get-Item -LiteralPath $f.Path
        $manifest += [pscustomobject]@{ set = $f.Set; path = $f.Path; copy = $copy; sha = (Get-FileHash -LiteralPath $f.Path -Algorithm SHA256).Hash; mtimeTicks = $item.LastWriteTimeUtc.Ticks }
    }
    Write-Json (Join-Path $BackupDir 'manifest.json') $manifest
    $manifest.Count
}

# Puts every backed-up file whose content changed back as it was (timestamps included). Cloud copies are restored too, and the report
# says so: Steam uploads a changed copy at its next sync, which is then the user's original again.
function Restore-Cs2Settings($Env, [string]$BackupDir) {
    $manifest = @(Read-Json (Join-Path $BackupDir 'manifest.json'))
    $report = @()
    foreach ($m in $manifest) {
        $state = 'unchanged'
        if (-not (Test-Path -LiteralPath $m.path)) { $state = 'missing' }
        elseif ((Get-FileHash -LiteralPath $m.path -Algorithm SHA256).Hash -ne $m.sha) { $state = 'changed' }
        elseif ((Get-Item -LiteralPath $m.path).LastWriteTimeUtc.Ticks -ne [long]$m.mtimeTicks) { $state = 'rewritten identically' }
        $diff = $null
        if ($state -in 'changed', 'missing') {
            if ($state -eq 'changed') { $diff = (Compare-Object (Get-Content -LiteralPath $m.copy) (Get-Content -LiteralPath $m.path) | ForEach-Object { "$($_.SideIndicator) $($_.InputObject.Trim())" }) -join ' | ' }
            Copy-Item -LiteralPath $m.copy -Destination $m.path -Force
            (Get-Item -LiteralPath $m.path).LastWriteTimeUtc = [DateTime]::new([long]$m.mtimeTicks, [DateTimeKind]::Utc)
            $ok = (Get-FileHash -LiteralPath $m.path -Algorithm SHA256).Hash -eq $m.sha
            $state = if ($ok) { "$state - restored" } else { "$state - RESTORE FAILED" }
        }
        $report += [pscustomobject]@{ set = $m.set; file = (Split-Path -Leaf $m.path); state = $state; diff = $diff }
    }
    # Files that appeared during the session are reported, not deleted.
    $known = @($manifest | ForEach-Object { $_.path })
    foreach ($f in Get-SettingsFiles $Env) { if ($known -notcontains $f.Path) { $report += [pscustomobject]@{ set = $f.Set; file = (Split-Path -Leaf $f.Path); state = 'new during the session (left in place)'; diff = $null } } }
    $report
}

# ---------------------------------------------------------------------------------------------------------------- server checks

# The server lock (server.lock_file in the private config): its text, or nothing when it is free. With no lock file named, or no config
# to read one from, a held lock cannot be ruled out, so it reads as held - and a session without a server still starts and reports.
function Get-Cs2ServerLock {
    try { $p = (Get-Cs2Config).lock_file }
    catch { return "the server lock cannot be checked: $($_.Exception.Message)" }
    if (-not $p) { return 'no server.lock_file in the private config: the harness cannot tell whether anyone is acting on the server' }
    if (Test-Path -LiteralPath $p) { (Get-Content -LiteralPath $p -Raw).Trim() }
}

function Invoke-Cs2Rcon([string[]]$Commands) {
    $r = Invoke-Cs2Probe (@('rcon') + $Commands)
    if (-not $r.Ok) { throw "RCON failed: $($r.Text.Trim())" }
    $r.Text
}

# What answers on the server's address right now: the running containers that publish its port, and the game container, by the Docker
# API (read-only), plus RCON status. A test copy of production (under the server lock) answers on the same address with the same name.
function Get-Cs2ServerIdentity {
    $containers = $null
    $r = Invoke-Cs2Probe @('server')
    if ($r.Ok) { try { $containers = $r.Text | ConvertFrom-Json } catch { } }
    $status = $null
    try { $status = Invoke-Cs2Rcon @('status') } catch { $status = "RCON failed: $($_.Exception.Message)" }
    [pscustomobject]@{ Containers = $containers; RconStatus = $status }
}

# ---------------------------------------------------------------------------------------------------------------- VAC safety
#
# The user's Steam account must never be put at risk of a VAC ban: the harness's first rule. The harness never reads or writes cs2's
# memory, never injects or hooks, never attaches a debugger, and never opens cs2 for more than
# query, priority, quota and terminate rights (Cs2Native.cs; no Toolhelp module snapshot, no WMI command-line read of a cs2 process).
# Before every launch Test-Cs2Integrity refuses anything that could reach a VAC-secured server modified:
#   - launch options that are never used (-tools, -allow_third_party_software, ...) and, outside an insecure session, the ones that
#     only an insecure session may carry;
#   - gameinfo.gi / gameinfo_branchspecific.gi edited (a Metamod search path, a change after Steam's last update of the game, or a
#     change within the same Steam build);
#   - loose compiled game files (*_c outside the VPKs; the Workshop Tools' own editor files excepted), a game\csgo\addons folder;
#   - unsigned map VPKs (a loose map: the client refuses them anyway, "Signature failed");
#   - proxy DLLs (d3d11.dll, dxgi.dll, ... or *.asi) beside the game's binaries;
#   - capture or overlay tools that hook into games (RivaTuner, Afterburner, OBS, Bandicam, ...) running at all.
# Loose files (a HUD build under test) only ever run with -insecure (VAC off for that session), against a server that is not
# VAC-secured (checked by A2S before the launch; the client itself also refuses a secure server in insecure mode).

$script:ForbiddenArgs = @('-tools', '-allow_third_party_software', '-nosteam', '-vconsole', '+sv_cheats', '-dev', '-hijack')
$script:InsecureOnlyArgs = @('-insecure', '-netconport', '-netconpassword', '-vconport', '-novconsole')
$script:HookProcesses = @('RTSS', 'RTSSHooksLoader', 'RTSSHooksLoader64', 'EncoderServer', 'MSIAfterburner', 'obs', 'obs32', 'obs64',
    'bdcam', 'bandicam', 'fraps', 'Dxtory', 'action_x64', 'Overwolf', 'OverwolfLauncher', 'Medal', 'Outplayed', 'PlayClaw', 'XSplit.Core',
    'ReShade', 'SpecialK64', 'SKIF', 'Cheat Engine', 'cheatengine-x86_64', 'x64dbg', 'x32dbg', 'ollydbg', 'ida64', 'ProcessHacker',
    'SystemInformer')
$script:ProxyDlls = @('d3d9.dll', 'd3d10.dll', 'd3d11.dll', 'd3d12.dll', 'dxgi.dll', 'dinput8.dll', 'version.dll', 'winmm.dll',
    'opengl32.dll', 'vulkan-1.dll', 'winhttp.dll', 'xinput1_3.dll', 'xinput1_4.dll', 'dsound.dll', 'msimg32.dll', 'wininet.dll')
$script:LooseAllowed = @('lighting\postprocessing\editor\')   # the Workshop Tools' own files, installed by Steam

function Get-Cs2AppManifest($Env) {
    $acf = Join-Path (Split-Path -Parent (Split-Path -Parent $Env.Cs2Root)) 'appmanifest_730.acf'
    $t = if (Test-Path -LiteralPath $acf) { Get-Content -LiteralPath $acf -Raw } else { '' }
    [pscustomobject]@{
        BuildId     = if ($t -match '"buildid"\s+"(\d+)"') { $Matches[1] } else { '' }
        LastUpdated = if ($t -match '"LastUpdated"\s+"(\d+)"') { [double]$Matches[1] } else { 0 }
    }
}

# A VPK whose header has no signature section is not Valve's (Valve's map and content VPKs are v2 with one).
function Test-VpkSigned([string]$Path) {
    $fs = [IO.File]::OpenRead($Path)
    try { $b = New-Object byte[] 28; if ($fs.Read($b, 0, 28) -lt 28) { return $false } } finally { $fs.Close() }
    if ([BitConverter]::ToUInt32($b, 0) -ne 0x55AA1234) { return $false }
    return [BitConverter]::ToUInt32($b, 4) -eq 2 -and [BitConverter]::ToUInt32($b, 24) -gt 0
}

function Test-Cs2Integrity($Env, [string[]]$GameArgs, [switch]$Insecure) {
    $bad = [Collections.Generic.List[string]]::new()
    $warn = [Collections.Generic.List[string]]::new()
    $lower = @($GameArgs | ForEach-Object { "$_".ToLowerInvariant() })
    foreach ($a in $script:ForbiddenArgs) { if ($lower -contains $a) { $bad.Add("launch option $a - never used by this harness") } }
    if (-not $Insecure) { foreach ($a in $script:InsecureOnlyArgs) { if ($lower -contains $a) { $bad.Add("launch option $a - only an insecure session (-Insecure) may carry it") } } }

    # gameinfo: no Metamod, nothing changed since Steam last updated the game, nothing changed within one Steam build.
    $am = Get-Cs2AppManifest $Env
    $basePath = Join-Path $script:StateDir 'integrity.json'
    $base = Read-Json $basePath
    $now = @{}
    foreach ($n in 'gameinfo.gi', 'gameinfo_branchspecific.gi') {
        $p = Join-Path $Env.GameDir $n
        if (-not (Test-Path -LiteralPath $p)) { $bad.Add("$n is missing"); continue }
        $text = Get-Content -LiteralPath $p -Raw
        if ($text -match '(?i)metamod') { $bad.Add("$n names Metamod - an edited gameinfo (Steam: Verify integrity of game files)") }
        $mt = ([DateTimeOffset](Get-Item -LiteralPath $p).LastWriteTimeUtc).ToUnixTimeSeconds()
        if ($am.LastUpdated -gt 0 -and $mt -gt $am.LastUpdated + 600) { $bad.Add("$n changed at $(Format-Unix $mt), after Steam last updated the game ($(Format-Unix $am.LastUpdated)) - an edited gameinfo") }
        $now[$n] = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash
        if ($base -and $base.buildId -eq $am.BuildId -and $base.files.$n -and $base.files.$n -ne $now[$n]) { $bad.Add("$n differs from what it was earlier in Steam build $($am.BuildId) - an edited gameinfo") }
    }
    if ($bad.Where({ $_ -like 'gameinfo*' }).Count -eq 0 -and (-not $base -or $base.buildId -ne $am.BuildId)) {
        Write-Json $basePath @{ buildId = $am.BuildId; recorded = (Get-Date).ToString('s'); files = $now }
    }

    # Loose game content.
    $root = $Env.GameDir.TrimEnd('\') + '\'
    foreach ($f in @(Get-ChildItem -LiteralPath $Env.GameDir -Recurse -File -Filter '*_c' -ErrorAction SilentlyContinue)) {
        $rel = $f.FullName.Substring($root.Length)
        if (-not ($script:LooseAllowed | Where-Object { $rel.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) })) { $bad.Add("loose game file game\csgo\$rel") }
        if ($bad.Count -gt 40) { $bad.Add('... (more)'); break }
    }
    if (Test-Path -LiteralPath (Join-Path $Env.GameDir 'addons')) { $bad.Add('game\csgo\addons exists - server plugins have no place in a client') }
    foreach ($v in @(Get-ChildItem -LiteralPath (Join-Path $Env.GameDir 'maps') -Recurse -File -Filter '*.vpk' -ErrorAction SilentlyContinue) +
        @(Get-ChildItem -LiteralPath $Env.GameDir -File -Filter '*.vpk' -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch '^(pak01|shaders_pc|shaders_vulkan)_(dir|\d{3})\.vpk$' })) {
        if ($v.Name -match '_\d{3}\.vpk$') { continue }   # the archives of a multi-part VPK carry no header; their _dir does
        if (-not (Test-VpkSigned $v.FullName)) { $bad.Add("unsigned VPK $($v.FullName.Substring($root.Length)) - a loose map or mod") }
    }

    # Proxy DLLs beside the game's binaries (loaded into cs2 by name).
    foreach ($d in (Join-Path $Env.Cs2Root 'game\bin\win64'), (Join-Path $Env.GameDir 'bin\win64'), $Env.Cs2Root) {
        foreach ($f in @(Get-ChildItem -LiteralPath $d -File -ErrorAction SilentlyContinue)) {
            if ($script:ProxyDlls -contains $f.Name.ToLowerInvariant() -or $f.Extension -eq '.asi') { $bad.Add("$($f.FullName) - a proxy DLL would be loaded into cs2") }
        }
    }

    # Tools that hook into game processes (or that inspect them), running at all.
    $procs = @(Get-Process -ErrorAction SilentlyContinue | ForEach-Object { $_.ProcessName })
    foreach ($h in $script:HookProcesses) {
        foreach ($p in $procs) { if (($p -ieq $h) -or ($h.Length -ge 6 -and $p -like "$h*")) { $bad.Add("$p.exe is running - it hooks into (or reads) game processes; close it first") } }
    }
    if ($procs -contains 'Discord') { $warn.Add('Discord is running: its overlay hooks games it sees as visible (it was seen trying a visible CS2, and failing); it has never been seen trying a hidden-desktop session, and the watchdog ends ours at the first hook attempt in discord_hook.log') }
    if ($procs -contains 'nvcontainer') { $warn.Add('the NVIDIA App container is running: its overlay hooks games when enabled (Valve tolerates it)') }
    [pscustomobject]@{ Violations = @($bad | Select-Object -Unique); Warnings = @($warn); BuildId = $am.BuildId }
}

# A2S_INFO of a server: name, map, humans, and the VAC flag (1 = VAC-secured). $null when it does not answer.
function Get-Cs2A2S([string]$Address) {
    $h, $port = $Address -split ':'
    $udp = [Net.Sockets.UdpClient]::new()
    $udp.Client.ReceiveTimeout = 3000
    try {
        $q = [byte[]](0xFF, 0xFF, 0xFF, 0xFF, 0x54) + [Text.Encoding]::ASCII.GetBytes('Source Engine Query') + [byte[]](0)
        $udp.Connect($h, [int]$port)
        [void]$udp.Send($q, $q.Length)
        $ep = [Net.IPEndPoint]::new([Net.IPAddress]::Any, 0)
        $d = $udp.Receive([ref]$ep)
        if ($d[4] -eq 0x41) { $q2 = $q + $d[5..8]; [void]$udp.Send($q2, $q2.Length); $d = $udp.Receive([ref]$ep) }
        $i = 6; $strs = @()
        for ($k = 0; $k -lt 4; $k++) { $j = [Array]::IndexOf($d, [byte]0, $i); $strs += [Text.Encoding]::UTF8.GetString($d, $i, $j - $i); $i = $j + 1 }
        $i += 2
        [pscustomobject]@{ Name = $strs[0]; Map = $strs[1]; Humans = [int]$d[$i] - [int]$d[$i + 2]; Vac = [int]$d[$i + 6] }
    }
    catch { $null }
    finally { $udp.Close() }
}

function Get-FreeTcpPort {
    $l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0); $l.Start()
    try { $l.LocalEndpoint.Port } finally { $l.Stop() }
}

# The console of an insecure session started with -Console: netcon (TCP, password), output collected for WaitMs.
function Invoke-Cs2Console([string]$Command, [int]$WaitMs = 1500) {
    Update-Cs2Lease
    $s = Read-Cs2Session
    if (-not $s -or $s.state -ne 'running') { throw 'no running session' }
    if (-not $s.insecure -or -not $s.netconPort) { throw 'no console in this session: only an insecure session started with -Console has one (never production)' }
    $c = [Net.Sockets.TcpClient]::new()
    try {
        $c.Connect('127.0.0.1', [int]$s.netconPort)
        $st = $c.GetStream(); $st.ReadTimeout = 200
        $send = { param($t) $b = [Text.Encoding]::UTF8.GetBytes("$t`n"); $st.Write($b, 0, $b.Length) }
        & $send "PASS $($s.netconPassword)"
        Start-Sleep -Milliseconds 250
        $buf = New-Object byte[] 65536
        while ($st.DataAvailable) { [void]$st.Read($buf, 0, $buf.Length) }
        & $send $Command
        $out = [Text.StringBuilder]::new()
        $end = (Get-Date).AddMilliseconds($WaitMs)
        while ((Get-Date) -lt $end) {
            if ($st.DataAvailable) { $n = $st.Read($buf, 0, $buf.Length); [void]$out.Append([Text.Encoding]::UTF8.GetString($buf, 0, $n)) }
            else { Start-Sleep -Milliseconds 50 }
        }
        $out.ToString().TrimEnd()
    }
    finally { $c.Close() }
}

# ---------------------------------------------------------------------------------------------------------------- the HUD under test

function Get-HudLocalMarker { Join-Path $script:StateDir 'hud-local.json' }

# The stage: where a session's loose HUD files wait, beside the game's folder on the same volume and outside every search path the game
# has (game\csgo, game\core and their VPKs - Filesystem's list at boot): nothing in it is ever read by any launch. Renames move the build's
# folders into game\csgo\panorama at once, right before the launch, and back out at once when the session is to end.
function Get-Cs2HudStage($Env) { Join-Path $Env.Cs2Root 'cs2harness-stage' }

# A loose HUD build for one insecure session. The game reads loose compiled Panorama files under game\csgo\panorama\... before the Workshop
# addon's copy of the same path, and that is the only place this CS2 reads them from: -addon <name> mounts nothing without -tools (tested
# 2026-11-20 - the boot's search paths and every loop's addons() stayed empty), and -tools is never used. So the build's compiled files
# (*_c: layout, stylesheets, textures; the map thumbnails only with -IncludeThumbs) are copied into the stage first, while nothing of them is
# in the game's folder; Enable-Cs2HudLocal then puts them in place with one rename per top folder (layout, styles, images - none of which a
# stock CS2 has in game\csgo\panorama, where only Valve's fonts and videos are). Refuses when any of those folders is there already.
function Install-Cs2HudLocal($Env, [string]$Source, [switch]$IncludeThumbs) {
    if (-not $Source) { $Source = $Env.HudBuildDir }
    $panorama = Join-Path $Source 'panorama'
    if (-not (Test-Path -LiteralPath $panorama)) { throw "no compiled HUD in $Source - run build.ps1 first (PUBLISH.md)" }
    if (Test-Path -LiteralPath (Get-HudLocalMarker)) { throw "a previous loose HUD install is still recorded ($(Get-HudLocalMarker)); run 'cs2client.ps1 cleanup' first" }
    $files = @(Get-ChildItem -LiteralPath $panorama -Recurse -File | Where-Object { $_.Name -like '*_c' -and ($IncludeThumbs -or $_.FullName -notmatch '\\thumbs\\') })
    if ($files.Count -eq 0) { throw "no compiled files (*_c) under $panorama" }
    $rels = @(foreach ($f in $files) { $f.FullName.Substring($panorama.Length).TrimStart('\') })
    if ($rels | Where-Object { $_ -notmatch '\\' }) { throw "compiled files directly in the build's panorama folder ($(($rels | Where-Object { $_ -notmatch '\\' } | Select-Object -First 3) -join ', ')): they cannot be moved in by folder" }
    $tops = @($rels | ForEach-Object { ($_ -split '\\')[0] } | Sort-Object -Unique)
    $gamePanorama = Join-Path $Env.GameDir 'panorama'
    $there = @($tops | Where-Object { Test-Path -LiteralPath (Join-Path $gamePanorama $_) })
    if ($there.Count) { throw "game\csgo\panorama already has $(($there | ForEach-Object { "$_\" }) -join ', ') (loose files of an earlier test?) - refusing; 'cs2client.ps1 check' lists them" }
    $stage = Get-Cs2HudStage $Env
    Clear-Cs2HudStage $Env | Out-Null
    $staged = Join-Path $stage 'panorama'
    for ($i = 0; $i -lt $files.Count; $i++) {
        $to = Join-Path $staged $rels[$i]
        $d = Split-Path -Parent $to
        if (-not [IO.Directory]::Exists($d)) { [void][IO.Directory]::CreateDirectory($d) }
        [IO.File]::Copy($files[$i].FullName, $to)
    }
    [pscustomobject]@{ Source = $Source; Stage = $stage; Staged = $staged; GamePanorama = $gamePanorama; Tops = $tops
        Moved = @($tops | ForEach-Object { Join-Path $gamePanorama $_ }); Files = @($rels | ForEach-Object { Join-Path $gamePanorama $_ }) }
}

# The renames that put a staged build in place (game\csgo\panorama\<layout, styles, images>), recorded before they happen so a crash in
# between leaves nothing unrecorded. The last thing before the launch.
function Enable-Cs2HudLocal($Hud) {
    # the marker stays small (the folders, not the files): the removal reads it before anything else, and must not wait on parsing thousands of paths
    Write-Json (Get-HudLocalMarker) @{ source = $Hud.Source; moved = @($Hud.Moved); stage = $Hud.Stage; count = @($Hud.Files).Count; since = (Get-Date).ToString('s') }
    if (-not [IO.Directory]::Exists($Hud.GamePanorama)) { [void][IO.Directory]::CreateDirectory($Hud.GamePanorama) }
    foreach ($t in $Hud.Tops) { [IO.Directory]::Move((Join-Path $Hud.Staged $t), (Join-Path $Hud.GamePanorama $t)) }
}

# Takes the loose HUD files out of the game's folder. Called the moment a session is to end - the watchdog's yield, the stop verb, a kill,
# before the quit is even asked (VAC safety: nothing of it may outlive the decision) - and again at the clean-up after the exit; idempotent.
# One rename per top folder moves the build back into the stage, out of every search path at once (tens of milliseconds, where deleting the
# 9242 files one by one took 3 s); the stage itself is deleted later (Clear-Cs2HudStage), when nothing hurries. Should a rename fail - a file
# our own game holds open - what is left goes file by file; anything still there is reported (the watchdog then ends our game at once and
# tries again). An install recorded file by file (before 2026-11-20) is removed file by file.
function Remove-Cs2HudLocal {
    $marker = Get-HudLocalMarker
    $m = Read-Json $marker
    if (-not $m) { return 'no loose HUD files recorded' }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $how = @()
    $q = Join-Path ([string]$m.stage) "removed-$(Get-Date -Format 'yyyyMMdd-HHmmss-fff')"
    foreach ($d in @($m.moved)) {
        if (-not $d -or -not [IO.Directory]::Exists($d)) { continue }
        try { [void][IO.Directory]::CreateDirectory($q); [IO.Directory]::Move($d, (Join-Path $q (Split-Path -Leaf $d))); $how += "$(Split-Path -Leaf $d)\ renamed out" }
        catch { $how += "$(Split-Path -Leaf $d)\ could not be renamed ($($_.Exception.Message.Trim()))" }
    }
    $out = $sw.ElapsedMilliseconds
    # what the renames could not move (or an older install's own list): file by file
    $files = @($m.files) + @(foreach ($d in @($m.moved)) { if ($d -and [IO.Directory]::Exists($d)) { [IO.Directory]::EnumerateFiles($d, '*', [IO.SearchOption]::AllDirectories) } })
    $left = [Collections.Generic.List[string]]::new()
    foreach ($f in $files) { if ($f -and [IO.File]::Exists($f)) { try { [IO.File]::Delete($f) } catch { }; if ([IO.File]::Exists($f)) { $left.Add($f) } } }
    $dirs = @(@($m.dirs) + @(foreach ($d in @($m.moved)) { if ($d -and [IO.Directory]::Exists($d)) { @([IO.Directory]::EnumerateDirectories($d, '*', [IO.SearchOption]::AllDirectories)) + $d } })) | Where-Object { $_ } | Sort-Object { $_.Length } -Descending
    foreach ($d in $dirs) { try { if ([IO.Directory]::Exists($d) -and -not [IO.Directory]::EnumerateFileSystemEntries($d).GetEnumerator().MoveNext()) { [IO.Directory]::Delete($d) } } catch { } }
    if ($left.Count) { return "LOOSE HUD FILES LEFT ($($left.Count)): $(($left | Select-Object -First 5) -join ', ')" }
    Remove-Item -LiteralPath $marker -Force
    $n = if ($m.count) { $m.count } else { @($m.files).Count }
    "$n loose HUD file(s) out of the game's folder ($(if ($how) { "$($how -join ', '): out after $out ms" } else { 'file by file' }); $($sw.ElapsedMilliseconds) ms in all)"
}

# Deletes the stage (a staged build never moved in, the folders moved back out): outside the game's search paths, so only tidiness waits on it.
function Clear-Cs2HudStage($Env) {
    $stage = Get-Cs2HudStage $Env
    if (-not (Test-Path -LiteralPath $stage)) { return 'no stage' }
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $stage) { "the stage $stage could not be removed entirely" } else { 'stage removed' }
}

# ---------------------------------------------------------------------------------------------------------------- start

function Start-Cs2Client {
    param(
        [string]$Connect,                    # ip:port; 'default' = server.address of the private config
        [string[]]$Commands = @(),           # console commands run at boot (before a connect)
        [string]$Purpose = 'client test',
        [switch]$AllowServerLock,            # connect although the server lock (server.lock_file) is held (only when YOU hold it for this test)
        [int]$LeaseMinutes = 15,             # the watchdog quits the client after this long without a harness command
        [int]$MaxMinutes = 60,               # and after this long in any case
        [ValidateSet('none', 'window', 'menu', 'ingame')][string]$WaitFor = 'menu',
        [int]$TimeoutSec = 240,
        [string]$Resolution = '1920x1080',   # windowed at this size (-sw -w -h; a quarter of a 4K screen's GPU load). CS2 writes it
                                             # into cs2_video.txt at start; restored at the end. 'native': the user's own video config, untouched.
        [string[]]$ExtraArgs = @(),
        [switch]$HudLocal,                   # install the compiled HUD build as loose files for this session (forces -Insecure)
        [string]$HudSource,
        [string]$HudAddon,                   # the built addon by its folder's name: game\csgo_addons\<name> of this CS2 (implies -HudLocal)
        [switch]$IncludeThumbs,
        [switch]$Insecure,                   # -insecure: VAC off for this session; joins only a server that is not VAC-secured
        [switch]$Console                     # insecure only: netcon (random port and password) + VConsole, for the cmd verb
    )
    $Env = Get-Cs2Env
    Import-Cs2Native

    # 1. Preflight: Steam logged in, no CS2 running (the user's or anyone's), no live harness session.
    $steam = Get-SteamState
    if (-not $steam.Running -or $steam.ActiveUser -eq 0) { throw 'Steam is not running and logged in - CS2 cannot start without it' }
    $running = @(Get-Cs2Processes)
    if ($running.Count) { throw "cs2.exe is already running (pid $($running.Pid -join ', ')) - the user's or another session's; refusing (never touch a CS2 the harness did not start)" }
    # Nor while Steam is launching one: between Play and the user's cs2.exe Steam takes seconds (install scripts, cloud, the process), and
    # no loose file may exist when that game starts (VAC safety).
    if ($launching = Test-Cs2SteamLaunching $Env) { throw "$launching - refusing: the user's own CS2 may be starting" }
    if ($HudAddon) {
        if ($HudSource) { throw '-HudAddon and -HudSource both name the build: give one' }
        if ($HudAddon -notmatch '^[\w.-]+$') { throw "-HudAddon takes an addon folder's name, not '$HudAddon'" }
        $HudSource = Join-Path $Env.Cs2Root "game\csgo_addons\$HudAddon"
        $HudLocal = [switch]$true
    }

    # 2. Secure or insecure. Loose game files never meet a VAC-secured server: they force an insecure session, and an insecure session
    #    joins only a server whose A2S says it is not VAC-secured (production is). The game itself refuses that join too.
    if ($HudLocal -and -not $Insecure) { Write-Host 'note: -HudLocal runs the client with -insecure (VAC off for this session); it can join only an insecure server (the test copy)'; $Insecure = $true }
    if ($Console -and -not $Insecure) { throw '-Console needs -Insecure: a VAC-secured client has no console channel, and production never sees an insecure client' }
    if ($Connect -eq 'default') { $Connect = (Get-Cs2Config).address }
    if ($Connect) {
        $lockText = Get-Cs2ServerLock
        if ($lockText -and -not $AllowServerLock) { throw "the server lock is held - another agent is acting on the server and a client joining could disturb it:`n$lockText`n(pass -AllowServerLock only if you hold it for this test)" }
        if ($Insecure) {
            $info = Get-Cs2A2S $Connect
            if (-not $info) { throw "$Connect does not answer A2S: refusing an insecure join that cannot be proven to be to a server without VAC" }
            if ($info.Vac -ne 0) { throw "$Connect ('$($info.Name)', $($info.Map)) is VAC-secured: an insecure client (loose files, console) never joins it - start the insecure test copy first (os2-deploy testcopy, README.md)" }
            Write-Host "server $Connect ('$($info.Name)', $($info.Map), $($info.Humans) human(s)) is not VAC-secured - an insecure join is allowed"
        }
    }

    # 3. The launch options, then the self-check that refuses anything that could put the account at risk.
    $gameArgs = @('-steam', '-nosound', '-con_logfile', $script:LogName)
    if ($Resolution -and $Resolution -ne 'native') {
        $wh = $Resolution -split 'x'
        if ($wh.Count -ne 2 -or -not ($wh[0] -match '^\d+$' -and $wh[1] -match '^\d+$')) { throw "-Resolution takes WIDTHxHEIGHT or native, not '$Resolution'" }
        $gameArgs += @('-sw', '-w', $wh[0], '-h', $wh[1])
    }
    $netcon = $null
    if ($Insecure) {
        $gameArgs += '-insecure'
        if ($Console) {
            # netcon needs the VConsole module loaded (it opens WinSock early); both listen on every interface, so random ports and a
            # random netcon password. VConsole itself has no password (README.md, "Commands at runtime").
            $netcon = [pscustomobject]@{ Port = Get-FreeTcpPort; Password = -join ((1..24) | ForEach-Object { '{0:x}' -f (Get-Random -Maximum 16) }); VconPort = Get-FreeTcpPort }
            $gameArgs += @('-netconport', $netcon.Port, '-netconpassword', $netcon.Password, '-vconport', $netcon.VconPort)
        }
        else { $gameArgs += '-novconsole' }  # no listener at all
    }
    if ($ExtraArgs) { $gameArgs += $ExtraArgs }
    $gameArgs += @('+exec', $script:BootCfg)
    $check = Test-Cs2Integrity $Env $gameArgs -Insecure:$Insecure
    foreach ($w in $check.Warnings) { Write-Host "warning: $w" }
    if ($check.Violations.Count) { throw "REFUSED by the VAC-safety self-check (README.md, 'VAC safety'):`n  - $($check.Violations -join "`n  - ")" }

    Enter-Cs2ClientLock $Purpose
    try {
        Invoke-Cs2Leftovers $Env | ForEach-Object { Write-Host "leftover cleanup: $_" }

        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $slug = ($Purpose -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLowerInvariant()
        if ($slug.Length -gt 40) { $slug = $slug.Substring(0, 40) }
        $runDir = Join-Path $script:StateDir "runs\$stamp-$slug"
        New-Item -ItemType Directory -Force -Path $runDir | Out-Null
        $n = Save-Cs2Settings $Env (Join-Path $runDir 'backup')

        # The watchdog reads Steam's console log from here on: from before the loose files exist, so a launch of the user's CS2 logged while
        # they are put in place is seen by its first look.
        $steamLogPos = if (Test-Path -LiteralPath $Env.SteamConsoleLog) { (Get-Item -LiteralPath $Env.SteamConsoleLog).Length } else { 0 }
        $hud = $null
        if ($HudLocal) { $hud = Install-Cs2HudLocal $Env $HudSource -IncludeThumbs:$IncludeThumbs }

        $boot = @('// written by the CS2 client test harness (tools/client); deleted when the session ends') + $Commands
        if ($Connect) { $boot += "connect $Connect" }  # from a cfg: a command-line +connect is held back by gameinfo's DelayedConCommands and never ran
        Write-Utf8 (Join-Path $Env.CfgDir $script:BootCfg) (($boot -join "`r`n") + "`r`n")

        $cmdLine = '"' + $Env.Exe + '" ' + ($gameArgs -join ' ')

        # The last look before the launch: nothing of the user's CS2 began while the files went in (the catch below removes them again).
        $other = @(Get-Cs2Processes)
        if ($other.Count) { throw "a cs2.exe started while this session was being set up (pid $($other.Pid -join ', ')) - the user's game; the loose files are removed again" }
        $spos = [long]$steamLogPos
        if ($launch = Find-Cs2SteamLaunch (Read-Cs2NewText $Env.SteamConsoleLog ([ref]$spos))) { throw "Steam was asked to launch CS2 while this session was being set up: $launch - the loose files are removed again" }
        # Only now does the staged build go in place: one rename, then the launch.
        if ($hud) { Enable-Cs2HudLocal $hud }

        $envVars = [Collections.Generic.Dictionary[string, string]]::new()
        $envVars['SteamAppId'] = '730'; $envVars['SteamGameId'] = '730'
        $cs2Pid = [Cs2Harness.Native]::StartOnDesktop($script:Desktop, $Env.Exe, $cmdLine, (Split-Path -Parent $Env.Exe), $envVars)
        $started = Get-UnixNow
        try { $started = ([DateTimeOffset](Get-Process -Id $cs2Pid).StartTime).ToUnixTimeMilliseconds() / 1000.0 } catch { }

        $session = [ordered]@{
            id = $stamp; purpose = $Purpose; state = 'running'; runDir = $runDir; desktop = $script:Desktop
            cs2Pid = $cs2Pid; cs2StartedUnix = $started; commandLine = ($cmdLine -replace '-netconpassword \S+', '-netconpassword <hidden>')
            connect = $Connect; bootCommands = $Commands; insecure = [bool]$Insecure; buildId = $check.BuildId
            netconPort = $(if ($netcon) { $netcon.Port } else { 0 }); netconPassword = $(if ($netcon) { $netcon.Password } else { '' })
            vconPort = $(if ($netcon) { $netcon.VconPort } else { 0 })
            leaseMinutes = $LeaseMinutes; maxUntil = $started + 60 * $MaxMinutes; steamLogPos = $steamLogPos
            settingsFiles = $n; hudLocal = [bool]$HudLocal; watchdogPid = 0; startedBy = $PID
        }
        Write-Cs2Session ([pscustomobject]$session)
        Update-Cs2Lease $LeaseMinutes

        $pw = (Get-Process -Id $PID).Path
        $wd = '"' + $pw + '" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + (Join-Path $script:Here 'watchdog.ps1') + '" -StateDir "' + $script:StateDir + '"'
        $session.watchdogPid = [int][Cs2Harness.Native]::StartDetached($pw, $wd, $runDir)
        Write-Cs2Session ([pscustomobject]$session)
        # CS2 never runs unguarded: the watchdog must report (guard.json, after it has tied cs2 to itself) within 20 s. Until it does, this
        # start keeps the watchdog's watch for the user's CS2 itself, every 100 ms (the loose files are in place now).
        $deadline = (Get-Date).AddSeconds(20)
        while (-not (Test-Path -LiteralPath (Join-Path $runDir 'guard.json')) -and (Get-Date) -lt $deadline) {
            if ($hud) {
                $other = @(Get-Cs2Processes | Where-Object { $_.Pid -ne $cs2Pid })
                $launch = if ($other.Count) { "a cs2.exe (pid $($other.Pid -join ', '))" } else { Find-Cs2SteamLaunch (Read-Cs2NewText $Env.SteamConsoleLog ([ref]$spos)) }
                if ($launch) { $gone = Remove-Cs2HudLocal; throw "the user's CS2 is starting ($launch) - the loose files first: $gone; ours is stopped" }
            }
            Start-Sleep -Milliseconds 100
        }
        if (-not (Test-Path -LiteralPath (Join-Path $runDir 'guard.json'))) { throw "the watchdog (pid $($session.watchdogPid)) did not come up within 20 s - see $runDir\watchdog.log; cs2 is stopped" }
    }
    catch {
        # Nothing may be left behind by a start that failed half way.
        $s = Read-Cs2Session
        if ($s -and $s.state -eq 'running' -and (Test-ProcessAlive $s.cs2Pid 'cs2')) { Stop-Process -Id $s.cs2Pid -Force -ErrorAction SilentlyContinue }
        Remove-Item -LiteralPath (Join-Path (Get-Cs2Env).CfgDir $script:BootCfg) -Force -ErrorAction SilentlyContinue
        if ($HudLocal) { Remove-Cs2HudLocal | Out-Null; Clear-Cs2HudStage (Get-Cs2Env) | Out-Null }
        if ($s -and $s.runDir -and (Test-Path -LiteralPath (Join-Path $s.runDir 'backup\manifest.json'))) { Restore-Cs2Settings (Get-Cs2Env) (Join-Path $s.runDir 'backup') | Out-Null }
        if ($s) { $s.state = 'done'; Write-Cs2Session $s }
        Exit-Cs2ClientLock
        throw
    }

    Write-Host "cs2 pid $cs2Pid on desktop $script:Desktop, watchdog pid $($session.watchdogPid), run $runDir"
    Write-Host $(if ($Insecure) { "INSECURE session (-insecure: VAC off; joins only a server that is not VAC-secured)$(if ($netcon) { "; console on netcon 127.0.0.1:$($netcon.Port) (password in session.json), VConsole on $($netcon.VconPort)" })" } else { 'secure session: a plain client - no loose files, no extra launch options (self-check passed)' })
    if ($hud) { Write-Host "loose HUD build: $(@($hud.Files).Count) file(s) from $($hud.Source)" }
    if ($WaitFor -ne 'none') { Wait-Cs2Ready -WaitFor $WaitFor -TimeoutSec $TimeoutSec }
    Get-Cs2ClientStatus -Brief
}

# menu: the main menu is up. ingame: connected to a server, map loaded, playing - after the LAST connection (a join that has to fetch
# the HUD addon connects twice), and still so 3 s later.
function Wait-Cs2Ready([string]$WaitFor = 'menu', [int]$TimeoutSec = 240) {
    $s = Read-Cs2Session
    if (-not $s) { throw 'no session' }
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $candidate = $null
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-ProcessAlive $s.cs2Pid 'cs2' $s.cs2StartedUnix)) { throw "cs2 exited while waiting for '$WaitFor' - see $($s.runDir)" }
        $w = Get-Cs2Window $s
        if ($w -and $WaitFor -eq 'window') { Write-Host "ready: window $($w.ToString())"; return }
        if ($w -and $WaitFor -eq 'menu') {
            # The UI state turns MAINMENU before the menu's background map has drawn a frame (a shot then is black, and a quit then
            # can hang in Source2Shutdown): ready means the state AND a frame that is not black.
            # The menu's own background map loads right after (SwitchToLoop game ... Signon traffic); a quit during that load hangs
            # in Source2Shutdown, so ready is after its signon.
            $hit = Find-Cs2LogLine 'CSGO_GAME_UI_STATE_MAINMENU'
            $signon = if ($hit) { Find-Cs2LogLine 'Signon traffic "server"' $hit.Number } else { $null }
            if ($signon -and (Test-Cs2Frame $s $w)) { Write-Host "ready (menu): line $($signon.Number): $($signon.Line)"; return }
        }
        if ($w -and $WaitFor -eq 'ingame') {
            $conn = Find-Cs2LogLine 'Received S2C_CONNECTION from' -Last
            $hit = if ($conn) { Find-Cs2LogLine '-> CSGO_GAME_UI_STATE_INGAME\s*$' $conn.Number -Last } else { $null }
            $after = if ($hit) { Find-Cs2LogLine 'CSGO_GAME_UI_STATE_INGAME ->' $hit.Number } else { $null }
            if ($after) { $hit = $null }   # it left the game state again (a map change, the addon reconnect)
            $gone = if ($conn) { Find-Cs2LogLine 'Disconnect(ing|ed) from server' $conn.Number } else { $null }
            if ($hit -and -not $gone) {
                if ($candidate -and $candidate.Number -eq $hit.Number -and ((Get-Date) - $candidate.At).TotalSeconds -ge 3 -and (Test-Cs2Frame $s $w)) {
                    Write-Host "ready (ingame): line $($conn.Number): $($conn.Line)"
                    Write-Host "                line $($hit.Number): $($hit.Line)"
                    return
                }
                if (-not $candidate -or $candidate.Number -ne $hit.Number) { $candidate = [pscustomobject]@{ Number = $hit.Number; At = Get-Date } }
            }
            else { $candidate = $null }
        }
        Start-Sleep -Milliseconds 500
    }
    throw "timed out after $TimeoutSec s waiting for '$WaitFor' (the session keeps running; 'cs2client.ps1 log' shows why, 'stop' ends it)"
}

# ---------------------------------------------------------------------------------------------------------------- reading

# Whether the game window shows a picture (a capture whose mean brightness is above near-black), not a loading-black frame.
function Test-Cs2Frame($s, $w) {
    $probe = Join-Path $s.runDir 'frame-check.png'
    $r = [Cs2Harness.Native]::CaptureWindow($s.desktop, $w.Handle, $probe, 320)
    Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
    return ($r -match 'mean=([\d.,]+)') -and ([double]($Matches[1] -replace ',', '.') -gt 8)
}

function Get-Cs2Window($s) {
    if (-not $s) { $s = Read-Cs2Session }
    if (-not $s) { return $null }
    Import-Cs2Native
    [Cs2Harness.Native]::WindowsOnDesktop($s.desktop) | Where-Object { $_.Pid -eq [uint32]$s.cs2Pid -and $_.Class -eq 'SDL_app' } | Select-Object -First 1
}

# The game's console log, read incrementally within one process (a map that spams can make it hundreds of MB). The game truncates
# it at every start; a shrink starts the cache over.
$script:LogCache = $null
function Read-Cs2LogLines {
    $path = (Get-Cs2Env).LogPath
    if (-not (Test-Path -LiteralPath $path)) {
        # After the session ended, its log is in the run folder.
        $s = Read-Cs2Session
        if ($s -and $s.runDir -and (Test-Path -LiteralPath (Join-Path $s.runDir 'console.log'))) { $path = Join-Path $s.runDir 'console.log' }
        else { return , ([Collections.Generic.List[string]]::new()) }
    }
    $c = $script:LogCache
    $len = (Get-Item -LiteralPath $path).Length
    if (-not $c -or $c.Path -ne $path -or $len -lt $c.Pos) { $c = $script:LogCache = [pscustomobject]@{ Path = $path; Pos = [long]0; Lines = [Collections.Generic.List[string]]::new(); Partial = '' } }
    if ($len -gt $c.Pos) {
        $fs = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
        try {
            [void]$fs.Seek($c.Pos, [IO.SeekOrigin]::Begin)
            $buf = New-Object byte[] ($len - $c.Pos)
            $read = 0
            while ($read -lt $buf.Length) { $n = $fs.Read($buf, $read, $buf.Length - $read); if ($n -le 0) { break }; $read += $n }
        }
        finally { $fs.Close() }
        $c.Pos += $read
        $text = $c.Partial + [Text.Encoding]::UTF8.GetString($buf, 0, $read)
        $parts = $text -split "`r?`n"
        $c.Partial = $parts[-1]                      # an unfinished last line waits for the rest
        for ($i = 0; $i -lt $parts.Count - 1; $i++) { $c.Lines.Add($parts[$i].TrimStart([char]0xFEFF)) }
    }
    , $c.Lines
}

function Find-Cs2LogLine([string]$Pattern, [int]$FromLine = 0, [switch]$Last) {
    $lines = Read-Cs2LogLines
    $hit = $null
    for ($i = [Math]::Max(0, $FromLine); $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $Pattern) { $hit = [pscustomobject]@{ Number = $i + 1; Line = $lines[$i] }; if (-not $Last) { break } }
    }
    $hit
}

function Get-Cs2Log([int]$Tail = 40, [string]$Pattern, [int]$FromLine = 0) {
    Update-Cs2Lease
    $lines = Read-Cs2LogLines
    $sel = for ($i = [Math]::Max(0, $FromLine); $i -lt $lines.Count; $i++) { if (-not $Pattern -or $lines[$i] -match $Pattern) { '{0,6}: {1}' -f ($i + 1), $lines[$i] } }
    $sel = @($sel)
    if ($Tail -gt 0 -and $sel.Count -gt $Tail) { $sel = $sel[($sel.Count - $Tail)..($sel.Count - 1)] }
    $sel
    "lines: $($lines.Count)"
}

function Wait-Cs2Log([string]$Pattern, [int]$TimeoutSec = 60, [int]$FromLine = 0) {
    Update-Cs2Lease
    $s = Read-Cs2Session
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        $hit = Find-Cs2LogLine $Pattern $FromLine
        if ($hit) { return "{0,6}: {1}" -f $hit.Number, $hit.Line }
        if ($s -and -not (Test-ProcessAlive $s.cs2Pid 'cs2' $s.cs2StartedUnix)) { throw "cs2 is not running (session $($s.state))" }
        Start-Sleep -Milliseconds 500
    }
    throw "timed out after $TimeoutSec s waiting for /$Pattern/"
}

# The run's own shots: a shot with -OutDir lands elsewhere, and the run keeps a line for it (shots-elsewhere.txt), so a run whose shots all
# went elsewhere says so at its end - a HUD release's client test is a run folder with its own shots\ (os2-deploy release publish --client-test).
function Add-Cs2ShotElsewhere($s, [string]$Path) {
    Add-Content -LiteralPath (Join-Path $s.runDir 'shots-elsewhere.txt') -Value "$((Get-Date).ToString('s'))`t$Path" -Encoding utf8
}

function Get-Cs2ShotCounts([string]$RunDir) {
    $own = Join-Path $RunDir 'shots'
    $list = Join-Path $RunDir 'shots-elsewhere.txt'
    $lines = @(if (Test-Path -LiteralPath $list) { Get-Content -LiteralPath $list -Encoding utf8 | Where-Object { $_ } })
    $over = Join-Path $RunDir 'shots-overlay.txt'
    [ordered]@{
        run       = if (Test-Path -LiteralPath $own) { @(Get-ChildItem -LiteralPath $own -Filter '*.png' -File).Count } else { 0 }
        elsewhere = $lines.Count
        dir       = if ($lines.Count) { Split-Path -Parent (($lines[0] -split "`t", 2)[-1]) } else { '' }
        overlay   = @(if (Test-Path -LiteralPath $over) { Get-Content -LiteralPath $over -Encoding utf8 | Where-Object { $_ } }).Count
    }
}

function Get-Cs2ShotWarning($Shots, [string]$RunDir) {
    if (-not $Shots) { return }
    if ($Shots.overlay -gt 0) {
        "WARNING: the Steam overlay was over the game in $($Shots.overlay) shot(s) of this run - none of those frames was written " +
        "($(Join-Path $RunDir 'shots-overlay.txt')). The frames that were written are clean"
    }
    if ($Shots.run -gt 0 -or $Shots.elsewhere -eq 0) { return }
    "WARNING: no shots\ in this run - its $($Shots.elsewhere) shot(s) went to -OutDir ($($Shots.dir)). A HUD release's client test is a run with its own shots\ " +
    "(os2-deploy release publish --client-test <run>): take them without -OutDir, or copy this run's own frames into $(Join-Path $RunDir 'shots') with a note of where they came from"
}

function Save-Cs2Screenshot([string]$Name, [int]$MaxWidth = 1920, [string]$OutDir) {
    Update-Cs2Lease
    $s = Read-Cs2Session
    if (-not $s -or $s.state -ne 'running') { throw 'no running session' }
    $w = Get-Cs2Window $s
    if (-not $w) { throw "no CS2 window on desktop $($s.desktop) (still booting?)" }
    if (-not $Name) { $Name = 'shot-' + (Get-Date -Format 'HHmmss') }
    if (-not $Name.EndsWith('.png')) { $Name += '.png' }
    $own = Join-Path $s.runDir 'shots'
    if (-not $OutDir) { $OutDir = $own }
    $path = Join-Path $OutDir $Name
    $r = [Cs2Harness.Native]::CaptureWindow($s.desktop, $w.Handle, $path, $MaxWidth)
    if ($r.StartsWith('overlay ')) {
        # Steam's overlay over the game shows the account: the frame is never written (Cs2Native's SteamOverlay), the run notes it, and the shot fails
        Add-Content -LiteralPath (Join-Path $s.runDir 'shots-overlay.txt') -Value "$((Get-Date).ToString('s'))`t$Name`t$($r.Substring(8))" -Encoding utf8
        throw "the Steam overlay is over the game (its Back to Game button at $($r.Substring(8))): the frame was not written - it shows the account. Stop the client and start it again: the overlay stays until then"
    }
    if (-not $r.StartsWith('ok')) { throw "capture failed: $r" }
    if ([IO.Path]::GetFullPath($OutDir).TrimEnd('\') -ne [IO.Path]::GetFullPath($own).TrimEnd('\')) { Add-Cs2ShotElsewhere $s $path }
    "$path  $r"
    if (($r -match 'mean=([\d.,]+)') -and [double]($Matches[1] -replace ',', '.') -le 3) { 'WARNING: a black frame - the game is loading a map (or quitting); take the shot again in a few seconds' }
}

function Get-Cs2ClientStatus([switch]$Brief) {
    $s = Read-Cs2Session
    $lock = Get-Cs2ServerLock
    if (-not $s) { "no session"; if ($lock) { "server lock: HELD - $($lock -split "`n" | Select-Object -First 1)" }; return }
    if ($s.state -eq 'done') {
        $r = Read-Json (Join-Path $s.runDir 'result.json')
        "last session $($s.id) '$($s.purpose)': ended $(if ($r) { "$($r.ended) - $($r.reason)" })"
        if ($r) {
            "loose HUD files: $($r.hudLocal)"
            "settings: $(@($r.settings | Where-Object { $_.state -notin 'unchanged', 'rewritten identically' } | ForEach-Object { "$($_.file) $($_.state)" }) -join '; ')"
            if ($r.guard) { "guard: $($r.guard.samples) audio samples, max peak $($r.guard.maxPeak), foreground ours $($r.guard.fgOurs) of $($r.guard.fgSamples)" }
        }
        "run dir: $($s.runDir)"
        if ($r) { Get-Cs2ShotWarning $r.shots $s.runDir }
        if ($lock) { "server lock: HELD - $($lock -split "`n" | Select-Object -First 1)" } else { 'server lock: free' }
        $live = @(Get-Cs2Processes)
        "cs2.exe running now: $(if ($live.Count) { "pid $($live.Pid -join ', ') (not a harness session)" } else { 'none' })"
        return
    }
    Update-Cs2Lease
    Import-Cs2Native
    $alive = Test-ProcessAlive $s.cs2Pid 'cs2' $s.cs2StartedUnix
    $wd = Test-ProcessAlive $s.watchdogPid 'pwsh'
    $lease = Read-Json (Join-Path $s.runDir 'lease.json')
    $guard = Read-Json (Join-Path $s.runDir 'guard.json')
    "session $($s.id) '$($s.purpose)': state $($s.state), cs2 pid $($s.cs2Pid) $(if ($alive) { 'running' } else { 'not running' }), watchdog $(if ($wd) { 'alive' } else { 'NOT RUNNING' })"
    if ($s.connect) { "server: $($s.connect)" }
    if ($lease) { "lease until $(Format-Unix $lease.leaseUntil), hard stop at $(Format-Unix $s.maxUntil)" }
    if ($guard) { "guard: $($guard.samples) audio samples, max peak $($guard.maxPeak), foreground samples $($guard.fgSamples) (ours $($guard.fgOurs)), last $($guard.at)" }
    if ($alive) {
        $w = Get-Cs2Window $s
        if ($w) { "window: $($w.ToString())" } else { 'window: none yet' }
        $last = Find-Cs2LogLine 'ChangeGameUIState|Received S2C_CONNECTION|Disconnected from server|Map: "' -Last
        if ($last) { "last state line ($($last.Number)): $($last.Line)" }
    }
    if (-not $Brief) {
        "run dir: $($s.runDir)"
        if ($lock) { "server lock: HELD - $($lock -split "`n" | Select-Object -First 1)" } else { 'server lock: free' }
        $r = Read-Json (Join-Path $s.runDir 'result.json')
        if ($r) { "result: $($r.reason); settings: $(@($r.settings | Where-Object { $_.state -ne 'unchanged' } | ForEach-Object { "$($_.file) $($_.state)" }) -join '; ')" }
    }
}

# ---------------------------------------------------------------------------------------------------------------- stop and clean up

function Stop-Cs2Process($s, [string]$Reason, [int]$GraceSec = 30) {
    Import-Cs2Native
    # The loose HUD files go first, before the quit is asked: a quit can take many seconds (a map load let finish below, Source2Shutdown, the
    # 20-30 s grace), and nothing of the HUD under test may outlive the decision to end the session (README.md, "VAC safety").
    $loose = if (Test-Path -LiteralPath (Get-HudLocalMarker)) { "loose HUD files first: $(Remove-Cs2HudLocal); " } else { '' }
    if (-not (Test-ProcessAlive $s.cs2Pid 'cs2' $s.cs2StartedUnix)) { return "${loose}not running" }
    if (-not (Test-Path -LiteralPath (Join-Path $s.runDir 'stop.json'))) { Write-Json (Join-Path $s.runDir 'stop.json') @{ reason = $Reason; at = (Get-Date).ToString('s'); by = $PID } }
    # A quit that lands while a map is loading (SwitchToLoop game ... until its Signon traffic) hangs after Source2Shutdown
    # (measured twice); one before or after that load ends in about a second. So a load in progress is let finish first.
    $settle = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $settle -and (Test-ProcessAlive $s.cs2Pid 'cs2' $s.cs2StartedUnix)) {
        $load = Find-Cs2LogLine 'SwitchToLoop game requested' -Last
        if (-not $load -or (Find-Cs2LogLine 'Signon traffic "server"|Disconnect(ing|ed) from server' $load.Number)) { break }
        Start-Sleep -Milliseconds 250
    }
    $w = Get-Cs2Window $s
    $how = 'WM_CLOSE'
    if ($w) { $r = [Cs2Harness.Native]::PostClose($s.desktop, $w.Handle); if ($r -ne 'ok') { $how = "WM_CLOSE failed ($r)" } } else { $how = 'no window' }
    $deadline = (Get-Date).AddSeconds($GraceSec)
    while ((Get-Date) -lt $deadline -and (Test-ProcessAlive $s.cs2Pid 'cs2' $s.cs2StartedUnix)) { Start-Sleep -Milliseconds 250 }
    if (Test-ProcessAlive $s.cs2Pid 'cs2' $s.cs2StartedUnix) {
        Stop-Process -Id $s.cs2Pid -Force -ErrorAction SilentlyContinue   # OUR process (pid and start time checked)
        return "$loose$how did not end it within $GraceSec s - terminated"
    }
    "$loose$how - quit cleanly"
}

# The end of every session, whoever ends it (the stop verb, the watchdog, or the next start finding a dead session). Idempotent.
function Invoke-Cs2PostExit($s, [string]$Why) {
    if (-not $s -or $s.state -eq 'done') { return }
    $plock = Join-Path $script:StateDir 'postexit.lock'
    try { $fs = [IO.File]::Open($plock, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None); $fs.Close() }
    catch { if (((Get-Date) - (Get-Item -LiteralPath $plock).LastWriteTime).TotalSeconds -lt 120) { return }; Set-Content -LiteralPath $plock -Value $PID }
    try {
        $Env = Get-Cs2Env
        $stop = Read-Json (Join-Path $s.runDir 'stop.json')
        $reason = if ($stop) { $stop.reason } elseif ($Why) { $Why } else { 'cs2 exited' }
        if (Test-Path -LiteralPath $Env.LogPath) {
            # The session's log moves to the run folder; nothing of the harness stays in the game folder.
            $copy = Join-Path $s.runDir 'console.log'
            Copy-Item -LiteralPath $Env.LogPath -Destination $copy -Force -ErrorAction SilentlyContinue
            if ((Test-Path -LiteralPath $copy) -and (Get-Item -LiteralPath $copy).Length -ge (Get-Item -LiteralPath $Env.LogPath).Length) { Remove-Item -LiteralPath $Env.LogPath -Force -ErrorAction SilentlyContinue }
        }
        Remove-Item -LiteralPath (Join-Path $Env.CfgDir $script:BootCfg) -Force -ErrorAction SilentlyContinue
        $hud = if (Test-Path -LiteralPath (Get-HudLocalMarker)) { Remove-Cs2HudLocal } elseif ($s.hudLocal) { 'removed before the quit (watchdog.log, stop)' } else { 'none installed' }
        if ($s.hudLocal) { $hud = "$hud; $(Clear-Cs2HudStage $Env)" }
        $settings = @(Restore-Cs2Settings $Env (Join-Path $s.runDir 'backup'))
        $guard = Read-Json (Join-Path $s.runDir 'guard.json')
        $result = [ordered]@{ session = $s.id; purpose = $s.purpose; reason = $reason; ended = (Get-Date).ToString('s'); hudLocal = $hud; guard = $guard; settings = $settings
                              shots = (Get-Cs2ShotCounts $s.runDir) }
        Write-Json (Join-Path $s.runDir 'result.json') $result
        $s.state = 'done'
        Write-Cs2Session $s
        Exit-Cs2ClientLock
    }
    finally { Remove-Item -LiteralPath $plock -Force -ErrorAction SilentlyContinue }
}

function Stop-Cs2Client([string]$Reason = 'stop requested', [int]$GraceSec = 30) {
    $s = Read-Cs2Session
    if (-not $s -or $s.state -eq 'done') { return 'no running session' }
    $how = Stop-Cs2Process $s $Reason $GraceSec
    # The watchdog sees the exit and runs the clean-up; do it here when it is gone or slow.
    $deadline = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $deadline) {
        $s2 = Read-Cs2Session
        if ($s2.state -eq 'done') { break }
        if (-not (Test-ProcessAlive $s.watchdogPid 'pwsh')) { Invoke-Cs2PostExit $s2 $Reason; break }
        Start-Sleep -Milliseconds 500
    }
    $s2 = Read-Cs2Session
    if ($s2.state -ne 'done') { Invoke-Cs2PostExit $s2 $Reason }
    "cs2: $how"
    $r = Read-Json (Join-Path $s.runDir 'result.json')
    if ($r) {
        "loose HUD files: $($r.hudLocal)"
        foreach ($x in @($r.settings)) { if ($x.state -ne 'unchanged') { "settings: [$($x.set)] $($x.file): $($x.state)$(if ($x.diff) { " ($($x.diff))" })" } }
        if ($r.guard) { "guard: $($r.guard.samples) audio samples, max peak $($r.guard.maxPeak), foreground ours $($r.guard.fgOurs) of $($r.guard.fgSamples); events: $(@($r.guard.events) -join '; ')" }
        Get-Cs2ShotWarning $r.shots $s.runDir
    }
    "run dir: $($s.runDir)"
}

# Anything an earlier session left behind (a crash, a killed watchdog): its clean-up runs now.
function Invoke-Cs2Leftovers($Env) {
    $s = Read-Cs2Session
    if ($s -and $s.state -ne 'done' -and -not (Test-Cs2SessionLive $s)) { Invoke-Cs2PostExit $s 'found dead at the next start'; "session $($s.id) cleaned up" }
    if (Test-Path -LiteralPath (Get-HudLocalMarker)) { Remove-Cs2HudLocal }
    if (Test-Path -LiteralPath (Get-Cs2HudStage $Env)) { Clear-Cs2HudStage $Env }
    $boot = Join-Path $Env.CfgDir $script:BootCfg
    if (Test-Path -LiteralPath $boot) { Remove-Item -LiteralPath $boot -Force; "removed a stale $script:BootCfg" }
}

# ---------------------------------------------------------------------------------------------------------------- the drill

# A dry proof of the guards around the loose HUD files, run entirely in a folder of its own under TEMP - never the game's folder, never Steam's
# log, no CS2 started - with the harness's own functions on a stand-in CS2 folder: the build's file list (empty files under the build's own
# names and count - the real build's when one is named, else a made-up 9242), staged and enabled as a start does it (Install-Cs2HudLocal,
# Enable-Cs2HudLocal), a launch of CS2 appended to a stand-in Steam log, what the watchdog runs every 100 ms on it (Find-Cs2SteamLaunch) and
# then the removal (Remove-Cs2HudLocal), each timed; the start's refusal on the same log (Test-Cs2SteamLaunching); then the real game
# folder's scan for loose files (the self-check's).
function Invoke-Cs2Drill([string]$HudAddon = '', [switch]$IncludeThumbs) {
    $real = Get-Cs2Env
    $root = Join-Path ([IO.Path]::GetTempPath()) "cs2harness-drill-$PID"
    $saved = $script:StateDir
    $out = [Collections.Generic.List[string]]::new()
    $ok = $false
    try {
        $fake = [pscustomobject]@{ Cs2Root = $root; GameDir = (Join-Path $root 'game\csgo'); SteamConsoleLog = (Join-Path $root 'steam\logs\console_log.txt'); HudBuildDir = '' }
        $build = Join-Path $root 'game\csgo_addons\drillbuild'
        New-Item -ItemType Directory -Force -Path $fake.GameDir, (Join-Path $root 'state'), (Split-Path -Parent $fake.SteamConsoleLog), $build | Out-Null
        # a stock game\csgo\panorama, as Steam installs it: Valve's fonts and videos, which the drill must leave exactly as they are
        $stock = Join-Path $fake.GameDir 'panorama\fonts\fonts.conf'
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $stock), (Join-Path $fake.GameDir 'panorama\videos') | Out-Null
        [IO.File]::WriteAllText($stock, 'stock')
        $rels = @()
        if ($HudAddon) {
            $src = Join-Path $real.Cs2Root "game\csgo_addons\$HudAddon"
            $rels = @(Get-ChildItem -LiteralPath (Join-Path $src 'panorama') -Recurse -File -ErrorAction Stop | Where-Object { $_.Name -like '*_c' } | ForEach-Object { $_.FullName.Substring($src.Length).TrimStart('\') })
        }
        if (-not $rels) { $rels = @(1..9242 | ForEach-Object { "panorama\images\drill\d$($_ % 40)\f$_.vtex_c" }) }
        foreach ($r in $rels) {
            $to = Join-Path $build $r
            if (-not $to.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "drill path outside its folder: $to" }
            $d = Split-Path -Parent $to
            if (-not [IO.Directory]::Exists($d)) { [void][IO.Directory]::CreateDirectory($d) }
            [IO.File]::WriteAllBytes($to, [byte[]]@())
        }
        Set-Cs2StateDir (Join-Path $root 'state')
        $hud = Install-Cs2HudLocal $fake $build -IncludeThumbs:$IncludeThumbs
        $out.Add("staged: $(@($hud.Files).Count) file(s) of $(if ($HudAddon) { "build $HudAddon's list$(if ($IncludeThumbs) { ' with its thumbnails' })" } else { 'a made-up list' }) in the stand-in stage; in the stand-in game folder before the renames: $(if (@($hud.Moved | Where-Object { Test-Path -LiteralPath $_ }).Count) { 'SOMETHING' } else { 'nothing' }) (its top folders: $($hud.Tops -join ', '))")

        # Steam's log as it was, the start's last look on it, the one rename into game\csgo\panorama.
        [IO.File]::WriteAllText($fake.SteamConsoleLog, "[$((Get-Date).AddMinutes(-5).ToString('yyyy-MM-dd HH:mm:ss'))] Game process removed: AppID 730 `"cs2.exe -con_logfile cs2harness_console.log`"`r`n")
        $pos = [long](Get-Item -LiteralPath $fake.SteamConsoleLog).Length
        $quiet = Find-Cs2SteamLaunch (Read-Cs2NewText $fake.SteamConsoleLog ([ref]$pos))
        $sw = [Diagnostics.Stopwatch]::StartNew()
        Enable-Cs2HudLocal $hud
        $tIn = $sw.Elapsed.TotalMilliseconds
        $inPlace = @($hud.Files | Where-Object { [IO.File]::Exists($_) }).Count
        $out.Add(("before the Play the watchdog's look finds {0}; enabled: {1} file(s) in the stand-in game\csgo\panorama, the rename took {2:0.0} ms" -f $(if ($quiet) { $quiet } else { 'nothing' }), $inPlace, $tIn))

        # The user's Play: Steam's first line of a launch, seconds before that game's process exists.
        $sw.Restart()
        [IO.File]::AppendAllText($fake.SteamConsoleLog, "[$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))] GameAction [AppID 730, ActionID 99] : LaunchApp changed task to ProcessingInstallScript with `"`"`r`n")
        $launch = Find-Cs2SteamLaunch (Read-Cs2NewText $fake.SteamConsoleLog ([ref]$pos))
        $tSeen = $sw.Elapsed.TotalMilliseconds
        $removal = Remove-Cs2HudLocal
        $tGone = $sw.Elapsed.TotalMilliseconds
        $left = @($hud.Files | Where-Object { [IO.File]::Exists($_) }).Count
        $out.Add("the Play's line, as the watchdog reads it: $launch")
        $out.Add(("seen {0:0.0} ms after it was written (the watchdog looks every 100 ms: up to 100 ms more); {1}; out of the game folder {2:0.0} ms after the line; files left there: {3}; its folders left: {4}; marker left: {5}; Valve's stock panorama files untouched: {6}" -f $tSeen, $removal, $tGone, $left, @($hud.Moved | Where-Object { Test-Path -LiteralPath $_ }).Count, (Test-Path -LiteralPath (Get-HudLocalMarker)), ((Test-Path -LiteralPath $stock) -and (Test-Path -LiteralPath (Join-Path $fake.GameDir 'panorama\videos')))))
        $out.Add("the stage afterwards (outside the game's search paths): $(Clear-Cs2HudStage $fake)")
        $refusal = Test-Cs2SteamLaunching $fake -SteamState ([pscustomobject]@{ RunningAppId = 0 })
        $out.Add("a start now would be refused: $(if ($refusal) { $refusal } else { 'NO - not refused' })")
        $ok = [bool]$launch -and $left -eq 0 -and -not @($hud.Moved | Where-Object { Test-Path -LiteralPath $_ }).Count -and -not (Test-Path -LiteralPath (Get-HudLocalMarker)) -and
            [bool]$refusal -and -not $quiet -and $inPlace -eq @($hud.Files).Count -and (Test-Path -LiteralPath $stock)
    }
    finally {
        Set-Cs2StateDir $saved
        Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    }
    $out.Add("the drill's folder removed: $(-not (Test-Path -LiteralPath $root))")
    Import-Cs2Native
    $check = Test-Cs2Integrity $real @('-steam')
    $loose = @($check.Violations | Where-Object { $_ -like 'loose game file*' })
    $out.Add("the real game folder: $(if ($loose.Count) { "$($loose.Count) loose file(s): $($loose | Select-Object -First 3)" } else { 'no loose game file (the self-check''s own scan of game\csgo)' }); game\csgo\panorama holds $(((Get-ChildItem -LiteralPath (Join-Path $real.GameDir 'panorama') -Directory -ErrorAction SilentlyContinue | ForEach-Object Name) -join ', ')) (Valve's own: fonts, videos); the harness's stage: $(if (Test-Path -LiteralPath (Get-Cs2HudStage $real)) { 'exists' } else { 'absent' })")
    $now = Test-Cs2SteamLaunching $real
    $out.Add("Steam now: $(if ($now) { $now } else { 'not launching CS2' })")
    $out.Add($(if ($ok -and -not $loose.Count) { 'DRILL PASSED' } else { 'DRILL FAILED' }))
    $out
}

Export-ModuleMember -Function *-Cs2*, Get-UnixNow, Format-Unix, Read-Json, Write-Json, Test-ProcessAlive, Get-SteamState, Invoke-Cs2Leftovers, Get-HudLocalMarker
