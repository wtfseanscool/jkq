# JKQ LAN Co-op — show the LIVE BepInEx log from the game folder. Run from the extracted folder.
#   .\showlog.ps1                 (auto-detect game; print join-relevant lines)
#   .\showlog.ps1 -Full           (print the whole log)
#   .\showlog.ps1 -Tail 40        (print last N lines)
#   .\showlog.ps1 -GameDir "..."
param([string]$GameDir = '', [switch]$Full, [int]$Tail = 0)
$ErrorActionPreference = 'Stop'

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

$game = Find-GameDir -Override $GameDir
$log  = Join-Path $game 'BepInEx\LogOutput.log'
if (-not (Test-Path $log)) { throw "No log at $log -- launch the game once first." }

$item = Get-Item $log
Write-Host ""
Write-Host "Log file   : $log"
Write-Host "Log mtime  : $($item.LastWriteTime)   (this should be RECENT if you just played)"
Write-Host ""

$lines = Get-Content $log
$header = $lines | Where-Object { $_ -match 'BepInEx .* - JKQ \(' } | Select-Object -First 1
Write-Host "Launch header: $header"
Write-Host "  (If this timestamp is NOT from your latest launch, the game did not start through BepInEx,"
Write-Host "   or you are reading an old copy of the log.)"
Write-Host ""

if ($Full)      { $lines; return }
if ($Tail -gt 0){ $lines | Select-Object -Last $Tail; return }

Write-Host "=== plugin + [join] lines ==="
$lines | Where-Object { $_ -match 'JKQ Local Matchmaker Redirect|\[join\]|\[client\]|\[broker\]' }
