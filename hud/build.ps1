# Builds the gungame_hud Workshop addon: the in-game HUD of the Over Strike 2 server (docs/reference.md, "In-game HUD").
#
#   powershell -File hud\build.ps1          # finds CS2 through Steam's library folders
#   powershell -File ...\build.ps1 -Cs2 "<path to 'Counter-Strike Global Offensive'>"
#   powershell -File ...\build.ps1 -Local                                    # also copy the result into your own client, to try it before publishing
#   powershell -File ...\build.ps1 -MarkShipped                              # after a publish: this build's pictures are the shipped set now
#
# The pictures guard (2026-10-24): every picture of a map the last release shipped must be the very JPEG it shipped (thumbs-shipped.txt:
# id, sha1, bytes), unless that map is listed in thumbs-regenerated.txt - regenerated on purpose, one id per line with // and why. So a
# stale or remade picture cannot slip into a release unseen (on 2026-10-22 a stale hud/thumbs folder nearly put the black bars back on
# 196 maps). Pictures of maps not in thumbs-shipped.txt are new and pass. After the publish, -MarkShipped rewrites thumbs-shipped.txt
# from hud/thumbs; commit it with the release.
#
# Copies layout\, styles\, images\ and the map thumbnails (thumbs\, made by thumbs.py) into content\csgo_addons\gungame_hud\ and
# compiles them with the Workshop Tools' resource compiler into game\csgo_addons\gungame_hud\ - the folder publish.ps1 uploads
# (PUBLISH.md). Compiling gungame_hud_thumbs.css builds every thumbnail it names, about a thousand small JPEG textures.
#
# Adapted from CS2UIKit's kit\build.ps1 (MIT, github.com/nvmxre/cs2-ui-kit), including its two hard-won details: fresh
# timestamps plus -f, because the compiler otherwise answers "skipped" and leaves an old layout in the addon while reporting
# success; and BGRA8888 in the .vtex, because DXT5 comes out as a pink picture on blue.

param(
    [string]$Cs2 = "",
    [string]$Addon = "gungame_hud",
    [switch]$Local,
    [switch]$NoThumbs,  # a text-only build: no map pictures, no picture stylesheet and no include of it - the fallback if pictures break the layout
    [switch]$MarkShipped # after a publish: write thumbs-shipped.txt from hud/thumbs (the pictures that release shipped), build nothing
)

$ErrorActionPreference = "Stop"
$src = Split-Path -Parent $MyInvocation.MyCommand.Path
$thumbs = Join-Path $src "thumbs"
$shippedList = Join-Path $src "thumbs-shipped.txt"
$regenList = Join-Path $src "thumbs-regenerated.txt"

