# The HUD of a clean checkout, built for the documentation's screenshots into an addon folder of its own (os2docs by default), so a build
# of the gungame_hud addon by anyone else never meets this one; cs2client.ps1 start -HudAddon <that name> installs it as loose files. The
# same compiler calls as hud\build.ps1, but the pictures stay under panorama\styles\custom_game\gungame_hud\ and
# panorama\images\custom_game\gungame_hud\, because the stylesheet names them there, whatever the addon is called.
#
#   build_hud.ps1 [-Src <checkout>\hud] [-Thumbs <folder of jpgs>] [-Addon os2docs] [-Cs2 <path to 'Counter-Strike Global Offensive'>]
#
# -Src: the hud folder of the commit under test (default: this checkout's). -Thumbs: the map pictures, compiled too (about 4 min); without
# it the ones already compiled into the addon stay. -Cs2: the CS2 folder (default: found through Steam's library folders, as hud\build.ps1
# does; the private config's workshop.cs2_dir, when set, is the one to pass).
param([string]$Src = "", [string]$Thumbs = "", [string]$Addon = "os2docs", [string]$Cs2 = "")
$ErrorActionPreference = "Stop"
[System.Diagnostics.Process]::GetCurrentProcess().PriorityClass = 'BelowNormal'
if (-not $Src) { $Src = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) '..\hud' }
$Src = [IO.Path]::GetFullPath($Src)
if (-not (Test-Path (Join-Path $Src 'layout'))) { Write-Output "no HUD sources in $Src (layout\ is missing)"; exit 1 }
if ($Addon -notmatch '^[\w.-]+$' -or $Addon -eq 'gungame_hud') { Write-Output "-Addon takes a folder name of its own, not '$Addon'"; exit 1 }
if (-not $Cs2) {
    $roots = @()
    $steamKey = Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue
    if ($steamKey -and $steamKey.SteamPath) { $roots += ($steamKey.SteamPath -replace '/', '\') }
    $roots += "C:\Program Files (x86)\Steam"
    foreach ($steam in @($roots)) {
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) { foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"(.+?)"')) { $roots += $m.Groups[1].Value.Replace('\\', '\') } }
    }
    foreach ($r in $roots) {
        $candidate = Join-Path $r 'steamapps\common\Counter-Strike Global Offensive'
        if (Test-Path (Join-Path $candidate 'game\bin\win64\resourcecompiler.exe')) { $Cs2 = $candidate; break }
    }
}
if (-not $Cs2 -or -not (Test-Path $Cs2)) { Write-Output "Counter-Strike 2 with its Workshop Tools not found. Pass -Cs2 <path to 'Counter-Strike Global Offensive'>"; exit 1 }
$compiler = Join-Path $Cs2 'game\bin\win64\resourcecompiler.exe'
if (-not (Test-Path $compiler)) { Write-Output "The Workshop Tools are not installed: $compiler is missing (hud\build.ps1 says how to install them)"; exit 1 }
$content = Join-Path $Cs2 "content\csgo_addons\$Addon"
$game = Join-Path $Cs2 "game\csgo_addons\$Addon"
$layoutDir = Join-Path $content 'panorama\layout\custom_game'
$styleDir = Join-Path $content 'panorama\styles\custom_game'
$imageDir = Join-Path $styleDir 'gungame_hud'
$thumbDir = Join-Path $content 'panorama\images\custom_game\gungame_hud\thumbs'
New-Item -ItemType Directory -Force -Path $layoutDir, $styleDir, $imageDir, $thumbDir, $game | Out-Null
Remove-Item (Join-Path $layoutDir '*.xml'), (Join-Path $styleDir '*.css') -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $Src 'layout\*.xml') $layoutDir -Force
Copy-Item (Join-Path $Src 'styles\*.css') $styleDir -Force
Copy-Item (Join-Path $Src 'images\*') $imageDir -Force
Copy-Item (Join-Path $Src 'addoninfo.txt') $game -Force
if ($Thumbs) {
    Remove-Item (Join-Path $thumbDir '*') -Force -ErrorAction SilentlyContinue
    Copy-Item (Join-Path $Thumbs '*.jpg') $thumbDir -Force
    Write-Output ("thumbs copied: {0}" -f (Get-ChildItem $thumbDir -Filter *.jpg).Count)
}
Get-ChildItem $layoutDir, $styleDir, $imageDir -File | ForEach-Object { $_.LastWriteTime = Get-Date }
if ($Thumbs) { Get-ChildItem $thumbDir -File | ForEach-Object { $_.LastWriteTime = Get-Date } }
if ($Thumbs) { foreach ($g in 'panorama\layout', 'panorama\styles') { Remove-Item (Join-Path $game $g) -Recurse -Force -ErrorAction SilentlyContinue } }
else { foreach ($g in 'panorama\layout\custom_game\gungame_hud_v3_0_0.vxml_c', 'panorama\styles\custom_game\gungame_hud_v3_0_0.vcss_c') { Remove-Item (Join-Path $game $g) -Force -ErrorAction SilentlyContinue } }
if ($Thumbs) { Remove-Item (Join-Path $game 'panorama\images') -Recurse -Force -ErrorAction SilentlyContinue }
$t0 = Get-Date
& $compiler -i (Join-Path $imageDir '*.vtex') -f -r
Write-Output ("images {0:0} s" -f ((Get-Date) - $t0).TotalSeconds)
foreach ($f in Get-ChildItem $styleDir -Filter *.css) {
    # the pictures' stylesheet compiles every picture, 3-5 min, with or without -r: only with -Thumbs; without it the compiled pictures and their stylesheet stay
    if (-not $Thumbs -and $f.Name -like 'gungame_hud_thumbs_*') { continue }
    if ($Thumbs) { & $compiler -i $f.FullName -f -r } else { & $compiler -i $f.FullName -f }
    Write-Output ("{0} {1:0} s" -f $f.Name, ((Get-Date) - $t0).TotalSeconds)
}
foreach ($f in Get-ChildItem $layoutDir -Filter *.xml) {
    # -r would compile what the layout refers to: the pictures' stylesheet too, about 4 min; a build without -Thumbs keeps the compiled one
    if ($Thumbs) { & $compiler -i $f.FullName -f -r } else { & $compiler -i $f.FullName -f }
    Write-Output ("{0} {1:0} s" -f $f.Name, ((Get-Date) - $t0).TotalSeconds)
}
$expected = @('panorama\layout\custom_game\gungame_hud_v3_0_0.vxml_c', 'panorama\styles\custom_game\gungame_hud_v3_0_0.vcss_c', 'panorama\styles\custom_game\gungame_hud_thumbs_v3_0_0.vcss_c',
    'panorama\styles\custom_game\gungame_hud\knife.vtex_c', 'panorama\styles\custom_game\gungame_hud\logo.vtex_c', 'panorama\styles\custom_game\gungame_hud\lock_shut.vtex_c', 'panorama\styles\custom_game\gungame_hud\lock_open.vtex_c')
$missing = 0
foreach ($rel in $expected) {
    $f = Join-Path $game $rel
    if (Test-Path $f) { Write-Output ("OK      {0} ({1} bytes)" -f $rel, (Get-Item $f).Length) } else { Write-Output "MISSING $rel"; $missing++ }
}
$built = @(Get-ChildItem (Join-Path $game 'panorama\images\custom_game\gungame_hud\thumbs') -Filter *_jpg.vtex_c -ErrorAction SilentlyContinue)
Write-Output ("thumbnails compiled: {0}" -f $built.Count)
Write-Output "Built into $game - cs2client.ps1 start -HudAddon $Addon installs it as loose files"
if ($missing) { exit 1 }
