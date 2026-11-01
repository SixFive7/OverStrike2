# The server's own CS2 maps on the Steam Workshop (maps\<map>\), through the Steam client that is running and logged in
# on this PC (the publisher never sees a password). Program.cs beside this file says what a map item holds and why.
#
#   powershell -File maps\publisher\publish.ps1 -Map gg_glasspire -Pack                     # the content folder only, no Steam
#   ... -Map gg_glasspire -Create -UserApprovedCreate                                                      # ONLY with the owner's go: a NEW public item
#   ... -Map gg_glasspire -Update -Note "What changed" -UserApprovedUpdate                                 # ONLY with the owner's go: a new version
#   ... -Map gg_glasspire -Query | -Public | -Watch                                                        # read-only: Steam's view, a visitor's view, the moderation watcher
#   ... -List                                                                                              # every item of the account, read-only
#
# A map folder holds workshop.json (title, description, tags, map) and, once the item exists, workshop_id.txt - written by -Create
# the moment Steam has made the item, so a failed upload is retried with -Update and never makes a second item.
# -Preview defaults to maps\<map>\workshop_preview.jpg (1280x720, under 1 MB); -Update without -Preview keeps the item's.
# -Addon defaults to <CS2>\game\csgo_addons\<map>, where the Workshop Tools compile the map.

param(
    [string]$Map = "",
    [string]$Cs2 = "",
    [string]$Addon = "",
    [string]$Preview = "",
    [string]$Note = "",
    [int]$Seconds = 60,
    [int]$Minutes = 240,
    [switch]$Pack,
    [switch]$Create,
    [switch]$Update,
    [switch]$Query,
    [switch]$Public,
    [switch]$Watch,
    [switch]$List,
    [switch]$UserApprovedCreate,
    [switch]$UserApprovedUpdate
)

$ErrorActionPreference = "Stop"
$tool = Split-Path -Parent $MyInvocation.MyCommand.Path
$maps = Split-Path -Parent $tool

if (-not $Cs2) {
    $roots = @("C:\Program Files (x86)\Steam")
    $vdf = "C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf"
    if (Test-Path $vdf) {
        foreach ($line in (Select-String -Path $vdf -Pattern '"path"\s+"(.+?)"' -AllMatches)) {
            foreach ($m in $line.Matches) { $roots += $m.Groups[1].Value.Replace('\\', '\') }
        }
    }
    foreach ($r in $roots) {
        $candidate = Join-Path $r "steamapps\common\Counter-Strike Global Offensive"
        if (Test-Path $candidate) { $Cs2 = $candidate; break }
    }
}
if (-not $Cs2 -or -not (Test-Path $Cs2)) { Write-Output "Counter-Strike 2 not found. Pass -Cs2 <path to 'Counter-Strike Global Offensive'>"; exit 1 }

# The publisher binds CS2's own steam_api64.dll (its flat API has the ISteamUGC version the code calls).
$out = Join-Path $tool "bin\publish"
dotnet build (Join-Path $tool "publisher.csproj") -c Release -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { exit 1 }
Copy-Item (Join-Path $Cs2 "game\bin\win64\steam_api64.dll") $out -Force
$exe = Join-Path $out "mappublisher.exe"

if ($List) { & $exe list; exit $LASTEXITCODE }
if (-not $Map) { Write-Output "Pass -Map <name of a folder in maps>."; exit 2 }
$dir = Join-Path $maps $Map
if (-not (Test-Path (Join-Path $dir "workshop.json"))) { Write-Output "No $dir\workshop.json."; exit 2 }
$idFile = Join-Path $dir "workshop_id.txt"
$id = if (Test-Path $idFile) { (Get-Content $idFile -Raw).Trim() } else { "" }
if (-not $Addon) { $Addon = Join-Path $Cs2 "game\csgo_addons\$Map" }
$staging = Join-Path $out "content-$Map"

if ($Query -or $Public -or $Watch) {
    if ($id -notmatch '^\d+$') { Write-Output "$idFile names no item yet."; exit 2 }
    if ($Query) { & $exe query $id }
    elseif ($Public) { & $exe public $id }
    else { & $exe watch $id $Seconds $Minutes }
    exit $LASTEXITCODE
}
if ($Pack) { & $exe pack $dir $Addon $staging $(if ($id -match '^\d+$') { $id } else { "0" }); exit $LASTEXITCODE }
if ($Create) {
    if (-not $Preview) { $Preview = Join-Path $dir "workshop_preview.jpg" }
    if ((Get-Process steam -ErrorAction SilentlyContinue) -eq $null) { Write-Output "Start Steam and log in first."; exit 1 }
    $createArgs = @("create", $dir, $Addon, $staging, $Preview)
    if ($UserApprovedCreate) { $createArgs += "--user-approved-create" }
    & $exe @createArgs
    exit $LASTEXITCODE
}
if ($Update) {
    if ((Get-Process steam -ErrorAction SilentlyContinue) -eq $null) { Write-Output "Start Steam and log in first."; exit 1 }
    $updateArgs = @("update", $dir, $Addon, $staging, $(if ($Preview) { $Preview } else { "-" }), $Note)
    if ($UserApprovedUpdate) { $updateArgs += "--user-approved-update" }
    & $exe @updateArgs
    exit $LASTEXITCODE
}
Write-Output "Pass one of -Pack, -Create, -Update, -Query, -Public, -Watch or -List."
exit 2