if ($MarkShipped) {
    $lines = @("# The map pictures the last HUD release shipped: one per line, Workshop id, sha1 and bytes of hud/thumbs/<id>.jpg. build.ps1 refuses",
        "# a build whose pictures differ from these unless the map is listed in thumbs-regenerated.txt (regenerated on purpose); a picture",
        "# of a map not listed here is new. Written by build.ps1 -MarkShipped after the publish of $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')).")
    foreach ($f in (Get-ChildItem $thumbs -Filter *.jpg | Sort-Object { [long]$_.BaseName })) {
        $lines += "{0}`t{1}`t{2}" -f $f.BaseName, (Get-FileHash -Algorithm SHA1 $f.FullName).Hash.ToLowerInvariant(), $f.Length
    }
    [System.IO.File]::WriteAllText($shippedList, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
    Write-Output "Wrote $shippedList ($($lines.Count - 3) pictures). Commit it with the release; empty thumbs-regenerated.txt, those are shipped now."
    exit 0
}

if (-not $NoThumbs -and -not (Test-Path (Join-Path $thumbs "*.jpg"))) { Write-Output "No map thumbnails in $thumbs - run: python $(Join-Path $src 'thumbs.py')"; exit 1 }

# The pictures guard (see the top of this file): the last release's pictures, unchanged unless regenerated on purpose.
if (-not $NoThumbs) {
    if (-not (Test-Path $shippedList)) { Write-Output "REFUSED: no $shippedList - the pictures cannot be checked against the last release"; exit 1 }
    $shipped = @{}
    foreach ($line in [System.IO.File]::ReadAllLines($shippedList)) { if ($line -match '^(\d+)\t([0-9a-f]{40})\t\d+$') { $shipped[$Matches[1]] = $Matches[2] } }
    $regen = @{}
    if (Test-Path $regenList) {
        foreach ($line in [System.IO.File]::ReadAllLines($regenList)) { $id = ($line -split '//')[0].Trim(); if ($id -match '^\d+$') { $regen[$id] = $true } }
    }
    $gone = @(); $changed = @(); $kept = 0; $remade = 0
    foreach ($id in $shipped.Keys) {
        $f = Join-Path $thumbs "$id.jpg"
        if (-not (Test-Path $f)) { $gone += $id; continue }
        $h = (Get-FileHash -Algorithm SHA1 $f).Hash.ToLowerInvariant()
        if ($h -eq $shipped[$id]) { $kept++ } elseif ($regen.ContainsKey($id)) { $remade++ } else { $changed += $id }
    }
    $new = @(Get-ChildItem $thumbs -Filter *.jpg | Where-Object { -not $shipped.ContainsKey($_.BaseName) }).Count
    if ($gone.Count -gt 0) {
        Write-Output "REFUSED: $($gone.Count) picture(s) the last release shipped are not in $thumbs (first: $(($gone | Select-Object -First 5) -join ', ')) - copy the shipped set back first (README, Building and publishing the HUD addon)"
        exit 1
    }
    if ($changed.Count -gt 0) {
        Write-Output "REFUSED: $($changed.Count) picture(s) differ from what the last release shipped (first: $(($changed | Select-Object -First 5) -join ', ')) - a stale or remade JPEG. Copy the shipped ones back, or list the maps in thumbs-regenerated.txt if they were regenerated on purpose"
        exit 1
    }
    Write-Output "OK      pictures: $kept as the last release shipped them, $new new, $remade regenerated on purpose ($(Split-Path -Leaf $regenList))"
}

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
Write-Output "CS2: $Cs2"

$compiler = Join-Path $Cs2 "game\bin\win64\resourcecompiler.exe"
if (-not (Test-Path $compiler)) {
    Write-Output ""
    Write-Output "The Counter-Strike 2 Workshop Tools are not installed: $compiler is missing."
    Write-Output "They are not in Steam's tools list. Install them from inside the game:"
    Write-Output "  1. Launch CS2"
    Write-Output "  2. Settings -> search 'Install Counter-Strike Workshop Tools' -> Yes"
    Write-Output "  3. Quit the game and let Steam download them (a content\ folder appears next to game\)"
    exit 1
}

$content = Join-Path $Cs2 "content\csgo_addons\$Addon"
$game = Join-Path $Cs2 "game\csgo_addons\$Addon"
$layoutDir = Join-Path $content "panorama\layout\custom_game"
$styleDir = Join-Path $content "panorama\styles\custom_game"
$imageDir = Join-Path $styleDir $Addon
$thumbDir = Join-Path $content "panorama\images\custom_game\$Addon\thumbs"
New-Item -ItemType Directory -Force -Path $layoutDir, $styleDir, $imageDir, $thumbDir, $game | Out-Null




# Only this release's layout and stylesheets: an earlier release's files (other names, Hud.Release) would be compiled into the addon too.
Remove-Item (Join-Path $layoutDir "*.xml"), (Join-Path $styleDir "*.css") -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $thumbDir "*") -Force -ErrorAction SilentlyContinue
if ($NoThumbs) {
    foreach ($f in Get-ChildItem (Join-Path $src "layout") -Filter *.xml) {
        $lines = [System.IO.File]::ReadAllLines($f.FullName) | Where-Object { $_ -notmatch 'gungame_hud_thumbs_' }
        [System.IO.File]::WriteAllLines((Join-Path $layoutDir $f.Name), [string[]]$lines, (New-Object System.Text.UTF8Encoding($false)))
    }
    Get-ChildItem (Join-Path $src "styles") -Filter *.css | Where-Object { $_.Name -notlike "gungame_hud_thumbs_*" } | Copy-Item -Destination $styleDir -Force
} else {
    Copy-Item (Join-Path $src "layout\*.xml") $layoutDir -Force
    Copy-Item (Join-Path $src "styles\*.css") $styleDir -Force
    Copy-Item (Join-Path $thumbs "*.jpg") $thumbDir -Force
}
if (-not (Test-Path (Join-Path $src "images\knife.png"))) { Write-Output "hud\images\knife.png is missing: python hud\knife.py draws it."; exit 1 }
Copy-Item (Join-Path $src "images\*") $imageDir -Force
Copy-Item (Join-Path $src "addoninfo.txt") $game -Force
Write-Output "Sources copied into $content"

