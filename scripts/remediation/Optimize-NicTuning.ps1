# Created by forg3
# License: MIT
#
# NIC Tuning Kit (Windows client)
# Optimizes CLIENT network stack for large SMB copies:
#   - RSS (Receive Side Scaling): distributes network interrupts across cores
#   - TCP Autotuning: dynamic window for high latency links
#   - NIC Power Management: prevents adapter sleep and throughput loss
#
# USAGE:
#   .\Optimize-NicTuning.ps1 -Status                # displays current state
#   .\Optimize-NicTuning.ps1 -Apply                 # applies optimizations (requires admin)
#   .\Optimize-NicTuning.ps1 -Rollback              # reverts to saved state
#   .\Optimize-NicTuning.ps1 -Apply -AdapterName "Ethernet"
#
# GAIN VALIDATION: run before and after
#   smbdoctor-cli.exe scan --save before.json --path \\server\share
#   (apply this script)
#   smbdoctor-cli.exe scan --compare before.json --path \\server\share

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Apply,
    [switch]$Rollback,
    [switch]$Status,
    [string]$AdapterName = ""
)

$ErrorActionPreference = 'Stop'
$BackupDir = Join-Path $env:ProgramData 'SmbSpeedDoctor'
$BackupFile = Join-Path $BackupDir 'nic-tuning-backup.json'

function Get-NicTargets {
    if ($AdapterName) {
        return Get-NetAdapter -Name $AdapterName -ErrorAction Stop | Where-Object Status -eq 'Up'
    }
    return Get-NetAdapter | Where-Object Status -eq 'Up' |
        Where-Object { $_.Virtual -eq $false }   # physical only
}

function Assert-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This command requires administrator elevation. Reopen PowerShell as admin."
    }
}

if ($Status -or (-not $Apply -and -not $Rollback)) {
    Write-Host "=== NIC TUNING — STATUS ===" -ForegroundColor Cyan
    Get-NicTargets | ForEach-Object {
        $rss = Get-NetAdapterRss -Name $_.Name -ErrorAction SilentlyContinue
        [pscustomobject]@{
            Adapter      = $_.Name
            LinkSpeed    = $_.LinkSpeed
            RssEnabled   = if ($rss) { $rss.Enabled } else { 'N/A' }
            RssQueues    = if ($rss) { "$($rss.NumberOfReceiveQueues) queues" } else { '-' }
            # if/else instead of ternary '? :' for PowerShell 5.1 compatibility
            PowerSaving  = if (Get-Member -InputObject $_ -Name AllowComputerToTurnOffDevice) {
                               (Get-NetAdapterPowerManagement -Name $_.Name -ErrorAction SilentlyContinue).AllowComputerToTurnOffDevice
                           } else { 'N/A' }
        } | Format-Table -AutoSize
    }
    # Get-NetTCPSetting is language-independent
    $autotune = $null
    try {
        $autotune = (Get-NetTCPSetting -SettingName Internet -ErrorAction Stop).AutoTuningLevelLocal
    }
    catch {
        $line = netsh int tcp show global |
                Select-String -Pattern 'Auto-Tuning|Autoajuste|Ajuste autom' -ErrorAction SilentlyContinue
        if ($line) { $autotune = $line.Line.Trim() }
    }
    if (-not $autotune) { $autotune = 'N/A (could not be determined)' }
    Write-Host "TCP Autotuning: $autotune"
    return
}

Assert-Elevated

if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir | Out-Null }

if ($Apply) {
    Write-Host "=== APPLYING NIC TUNING ===" -ForegroundColor Yellow

    # Capture current state for rollback
    $state = @{
        capturedAt = (Get-Date).ToUniversalTime().ToString('o')
        adapters   = @()
        autotuning = $null
    }
    foreach ($nic in Get-NicTargets) {
        $rss = Get-NetAdapterRss -Name $nic.Name -ErrorAction SilentlyContinue
        $pm  = Get-NetAdapterPowerManagement -Name $nic.Name -ErrorAction SilentlyContinue
        $state.adapters += [pscustomobject]@{
            name          = $nic.Name
            rssWasEnabled = if ($rss) { $rss.Enabled } else { $null }
            allowSleep    = if ($pm) { $pm.AllowComputerToTurnOffDevice } else { $null }
        }
    }
    $at = netsh int tcp show global | Select-String 'Receive Window Auto-Tuning Level'
    $state.autotuning = ($at.Line -split ':')[-1].Trim()
    $state | ConvertTo-Json -Depth 5 | Set-Content $BackupFile -Encoding UTF8
    Write-Host "State captured in $BackupFile"

    foreach ($nic in Get-NicTargets) {
        if ($PSCmdlet.ShouldProcess($nic.Name, "Enable RSS")) {
            try {
                Enable-NetAdapterRss -Name $nic.Name -NoRestart:$false
                Write-Host "  [OK] RSS enabled: $($nic.Name)" -ForegroundColor Green
            } catch { Write-Host "  [WARN] RSS not supported by $($nic.Name): $($_.Exception.Message)" -ForegroundColor Yellow }
        }
        if ($PSCmdlet.ShouldProcess($nic.Name, "Disable NIC power saving")) {
            try {
                $pm = Get-NetAdapterPowerManagement -Name $nic.Name -ErrorAction Stop
                $pm.AllowComputerToTurnOffDevice = 'Disabled'
                Set-NetAdapterPowerManagement -InputObject $pm
                Write-Host "  [OK] Power saving disabled: $($nic.Name)" -ForegroundColor Green
            } catch { Write-Host "  [WARN] Power management not available for $($nic.Name)" -ForegroundColor Yellow }
        }
    }

    if ($PSCmdlet.ShouldProcess('TCP global', 'Autotuning = normal')) {
        netsh int tcp set global autotuninglevel=normal | Out-Null
        Write-Host "  [OK] TCP autotuning = normal" -ForegroundColor Green
    }

    Write-Host "`nCompleted. Validate gain:" -ForegroundColor Cyan
    Write-Host "  smbdoctor-cli scan --compare <baseline.json> --path \\server\share"
    return
}

if ($Rollback) {
    if (-not (Test-Path $BackupFile)) {
        throw "No backup found in $BackupFile — nothing to revert. Run -Apply first."
    }
    $state = Get-Content $BackupFile -Raw | ConvertFrom-Json
    Write-Host "=== REVERTING NIC TUNING (captured at $($state.capturedAt)) ===" -ForegroundColor Yellow

    foreach ($a in $state.adapters) {
        if ($null -ne $a.rssWasEnabled) {
            if ($a.rssWasEnabled) { Enable-NetAdapterRss -Name $a.name }
            else { Disable-NetAdapterRss -Name $a.name }
            Write-Host "  [OK] RSS reverted: $($a.name) -> $a.rssWasEnabled"
        }
        if ($null -ne $a.allowSleep) {
            $pm = Get-NetAdapterPowerManagement -Name $a.name -ErrorAction SilentlyContinue
            if ($pm) {
                $pm.AllowComputerToTurnOffDevice = $a.allowSleep
                Set-NetAdapterPowerManagement -InputObject $pm
                Write-Host "  [OK] Power management reverted: $($a.name)"
            }
        }
    }
    if ($state.autotuning) {
        netsh int tcp set global autotuninglevel=$($state.autotuning) | Out-Null
        Write-Host "  [OK] Autotuning reverted -> $($state.autotuning)"
    }
    Remove-Item $BackupFile -Force
    Write-Host "Rollback completed." -ForegroundColor Green
}
