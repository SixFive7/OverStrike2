# Where Counter-Strike 2 is installed on this PC, for the scripts that build or publish our Workshop items (hud\build.ps1, hud\publish.ps1,
# maps\publisher\publish.ps1). Dot-source it, then:
#
#   $Cs2 = Find-Cs2 $Cs2                                        # the folder passed, else the first install Steam knows of; "" for none
#   $Cs2 = Find-Cs2 $Cs2 "game\bin\win64\resourcecompiler.exe"   # ...the first that has this file (the Workshop Tools, say)
#
# Steam's own folder comes from the registry (HKCU\Software\Valve\Steam, SteamPath) and its usual place; every library folder Steam lists in
# its libraryfolders.vdf is looked in, in that order. A folder passed by hand is taken as it is: the caller says what is missing from it.

function Find-Cs2([string]$Cs2 = "", [string]$Needs = "game\bin\win64\cs2.exe") {
    if ($Cs2) { return $Cs2 }
    $steams = @()
    $key = Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue
    if ($key -and $key.SteamPath) { $steams += ($key.SteamPath -replace '/', '\') }
    $steams += "C:\Program Files (x86)\Steam"
    $roots = @()
    foreach ($steam in $steams) {
        $roots += $steam
        $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"(.+?)"')) { $roots += $m.Groups[1].Value.Replace('\\', '\') }
        }
    }
    foreach ($root in ($roots | Select-Object -Unique)) {
        $candidate = Join-Path $root "steamapps\common\Counter-Strike Global Offensive"
        if (Test-Path -LiteralPath (Join-Path $candidate $Needs)) { return $candidate }
    }
    return ""
}
