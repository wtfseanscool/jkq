# JKQ LAN Co-op — installation self-check. Run from the extracted JKQ-LAN-Coop folder.
#   .\check.ps1                 (auto-detect game)
#   .\check.ps1 -GameDir "..."  (explicit)
#
# Prints what is ACTUALLY installed in the game folder vs what shipped in this package,
# so we can tell whether a reinstall really took effect (independent of game logs).
param([string]$GameDir = '')
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
function Line($m){ Write-Host $m }

function Find-GameDir {
    param([string]$Override)
    if ($Override) { if (Test-Path $Override) { return $Override } else { throw "GameDir not found: $Override" } }
    $candidates = @()
    $steam = "${env:ProgramFiles(x86)}\Steam"
    $candidates += Join-Path $steam 'steamapps\common\JUMP KING QUEST'
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        Get-Content $vdf | Select-String -Pattern '"path"\s*"(.+?)"' | ForEach-Object {
            $p = ($_.Matches[0].Groups[1].Value -replace '\\\\','\')
            $candidates += Join-Path $p 'steamapps\common\JUMP KING QUEST'
        }
    }
    foreach ($c in $candidates) { if (Test-Path (Join-Path $c 'JKQ.exe')) { return $c } }
    throw "Could not auto-detect JUMP KING QUEST. Re-run with -GameDir '...'."
}

function FileHash2($p){ if (Test-Path $p) { (Get-FileHash $p -Algorithm SHA256).Hash } else { 'MISSING' } }

$game = Find-GameDir -Override $GameDir
Line ""
Line "Game folder : $game"
Line ""

# Is the game running? (a running game locks the DLL so a reinstall silently fails)
$proc = Get-Process JKQ -ErrorAction SilentlyContinue
if ($proc) { Line "GAME IS RUNNING (pid $($proc.Id)) -- close it before reinstalling, or the DLL won't update!" }
else       { Line "Game not running (safe to reinstall)." }
Line ""

$packagePlugin   = Get-ChildItem -Recurse -File $src -Filter JKQLocalMM.dll | Select-Object -First 1
$installedPlugin = Join-Path $game 'BepInEx\plugins\JKQLocalMM\JKQLocalMM.dll'

Line "JKQLocalMM.dll"
Line ("  in this package : " + (FileHash2 $packagePlugin.FullName))
Line ("  installed in game: " + (FileHash2 $installedPlugin))
if (Test-Path $installedPlugin) {
    Line ("  installed mtime  : " + (Get-Item $installedPlugin).LastWriteTime)
}
$match = (Test-Path $installedPlugin) -and ((FileHash2 $packagePlugin.FullName) -eq (FileHash2 $installedPlugin))
Line ""
if ($match) {
    Line "RESULT: installed plugin MATCHES this package (latest build is live)."
} else {
    Line "RESULT: MISMATCH -- the game is NOT running this package's plugin."
    Line "        Close the game, run install.ps1 again, then re-run this check."
}

# Also surface any OTHER copies of the plugin anywhere under the game folder (stale duplicates).
Line ""
$all = Get-ChildItem -Recurse -File $game -Filter JKQLocalMM.dll -ErrorAction SilentlyContinue
Line ("All JKQLocalMM.dll copies under the game folder: " + $all.Count)
foreach ($f in $all) { Line ("  " + $f.FullName) }
