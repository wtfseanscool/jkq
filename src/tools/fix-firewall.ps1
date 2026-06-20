# Opens the LAN co-op port on the HOST machine. Run as Administrator (right-click > Run with PowerShell,
# or from an elevated terminal). Reversible with -Remove.
#
#   Host setup:  powershell -ExecutionPolicy Bypass -File fix-firewall.ps1
#   Undo:        powershell -ExecutionPolicy Bypass -File fix-firewall.ps1 -Remove
param(
    [int]$Port = 9050,
    [switch]$Remove,
    [switch]$SetWifiPrivate
)
$ErrorActionPreference = 'Stop'
$ruleName = "JKQ LAN Coop (TCP $Port)"

function IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
}
if (-not (IsAdmin)) {
    Write-Host "ERROR: must run as Administrator." -ForegroundColor Red
    Write-Host "Right-click PowerShell -> Run as administrator, then re-run this script." -ForegroundColor Yellow
    exit 1
}

if ($Remove) {
    Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Write-Host "Removed firewall rule '$ruleName'." -ForegroundColor Cyan
    exit 0
}

# 1) Disable the stale 'jkq' BLOCK rules (they point at a different install on D: and only cause confusion).
$blocks = Get-NetFirewallRule -DisplayName 'jkq' -ErrorAction SilentlyContinue | Where-Object { $_.Action -eq 'Block' }
if ($blocks) {
    $blocks | Disable-NetFirewallRule
    Write-Host "Disabled $($blocks.Count) stale 'jkq' BLOCK rule(s)." -ForegroundColor Cyan
}

# 2) Add an explicit inbound ALLOW for the broker port (TCP), all profiles, local subnet only (safer).
Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP `
    -LocalPort $Port -Profile Any -RemoteAddress LocalSubnet | Out-Null
Write-Host "Added inbound ALLOW: TCP $Port (LocalSubnet)." -ForegroundColor Cyan

# 3) Optionally flip Wi-Fi from Public to Private (Public is very restrictive for inbound).
if ($SetWifiPrivate) {
    Get-NetConnectionProfile | Where-Object { $_.InterfaceAlias -like 'Wi-Fi*' -and $_.NetworkCategory -eq 'Public' } | ForEach-Object {
        Set-NetConnectionProfile -InterfaceIndex $_.InterfaceIndex -NetworkCategory Private
        Write-Host ("Set " + $_.InterfaceAlias + " to Private.") -ForegroundColor Cyan
    }
}

Write-Host ""
Write-Host "DONE. Host LAN IP for the joiner to use:" -ForegroundColor Green
Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -like '192.168.*' -and $_.InterfaceAlias -like 'Wi-Fi*' } |
    ForEach-Object { Write-Host ("   " + $_.IPAddress) -ForegroundColor Green }
