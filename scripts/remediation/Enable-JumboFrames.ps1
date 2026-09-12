# Created by forg3
# License: MIT
#
# Conditional Jumbo Frames
# NEVER blindly enables jumbo frames: first tests whether MTU 9000 passes end-to-end,
# only then applies to the interface. Inconsistent MTU along the path causes
# fragmentation (worse than 1500) or complete loss of connectivity.
#
# USAGE:
#   .\Enable-JumboFrames.ps1 -Test                          # test without applying
#   .\Enable-JumboFrames.ps1 -Apply -Gateway 192.168.0.1    # test and apply if passes
#   .\Enable-JumboFrames.ps1 -Rollback                      # revert to 1500
#   .\Enable-JumboFrames.ps1 -Status
#
# NETWORK PREREQUISITE: ALL hops (NIC, switch, server) must support 9000.
# If destination is a share over VPN (Tailscale/WireGuard), jumbo does NOT apply.

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Test,
    [switch]$Apply,
    [switch]$Rollback,
    [switch]$Status,
    [string]$Gateway = "",
    [string]$AdapterName = "",
    [int]$Mtu = 9000,
    [int]$ProbeSize = 8972   # 9000 - 28 bytes IP+ICMP header
)

$ErrorActionPreference = 'Stop'
$BackupDir = Join-Path $env:ProgramData 'SmbSpeedDoctor'
$BackupFile = Join-Path $BackupDir 'jumbo-backup.json'

function Get-NicTarget {
    if ($AdapterName) { return Get-NetAdapter -Name $AdapterName | Where-Object Status -eq 'Up' }
    # Interface with default route = actual physical NIC
    $gw = (Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 1).InterfaceAlias
    return Get-NetAdapter -Name $gw
}

function Test-MtuPath {
    param([string]$Target, [int]$Size)
    # -f = do not fragment: if packet does not pass intact, fails honestly
    $result = ping -n 3 -f -l $Size $Target 2>&1
    $lost = ($result | Select-String 'Perdidos: 3|Lost = 3|100% loss|100% de perda').Count
    return ($lost -eq 0)
}

function Assert-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Administrator elevation required."
    }
}

if ($Status) {
    Write-Host "=== JUMBO FRAMES — STATUS ===" -ForegroundColor Cyan
    Get-NicTarget | ForEach-Object {
        $currentMtu = (Get-NetIPInterface -InterfaceAlias $_.Name -AddressFamily IPv4).NlMtu
        Write-Host ("{0}: MTU = {1}" -f $_.Name, $currentMtu)
    }
    if (-not $Gateway) { $Gateway = (Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 1).NextHop }
    Write-Host "`nTesting path to $Gateway with $ProbeSize byte packet..."
    if (Test-MtuPath -Target $Gateway -Size $ProbeSize) {
        Write-Host "  PASS: path supports jumbo frames." -ForegroundColor Green
    } else {
        Write-Host "  FAIL: some hop limits MTU to <9000. Keep MTU 1500." -ForegroundColor Yellow
    }
    return
}

if ($Test) {
    if (-not $Gateway) { throw "Specify -Gateway to test." }
    Write-Host "End-to-end test ($Gateway, $ProbeSize B packet, DF set):"
    if (Test-MtuPath -Target $Gateway -Size $ProbeSize) {
        Write-Host "  PASSED — jumbo frames can be applied." -ForegroundColor Green
    } else {
        Write-Host "  FAILED — do not apply. Check switch/server." -ForegroundColor Red
    }
    return
}

Assert-Elevated

if ($Apply) {
    if (-not $Gateway) {
        $Gateway = (Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 1).NextHop
        Write-Host "Detected gateway: $Gateway"
    }

    Write-Host "Step 1/2 — conditional test..."
    if (-not (Test-MtuPath -Target $Gateway -Size $ProbeSize)) {
        Write-Host "ABORTED: path does NOT support MTU $Mtu. Nothing was changed." -ForegroundColor Red
        exit 1
    }
    Write-Host "  Path approved." -ForegroundColor Green

    if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir | Out-Null }

    $nic = Get-NicTarget
    $previousMtu = (Get-NetIPInterface -InterfaceAlias $nic.Name -AddressFamily IPv4).NlMtu
    @{
        adapter = $nic.Name; previousMtu = $previousMtu
        capturedAt = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json | Set-Content $BackupFile -Encoding UTF8

    if ($PSCmdlet.ShouldProcess($nic.Name, "MTU -> $Mtu")) {
        Set-NetIPInterface -InterfaceAlias $nic.Name -AddressFamily IPv4 -NlMtuByte $Mtu
        Write-Host "Step 2/2 — applied: $($nic.Name) MTU $previousMtu -> $Mtu" -ForegroundColor Green

        # Post-application validation: basic connectivity must remain alive
        if (-not (Test-MtuPath -Target $Gateway -Size 1200)) {
            Write-Host "ALERT: connectivity degraded after change! Rolling back..." -ForegroundColor Red
            Set-NetIPInterface -InterfaceAlias $nic.Name -AddressFamily IPv4 -NlMtuByte $previousMtu
            Write-Host "Rolled back to $previousMtu." -ForegroundColor Yellow
            exit 1
        }
        Write-Host "`nConnectivity OK. Expected gain on large sequential copies (~5-15%)."
        Write-Host "Validate: smbdoctor-cli scan --compare <baseline.json>"
    }
    return
}

if ($Rollback) {
    if (-not (Test-Path $BackupFile)) { throw "No backup found in $BackupFile — nothing to revert." }
    $b = Get-Content $BackupFile -Raw | ConvertFrom-Json
    Assert-Elevated
    Set-NetIPInterface -InterfaceAlias $b.adapter -AddressFamily IPv4 -NlMtuByte $b.previousMtu
    Remove-Item $BackupFile -Force
    Write-Host "Reverted: $($b.adapter) -> MTU $($b.previousMtu)" -ForegroundColor Green
}
