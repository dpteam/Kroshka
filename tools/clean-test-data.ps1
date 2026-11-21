<#
.SYNOPSIS
    Removes test data from a Kroshka data folder.

.DESCRIPTION
    Built for one habit: on a live server, only create accounts whose nickname
    starts with a marker (default "test"), so cleaning up afterwards is a single
    command instead of archaeology.

    Three modes:

      -UsersOnly     delete users matching the marker, and their DMs.
                     Messages they wrote in shared rooms stay, but the author
                     becomes an anonymous "deleted user" - which is honest.
      -TestRooms     also delete rooms whose name starts with the marker.
      -Everything    wipe the whole data folder. Refuses to run while the
                     server is listening on the port, because deleting files
                     under a running server corrupts its in-memory state.

    Default (no switch) is a dry run: it prints what it would delete.

.EXAMPLE
    # see what would go
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\clean-test-data.ps1 -Data data

.EXAMPLE
    # actually delete test1, test2, ... and their private messages
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\clean-test-data.ps1 -Data data -UsersOnly -Apply

.EXAMPLE
    # start from scratch before going live
    powershell -NoProfile -ExecutionPolicy Bypass -File tools\clean-test-data.ps1 -Data data -Everything -Apply
#>

[CmdletBinding()]
param(
    [string]$Data = 'data',
    [string]$Marker = 'test',
    [switch]$UsersOnly,
    [switch]$TestRooms,
    [switch]$Everything,
    [switch]$Apply,
    [int]$Port = 6767
)

$ErrorActionPreference = 'Stop'

function Write-Step($text) { Write-Host $text }
function Write-Del($text) { Write-Host ("  - " + $text) -ForegroundColor Yellow }
function Write-Keep($text) { Write-Host ("    keep " + $text) -ForegroundColor DarkGray }

# --------------------------------------------------------------------- safety

if (-not (Test-Path $Data)) {
    throw "data folder not found: $Data"
}

$dataPath = (Resolve-Path $Data).Path

