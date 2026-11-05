# The HUD's Workshop item, through the Steam client that is running and logged in on this PC (the publisher never sees a password).
#
# NEVER another HUD Workshop item, under any name: Valve holds new items for moderation, and near-identical new items in place of an update are what
# it frowns on. There is one HUD item, 3823993566, named in workshop_id.txt, and a release - only with the user's go for it - is a
# content update of that item. Every update waits in Valve's moderation queue. This script and the publisher have no create path: an
# empty or missing workshop_id.txt is refused, and an upload needs -UserApprovedUpdate.
#
#   powershell -File hud\publish.ps1 -Public   # is the item shown to everybody (moderation)?
#   powershell -File ...\publish.ps1 -Query                                        # the item as Steam has it: title, visibility, sizes
#   powershell -File ...\publish.ps1 -Note "What changed" -UserApprovedUpdate      # ONLY with the user's go: a content update (build.ps1 first)
#
# publisher\bin\publish\publisher.exe also takes list, visibility, delete and unsubscribe (not guarded: none of them uploads).

param(
    [string]$Cs2 = "",
    [string]$Addon = "gungame_hud",
    [string]$Note = "",
    [switch]$Query,
    [switch]$Public,
    [switch]$UserApprovedUpdate
)

$ErrorActionPreference = "Stop"
$src = Split-Path -Parent $MyInvocation.MyCommand.Path
$idFile = Join-Path $src "workshop_id.txt"

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
$tool = Join-Path $src "publisher"
$out = Join-Path $tool "bin\publish"
dotnet build (Join-Path $tool "publisher.csproj") -c Release -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { exit 1 }
Copy-Item (Join-Path $Cs2 "game\bin\win64\steam_api64.dll") $out -Force
$exe = Join-Path $out "publisher.exe"

if ((Get-Process steam -ErrorAction SilentlyContinue) -eq $null) { Write-Output "Start Steam and log in first."; exit 1 }

if ($Query) {
    & $exe query ((Get-Content $idFile -Raw).Trim())
    exit $LASTEXITCODE
}
if ($Public) {
    & $exe public ((Get-Content $idFile -Raw).Trim())
    exit $LASTEXITCODE
}

$built = Join-Path $Cs2 "game\csgo_addons\$Addon"
if (-not (Test-Path (Join-Path $built "panorama\layout\custom_game\gungame_hud_*.vxml_c"))) { Write-Output "Not built: run build.ps1 first."; exit 1 }
if (-not $Note) { Write-Output "Pass -Note `"what changed`" - Steam shows it in the item's change notes."; exit 1 }
$item = if (Test-Path $idFile) { (Get-Content $idFile -Raw).Trim() } else { "" }
if ($item -notmatch '^\d+$') {
    Write-Output "REFUSED: workshop_id.txt names no item. There is no create path - a release is a content update of the one HUD item, 3823993566 (AGENTS.md, 'cs2 in-game HUD - NEVER create another Workshop item')."
    exit 3
}

$publishArgs = @("publish", $built, $idFile, (Join-Path $out "content"), (Join-Path $src "workshop_preview.jpg"), $Note)
if ($UserApprovedUpdate) { $publishArgs += "--user-approved-update" }
& $exe @publishArgs
exit $LASTEXITCODE
