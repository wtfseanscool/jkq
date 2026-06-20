# JKQ LAN Co-op installer (end-user). Run from the extracted JKQ-LAN-Coop folder.
#
#   Host  :  .\install.ps1 -Role Host
#   Joiner:  .\install.ps1 -Role Joiner -HostIp 192.168.1.50
#   Remove:  .\install.ps1 -Uninstall
#
# Auto-detects the JUMP KING QUEST install via the Steam library folders. Override with -GameDir.
param(
    [ValidateSet('Host','Joiner')][string]$Role = 'Host',
    [string]$HostIp = '',
    [string]$HostSteamId = '0',
    [int]   $Port = 9050,
    [string]$ServerKey = 'internal',
    [string]$GameDir = '',
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
function Step($m){ Write-Host "[install] $m" -ForegroundColor Cyan }
function Warn($m){ Write-Host "[install] $m" -ForegroundColor Yellow }

# ---------- locate the game ----------
function Find-GameDir {
    param([string]$Override)
    if ($Override) { if (Test-Path $Override) { return $Override } else { throw "GameDir not found: $Override" } }

    $candidates = @()
    # Default Steam install + library folders from libraryfolders.vdf
    $steam = "${env:ProgramFiles(x86)}\Steam"
    $candidates += Join-Path $steam 'steamapps\common\JUMP KING QUEST'
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        Get-Content $vdf | Select-String -Pattern '"path"\s*"(.+?)"' | ForEach-Object {
            $p = ($_.Matches[0].Groups[1].Value -replace '\\\\','\')
            $candidates += Join-Path $p 'steamapps\common\JUMP KING QUEST'
        }
    }
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c 'JKQ.exe')) { return $c }
    }
    throw "Could not auto-detect JUMP KING QUEST. Re-run with -GameDir 'C:\path\to\JUMP KING QUEST'."
}

$game = Find-GameDir -Override $GameDir
Step "Game: $game"

# ---------- uninstall ----------
if ($Uninstall) {
    foreach ($f in @('winhttp.dll','doorstop_config.ini','.doorstop_version','changelog.txt')) {
        $p = Join-Path $game $f; if (Test-Path $p) { Remove-Item $p -Force; Step "Removed $f" }
    }
    $bx = Join-Path $game 'BepInEx'; if (Test-Path $bx) { Remove-Item $bx -Recurse -Force; Step "Removed BepInEx/" }
    Step "Uninstalled. The game is back to vanilla."
    return
}

# ---------- validate inputs ----------
if ($Role -eq 'Joiner' -and [string]::IsNullOrWhiteSpace($HostIp)) {
    throw "Joiner needs the host's IP: .\install.ps1 -Role Joiner -HostIp <host-ip>"
}
$ip = if ($Role -eq 'Host') { if ($HostIp) { $HostIp } else { '0.0.0.0' } } else { $HostIp }
# Host binds all interfaces; the LAN transport listens on 0.0.0.0 regardless, but the game client
# connects to this Ip. For Host that's loopback; force 127.0.0.1 so the host's own client connects locally.
if ($Role -eq 'Host') { $ip = '127.0.0.1' }

# ---------- copy payload ----------
Step "Installing BepInEx + plugin (Role=$Role)..."
Copy-Item (Join-Path $src 'winhttp.dll') $game -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $src 'doorstop_config.ini') $game -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $src '.doorstop_version') $game -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $src 'changelog.txt') $game -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $src 'BepInEx') $game -Recurse -Force

# ---------- write config ----------
$cfgDir = Join-Path $game 'BepInEx\config'
New-Item -ItemType Directory -Path $cfgDir -Force | Out-Null
@"
## JKQ LAN Co-op
[General]
Enabled = true

[Matchmaker]
ServerKey = $ServerKey
Ip = $ip
Port = $Port
Label = Local

[Lan]
Role = $Role
HostSteamId = $HostSteamId
AutoJoin = true
JoinKey = F6
"@ | Set-Content -Path (Join-Path $cfgDir 'com.jkqcoop.localmm.cfg')

Step "Config: Role=$Role Ip=$ip Port=$Port ServerKey=$ServerKey HostSteamId=$HostSteamId"
Step "DONE."
Write-Host ""
if ($Role -eq 'Host') {
    Step "You are the HOST. Launch the game; the broker listens on port $Port."
    Step "Make sure your firewall allows inbound TCP $Port, and tell the joiner your LAN/VPN IP."
    Step "Tell the joiner your SteamID64 (find it in BepInEx\LogOutput.log: 'selfId=...' after you launch)."
} else {
    Step "You are the JOINER. The host ($HostIp) must be running first."
    if ($HostSteamId -eq '0') {
        Warn "HostSteamId not set! Re-run with -HostSteamId <host's SteamID64>, or edit"
        Warn "BepInEx\config\com.jkqcoop.localmm.cfg before launching. Without it, joining won't work."
    } else {
        Step "Launch the game and enter any online area; auto-join to host $HostSteamId will fire (or press F6)."
    }
}
Warn "Both players MUST use the same ServerKey ('$ServerKey')."