# A running server holds sessions and message logs in memory; deleting underneath
# it means the next write resurrects deleted files or corrupts a compacted one.
$listening = $false
try {
    $listening = [bool](Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
} catch {
    $listening = $false
}

if ($listening) {
    Write-Host ''
    Write-Host "  The server is listening on port $Port." -ForegroundColor Red
    Write-Host '  Stop it first, otherwise deleted data comes back on the next save.' -ForegroundColor Red
    Write-Host ''
    exit 1
}

if (-not $Everything -and -not $UsersOnly -and -not $TestRooms) {
    Write-Step "Dry run. Nothing is deleted until you pass -Apply."
    Write-Step "  users matching '$Marker*' and their DMs"
    Write-Step "  (add -TestRooms for rooms, -Everything for a full wipe)"
    Write-Step ""
}

$mode = if ($Everything) { 'everything' } elseif ($TestRooms) { 'test-rooms' } else { 'users-only' }

if (-not $Apply) {
    Write-Step "Mode: $mode (dry run)"
}

# ------------------------------------------------------------------- full wipe

if ($Everything) {
    $size = 0
    $count = 0
    Get-ChildItem -Path $dataPath -Recurse -File | ForEach-Object {
        $size += $_.Length
        $count++
    }

    Write-Step ("Wiping {0} files ({1} MB) from {2}" -f $count, [Math]::Round($size / 1MB, 1), $dataPath)

    if ($Apply) {
        foreach ($child in Get-ChildItem -Path $dataPath -Force) {
            if ($child.PSIsContainer) {
                Remove-Item -Recurse -Force $child.FullName
            } else {
                Remove-Item -Force $child.FullName
            }
        }
        Write-Step ''
        Write-Step '  done. data/ is empty; the server recreates it on start.' -ForegroundColor Green
    }

    return
}

# --------------------------------------------------------------- users first

$usersDir = Join-Path $dataPath 'users'
$dmDir = Join-Path $dataPath 'dm'

$testUsers = @()
$keptUsers = @()

if (Test-Path $usersDir) {
    foreach ($dir in Get-ChildItem -Path $usersDir -Directory) {
        $userFile = Join-Path $dir.FullName 'user.json'
        if (-not (Test-Path $userFile)) { continue }

        $user = Get-Content $userFile -Raw -Encoding UTF8 | ConvertFrom-Json
        $entry = [pscustomobject]@{
            Id   = $user.id
            Nick = $user.nick
            Dir  = $dir.FullName
            Role = $user.role
        }

        if ($entry.Nick -like "$Marker*") { $testUsers += $entry } else { $keptUsers += $entry }
    }
}

Write-Step ("Users: {0} match '{1}*', {2} kept" -f $testUsers.Count, $Marker, $keptUsers.Count)

foreach ($u in $testUsers) { Write-Del ("user {0} ({1})" -f $u.Nick, $u.Id) }

# DMs are only meaningful between two real accounts. A DM whose either side is
# being deleted goes with them; one between two kept users stays.
$testIds = @($testUsers | ForEach-Object { $_.Id })
$testDirs = @()

if (Test-Path $dmDir) {
    foreach ($dir in Get-ChildItem -Path $dmDir -Directory) {
        $dmFile = Join-Path $dir.FullName 'dm.json'
        if (-not (Test-Path $dmFile)) { continue }

        $dm = Get-Content $dmFile -Raw -Encoding UTF8 | ConvertFrom-Json
        $touchesTest = ($testIds -contains $dm.userA) -or ($testIds -contains $dm.userB)

        if ($touchesTest) {
            $testDirs += $dir.FullName
            Write-Del ("dm {0}" -f $dm.id)
        }
    }
}

# ---------------------------------------------------------------- test rooms

$roomsDir = Join-Path $dataPath 'rooms'
$testRooms = @()

if (Test-Path $roomsDir) {
    foreach ($dir in Get-ChildItem -Path $roomsDir -Directory) {
        $roomFile = Join-Path $dir.FullName 'room.json'
        if (-not (Test-Path $roomFile)) { continue }

        $room = Get-Content $roomFile -Raw -Encoding UTF8 | ConvertFrom-Json

        if ($room.name -like "$Marker*") {
            $testRooms += [pscustomobject]@{ Id = $room.id; Name = $room.name; Dir = $dir.FullName }
            Write-Del ("room '{0}' ({1})" -f $room.name, $room.id)
        } else {
            Write-Keep ("room '{0}'" -f $room.name)
        }
    }
}

# --------------------------------------------------------------------- apply

if (-not $Apply) {
    Write-Step ''
    Write-Step ("Dry run finished: {0} users, {1} DMs, {2} rooms would be deleted." -f `
        $testUsers.Count, $testDirs.Count, $(if ($mode -eq 'test-rooms') { $testRooms.Count } else { 0 }))
    Write-Step 'Re-run with -Apply to do it.'
    return
}

foreach ($u in $testUsers) {
    Remove-Item -Recurse -Force $u.Dir
}

foreach ($dir in $testDirs) {
    Remove-Item -Recurse -Force $dir
}

if ($mode -eq 'test-rooms') {
    foreach ($room in $testRooms) {
        Remove-Item -Recurse -Force $room.Dir
    }
}

# Sessions belong to accounts; a session pointing at a deleted user is dead
# weight the server would prune anyway, but doing it now keeps the file honest.
$sessionsFile = Join-Path $dataPath 'sessions.json'
if ((Test-Path $sessionsFile) -and $testIds.Count -gt 0) {
    $sessions = Get-Content $sessionsFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $before = @($sessions).Count
    $alive = @($sessions | Where-Object { $testIds -notcontains $_.userId })
    if ($alive.Count -ne $before) {
        $alive | ConvertTo-Json -Depth 6 | Set-Content -Path $sessionsFile -Encoding UTF8
        Write-Step ("  pruned {0} session(s)" -f ($before - $alive.Count))
    }
}

# Invites that were issued to deleted users are meaningless now.
$invitesFile = Join-Path $dataPath 'invites.json'
if ((Test-Path $invitesFile) -and $testIds.Count -gt 0) {
    $invites = Get-Content $invitesFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $changed = $false
    foreach ($code in @($invites.PSObject.Properties.Name)) {
        $remaining = @($invites.$code | Where-Object { $testIds -notcontains $_ })
        if ($remaining.Count -ne @($invites.$code).Count) {
            $invites.$code = $remaining
            $changed = $true
        }
    }
    if ($changed) {
        $invites | ConvertTo-Json -Depth 6 | Set-Content -Path $invitesFile -Encoding UTF8
        Write-Step '  cleaned invites'
    }
}

Write-Step ''
Write-Step '  done.' -ForegroundColor Green
Write-Step '  Restart the server so it reloads the remaining data.'
