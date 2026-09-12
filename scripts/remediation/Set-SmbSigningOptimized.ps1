<#
.SYNOPSIS
    SMB Signing Tuner — removes client SMB signing requirement.
    WARNING: effect is GLOBAL for ALL connections from this machine.

.DESCRIPTION
    Windows 11 24H2 made RequireSecuritySignature mandatory by default,
    typically causing drops from ~110 MB/s to ~38 MB/s in trusted internal
    networks. This script:
      - Removes the requirement (RequireSecuritySignature $false)
      - Keeps signing NEGOTIABLE (EnableSecuritySignature $true), so the server can still require it
      - Saves previous state for rollback

    SCOPE — read before using:
    Set-SmbClientConfiguration is a MACHINE-WIDE configuration. Windows does
    not offer per-subnet client SMB signing policies. Therefore there is NO
    way to limit this setting to a trusted network: upon applying, the signing
    requirement drops for all SMB connections from this machine — public Wi-Fi,
    VPN, DMZ, hotel, any connection.

    If differentiated posture across networks is required, containment must come
    from network topology (dedicated NIC/VLAN for trusted traffic) or administrative
    policies — not from this cmdlet.

    HOW TO VALIDATE:
      1) Before: smbdoctor-cli.exe scan --json --path \\server\share > before.json
      2) Run: .\Set-SmbSigningOptimized.ps1 -Apply -AcceptGlobalSecurityImpact
      3) After: smbdoctor-cli.exe scan --json --path \\server\share > after.json
      4) Compare SigningEnabled and throughput between before/after.

    Note: measure with --path. Without a real copy there is no throughput measurement
    and no basis to state that signing is the bottleneck.

.AUTHOR
    Created by forg3

.LICENSE
    MIT License — see LICENSE in repository.

.RISK
    SMB signing protects against tampering and relay/MITM attacks. Without the
    requirement, a network-positioned attacker may attempt to downgrade the
    session. Because the effect is machine-wide, only apply on machines that do
    not leave a controlled network segment. On laptops traveling through untrusted
    networks, DO NOT apply.
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $false)]
    [switch]$Apply,

    [Parameter(Mandatory = $false)]
    [switch]$Rollback,

    [Parameter(Mandatory = $false)]
    [switch]$Status,

    # Explicit acknowledgment that effect is global (all SMB connections from this machine).
    # Required with -Apply to ensure conscious decision recorded in command history and RMM logs.
    [Parameter(Mandatory = $false)]
    [switch]$AcceptGlobalSecurityImpact
)

$ErrorActionPreference = 'Stop'
$BackupDir = Join-Path ${env:ProgramData} 'SmbSpeedDoctor'
$BackupFile = Join-Path $BackupDir 'signing-backup.json'

# ── Elevation check ──────────────────────────────────────────────────────────
function Test-Admin {
    $current = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($current)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    Write-Error "This script requires elevation (Run as Administrator)."
    exit 1
}

# ── Status ────────────────────────────────────────────────────────────────────
if ($Status) {
    Write-Host "=== Current SMB Signing Status ===" -ForegroundColor Cyan
    $config = Get-SmbClientConfiguration
    Write-Host "RequireSecuritySignature : $($config.RequireSecuritySignature)"
    Write-Host "EnableSecuritySignature  : $($config.EnableSecuritySignature)"
    Write-Host ""
    Write-Host "Backup available: $(if (Test-Path $BackupFile) { 'YES' } else { 'NO' })"
    if (Test-Path $BackupFile) {
        Write-Host "Backup contents:"
        Get-Content $BackupFile | ForEach-Object { Write-Host "  $_" }
    }
    exit 0
}

# ── Helper: Capture current state ─────────────────────────────────────────────
function Save-State {
    if (-not (Test-Path $BackupDir)) {
        New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null
    }
    $state = Get-SmbClientConfiguration | Select-Object RequireSecuritySignature, EnableSecuritySignature
    $state | ConvertTo-Json | Out-File -FilePath $BackupFile -Encoding UTF8
    Write-Host "State saved to: $BackupFile" -ForegroundColor Green
}

