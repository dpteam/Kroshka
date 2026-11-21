# ---------------------------------------------------------------------------
# Opens the Kroshka port in Windows Firewall.
#
# Run from an elevated PowerShell:
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\firewall.ps1
#
#   ... -Port 7000          use a different port
#   ... -Remove             delete the rules again
#   ... -PrivateOnly        only allow the private/home profile (safer)
#
# A LAN messenger that cannot accept a connection is indistinguishable from a
# broken one, so this is a real part of the install, not an afterthought.
# ---------------------------------------------------------------------------

[CmdletBinding()]
param(
    [int]$Port = 6767,
    [int]$AdminPort = 6768,
    [switch]$Remove,
    [switch]$PrivateOnly,
    [switch]$SkipAdmin
)

$ErrorActionPreference = 'Stop'

function Test-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    Write-Host ''
    Write-Host '  This script needs administrator rights.' -ForegroundColor Yellow
    Write-Host '  Right-click PowerShell -> "Run as administrator", then run it again.' -ForegroundColor Yellow
    Write-Host ''
    exit 1
}

$profileName = if ($PrivateOnly) { 'Private' } else { 'Any' }

$rules = @(
    @{ Name = 'Kroshka Server';     Port = $Port;      Description = 'Kroshka messenger (chat, websocket, uploads)' }
)

if (-not $SkipAdmin) {
    $rules += @{ Name = 'Kroshka Admin'; Port = $AdminPort; Description = 'Kroshka admin board' }
}

foreach ($rule in $rules) {
    $existing = Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue

    if ($Remove) {
        if ($existing) {
            Remove-NetFirewallRule -DisplayName $rule.Name
            Write-Host ("  removed  {0} (tcp/{1})" -f $rule.Name, $rule.Port) -ForegroundColor Yellow
        } else {
            Write-Host ("  absent   {0}" -f $rule.Name) -ForegroundColor DarkGray
        }
        continue
    }

    if ($existing) {
        # Recreate instead of editing: the port may have changed since last time.
        Remove-NetFirewallRule -DisplayName $rule.Name
    }

    New-NetFirewallRule `
        -DisplayName $rule.Name `
        -Description $rule.Description `
        -Direction Inbound `
        -Action Allow `
        -Protocol TCP `
        -LocalPort $rule.Port `
        -Profile $profileName `
        -EdgeTraversalPolicy Block | Out-Null

    Write-Host ("  allowed  {0} -> tcp/{1} ({2})" -f $rule.Name, $rule.Port, $profileName) -ForegroundColor Green
}

if (-not $Remove) {
    Write-Host ''
    Write-Host '  Also allow Kroshka through third-party firewalls if you run one' -ForegroundColor DarkGray
    Write-Host '  (Kaspersky, ESET, Comodo). They keep their own allow-lists.' -ForegroundColor DarkGray
}

Write-Host ''
