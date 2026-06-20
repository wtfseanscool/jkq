# Builds a self-contained distributable zip for the JKQ LAN co-op fix.
# The zip contains: BepInEx (win x64), our plugin + broker DLLs, a default config, an end-user
# install script, and a README. It deliberately EXCLUDES all game-owned DLLs (SteamyChimp,
# ChimpWorldMatchmaking, ChimpKeeperShared, Steamworks.NET, NexileUtil, Assembly-CSharp, Unity*) —
# those load from the user's own game install at runtime and are not ours to redistribute.
param(
    [string]$OutZip = "$PSScriptRoot\..\dist\JKQ-LAN-Coop.zip"
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path "$PSScriptRoot\.."
$staging = Join-Path $env:TEMP ("jkqlan_pkg_" + [Guid]::NewGuid().ToString("N"))
$payload = Join-Path $staging 'JKQ-LAN-Coop'

function Step($m){ Write-Host "[pkg] $m" }

# --- (re)build plugin + broker in Release ---
Step "Building plugin + broker (Release)..."
& dotnet build (Join-Path $repo 'plugin\JKQLocalMM.csproj') -c Release -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) { throw "plugin build failed" }

$pluginDll = Join-Path $repo 'plugin\bin\Release\JKQLocalMM.dll'
$brokerDll = Join-Path $repo 'broker\bin\Release\ChimpWorldMatchmakingServer.dll'
$bepinexSrc = Join-Path $repo 'libs\BepInEx'
foreach ($p in @($pluginDll,$brokerDll,$bepinexSrc)) { if (-not (Test-Path $p)) { throw "missing: $p" } }

# --- assemble payload layout (mirrors what goes into the game folder) ---
New-Item -ItemType Directory -Path $payload -Force | Out-Null

# BepInEx loader + core (winhttp.dll, doorstop, BepInEx/core)
Copy-Item (Join-Path $bepinexSrc '*') $payload -Recurse -Force

# our plugin + broker into BepInEx/plugins/JKQLocalMM
$plugDir = Join-Path $payload 'BepInEx\plugins\JKQLocalMM'
New-Item -ItemType Directory -Path $plugDir -Force | Out-Null
Copy-Item $pluginDll $plugDir -Force
Copy-Item $brokerDll $plugDir -Force

# default config (the installer rewrites Role/Ip/Port/ServerKey based on user choices)
$cfgDir = Join-Path $payload 'BepInEx\config'
New-Item -ItemType Directory -Path $cfgDir -Force | Out-Null
@"
## JKQ LAN Co-op config. The installer overwrites these from your answers.
[General]
Enabled = true

[Matchmaker]
ServerKey = internal
Ip = 127.0.0.1
Port = 9050
Label = Local

[Lan]
Role = Host
"@ | Set-Content -Path (Join-Path $cfgDir 'com.jkqcoop.localmm.cfg')

# --- end-user installer (self-contained; auto-detects the game) ---
Copy-Item (Join-Path $repo 'tools\install.ps1') $payload -Force
Copy-Item (Join-Path $repo 'tools\check.ps1') $payload -Force
Copy-Item (Join-Path $repo 'tools\showlog.ps1') $payload -Force
Copy-Item (Join-Path $repo 'tools\README-LAN.txt') $payload -Force -ErrorAction SilentlyContinue

# --- zip it ---
$distDir = Split-Path $OutZip -Parent
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
if (Test-Path $OutZip) { Remove-Item $OutZip -Force }
Step "Compressing -> $OutZip"
Compress-Archive -Path (Join-Path $staging 'JKQ-LAN-Coop') -DestinationPath $OutZip -Force

Remove-Item $staging -Recurse -Force
Step "DONE: $OutZip"
Get-Item $OutZip | ForEach-Object { Write-Host ("[pkg] size: " + [math]::Round($_.Length/1KB,1) + " KB") }