# ── Helper: Restore state ─────────────────────────────────────────────────────
function Restore-State {
    if (-not (Test-Path $BackupFile)) {
        Write-Error "No backup found in $BackupFile"
        exit 1
    }
    $state = Get-Content $BackupFile | ConvertFrom-Json
    Write-Host "Restoring state: $($state | ConvertTo-Json)" -ForegroundColor Yellow
    # -Confirm:$false prevents interactive prompt hanging non-interactive sessions
    Set-SmbClientConfiguration -RequireSecuritySignature $state.RequireSecuritySignature -Confirm:$false
    Set-SmbClientConfiguration -EnableSecuritySignature $state.EnableSecuritySignature -Confirm:$false
    Write-Host "State restored successfully." -ForegroundColor Green
}

# ── Apply ─────────────────────────────────────────────────────────────────────
if ($Apply) {
    if (-not $AcceptGlobalSecurityImpact) {
        Write-Error @'
Rejected: this configuration change is GLOBAL, not per-subnet.

Set-SmbClientConfiguration applies machine-wide. When applied, the SMB signing
requirement drops for ALL connections from this machine, including untrusted
networks (public Wi-Fi, VPN, DMZ).

If this is acceptable for this machine, repeat with -AcceptGlobalSecurityImpact:
  .\Set-SmbSigningOptimized.ps1 -Apply -AcceptGlobalSecurityImpact
'@
        exit 1
    }

    Write-Host "=== Removing SMB signing requirement (GLOBAL effect) ===" -ForegroundColor Cyan
    Write-Host "WARNING: applies to all SMB connections from this machine." -ForegroundColor Yellow
    Write-Host "Rationale: Windows 11 24H2 requires signing by default, which on trusted LAN"
    Write-Host "  causes significant overhead (~65% throughput loss)."
    Write-Host ""

    # Backup before altering
    Save-State

    # RequireSecuritySignature $false -> removes requirement (global effect)
    # EnableSecuritySignature $true   -> keeps negotiable (server can still require)
    if ($PSCmdlet.ShouldProcess("SMB Client Configuration (entire machine)",
                                "Remove SMB signing requirement")) {
        Set-SmbClientConfiguration -RequireSecuritySignature $false -Confirm:$false
        Set-SmbClientConfiguration -EnableSecuritySignature $true -Confirm:$false
        Write-Host "Configuration applied:" -ForegroundColor Green
        Write-Host "  RequireSecuritySignature = false (not required) — ALL connections"
        Write-Host "  EnableSecuritySignature  = true  (negotiable)"
    }

    Write-Host ""
    Write-Host "=== Recommended validation ===" -ForegroundColor Yellow
    Write-Host "Execute: smbdoctor-cli.exe scan --json"
    Write-Host "Compare with previous backup (save before/after for diff)."
    exit 0
}

# ── Rollback ──────────────────────────────────────────────────────────────────
if ($Rollback) {
    Write-Host "=== Rolling back SMB Signing ===" -ForegroundColor Cyan
    Restore-State
    exit 0
}

# ── Without valid parameter ───────────────────────────────────────────────────
Write-Host "Usage: Set-SmbSigningOptimized.ps1 [-Apply -AcceptGlobalSecurityImpact | -Rollback | -Status]" -ForegroundColor Yellow
Write-Host "  -Apply                        Removes signing requirement (back up first)"
Write-Host "  -AcceptGlobalSecurityImpact   Required with -Apply: acknowledges that the"
Write-Host "                                effect is GLOBAL (all SMB connections on machine)"
Write-Host "  -Rollback                     Restores previous state from backup"
Write-Host "  -Status                       Displays current configuration and backup status"
exit 0