# Fresh timestamps and no old output: the compiler compares both and answers "skipped" otherwise.
Get-ChildItem $layoutDir, $styleDir, $imageDir, $thumbDir -File | ForEach-Object { $_.LastWriteTime = Get-Date }
Remove-Item (Join-Path $game "panorama") -Recurse -Force -ErrorAction SilentlyContinue

& $compiler -i (Join-Path $imageDir "*.vtex") -f -r
foreach ($f in Get-ChildItem $layoutDir -Filter *.xml) { & $compiler -i $f.FullName -f -r }
foreach ($f in Get-ChildItem $styleDir -Filter *.css) { & $compiler -i $f.FullName -f -r }

$expected = @(Get-ChildItem (Join-Path $src "layout") -Filter *.xml | ForEach-Object { "panorama\layout\custom_game\$($_.BaseName).vxml_c" }) +
    @(Get-ChildItem (Join-Path $src "styles") -Filter *.css | Where-Object { -not ($NoThumbs -and $_.Name -like "gungame_hud_thumbs_*") } | ForEach-Object { "panorama\styles\custom_game\$($_.BaseName).vcss_c" }) +
    @("panorama\styles\custom_game\$Addon\knife.vtex_c")
$missing = 0
foreach ($rel in $expected) {
    $f = Join-Path $game $rel
    if (Test-Path $f) { Write-Output ("OK      {0} ({1} bytes)" -f $rel, (Get-Item $f).Length) }
    else { Write-Output "MISSING $rel - read the compiler output above"; $missing++ }
}
$want = if ($NoThumbs) { 0 } else { (Get-ChildItem $thumbs -Filter *.jpg).Count }
$built = @(Get-ChildItem (Join-Path $game "panorama\images\custom_game\$Addon\thumbs") -Filter *_jpg.vtex_c -ErrorAction SilentlyContinue)
$bytes = ($built | Measure-Object -Property Length -Sum).Sum
if ($built.Count -eq $want) { Write-Output ("OK      {0} map thumbnails ({1:0.0} MB)" -f $built.Count, ($bytes / 1MB)) }
else { Write-Output "MISSING thumbnails: $($built.Count) of $want built - read the compiler output above"; $missing++ }
if ($missing -gt 0) { exit 1 }
Write-Output ""
Write-Output "Built into $game - publish that addon with the Workshop Manager (PUBLISH.md)."

if ($Local) {
    # Your own client loads loose compiled files from game\csgo\panorama\..., so the HUD can be tried on the server before it is
    # published (the server half: css_gghud on). Remove them again afterwards, or the published addon and these would compete.
    $own = Join-Path $Cs2 "game\csgo"
    foreach ($rel in $expected) {
        $to = Join-Path $own $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $to) | Out-Null
        Copy-Item (Join-Path $game $rel) $to -Force
        Write-Output "copied for local testing: $to"
    }
    Write-Output "Restart CS2 to load them: Panorama caches layouts for the whole game session."
}
