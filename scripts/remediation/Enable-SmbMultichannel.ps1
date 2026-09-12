# Created by forg3
# License: MIT
#
# SMB Multichannel (Windows client)
# Enables/disables/displays client SMB multichannel configuration.
#
# SMB Multichannel requires:
#   1) At least 2 active NICs (same VLAN or switch with LAG/RoCE)
#   2) RSS enabled on each adapter
#   3) EnableMultiChannel = $true in SmbClientConfiguration
#
# If only 1 NIC is available, multichannel will provide no gain —
# the script emits a YELLOW warning and refers to NIC tuning.
#
# USAGE:
#   .\Enable-SmbMultichannel.ps1 -Status
#   .\Enable-SmbMultichannel.ps1 -Apply
#   .\Enable-SmbMultichannel.ps1 -Apply -ComputerName SRV01
#   .\Enable-SmbMultichannel.ps1 -Rollback
#   .\Enable-SmbMultichannel.ps1 -WhatIf
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
    [string]$ComputerName = ""
)

$ErrorActionPreference = 'Stop'
$BackupDir = Join-Path $env:ProgramData 'SmbSpeedDoctor'
$BackupFile = Join-Path $BackupDir 'multichannel-backup.json'

function Assert-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This command requires administrator elevation. Reopen PowerShell as admin."
    }
}

function Get-ActiveNics {
    param([string]$Computer)
    if ($Computer) {
        return Get-NetAdapter -ComputerName $Computer | Where-Object { $_.Status -eq 'Up' -and $_.Virtual -eq $false }
    }
    return Get-NetAdapter | Where-Object { $_.Status -eq 'Up' -and $_.Virtual -eq $false }
}

function Get-MultichannelStatus {
    param([string]$Computer)
    $clientConfig = if ($Computer) {
        Get-SmbClientConfiguration -ComputerName $Computer -ErrorAction SilentlyContinue
    } else {
        Get-SmbClientConfiguration
    }
    
    $connections = if ($Computer) {
        Get-SmbMultichannelConnection -ComputerName $Computer -ErrorAction SilentlyContinue
    } else {
        Get-SmbMultichannelConnection -ErrorAction SilentlyContinue
    }
    
    # @() forces array: with ONE NIC return is scalar and $nics.Count was empty in PS 5.1
    $nics = @(Get-ActiveNics -Computer $Computer)

    $rssStatus = @{}
    foreach ($nic in $nics) {
        try {
            if ($Computer) {
                $rss = Get-NetAdapterRss -ComputerName $Computer -Name $nic.Name -ErrorAction SilentlyContinue
            } else {
                $rss = Get-NetAdapterRss -Name $nic.Name -ErrorAction SilentlyContinue
            }
            $rssStatus[$nic.Name] = @{ Enabled = if ($rss) { $rss.Enabled } else { $false } }
        } catch {
            $rssStatus[$nic.Name] = @{ Enabled = $false; Error = $_.Exception.Message }
        }
    }
    
    [pscustomobject]@{
        ComputerName       = if ($Computer) { $Computer } else { $env:COMPUTERNAME }
        EnableMultiChannel = if ($clientConfig) { $clientConfig.EnableMultiChannel } else { $null }
        ActiveNicsCount    = $nics.Count
        Nics               = $nics | ForEach-Object { $_.Name }
        RssStatus          = $rssStatus
        ActiveConnections  = if ($connections) { $connections.Count } else { 0 }
    }
}

function Save-Backup {
    param($value)
    if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir | Out-Null }
    $backup = @{
        Timestamp    = Get-Date -Format 'yyyy-MM-ddTHH:mm:ssK'
        ComputerName = $env:COMPUTERNAME
        Value        = $value
    }
    $backup | ConvertTo-Json | Out-File -FilePath $BackupFile -Encoding utf8
}

function Load-Backup {
    if (Test-Path $BackupFile) {
        return (Get-Content $BackupFile -Raw | ConvertFrom-Json).Value
    }
    return $null
}

# --- STATUS ---
if ($Status -or (-not $Apply -and -not $Rollback)) {
    Write-Host "=== SMB MULTICHANNEL — STATUS ===" -ForegroundColor Cyan
    
    $info = Get-MultichannelStatus -Computer $ComputerName
    
    Write-Host ""
    Write-Host "Computer     : $($info.ComputerName)" -ForegroundColor White
    Write-Host "MultiChannel : $(if ($info.EnableMultiChannel) { 'ENABLED' } else { 'DISABLED' })" -ForegroundColor White
    
    Write-Host ""
    Write-Host "--- Active adapters ($($info.ActiveNicsCount)) ---" -ForegroundColor Yellow
    foreach ($nic in $info.Nics) {
        $rss = $info.RssStatus[$nic]
        $rssStr = if ($rss) { if ($rss.Enabled) { 'RSS ON' } else { 'RSS OFF' } } else { 'N/A' }
        Write-Host "  [$nic] $rssStr"
    }
    
    Write-Host ""
    Write-Host "--- Active SMB connections ($($info.ActiveConnections)) ---" -ForegroundColor Yellow
    if ($info.ActiveConnections -gt 0) {
        $conns = if ($ComputerName) {
            Get-SmbMultichannelConnection -ComputerName $ComputerName -ErrorAction SilentlyContinue
        } else {
            Get-SmbMultichannelConnection -ErrorAction SilentlyContinue
        }
        $conns | Format-Table -AutoSize
    } else {
        Write-Host "  No multichannel connections currently active." -ForegroundColor Gray
    }
    
    # Warning if only 1 NIC
    if ($info.ActiveNicsCount -lt 2) {
        Write-Host "" -ForegroundColor Yellow
        Write-Host "WARNING: Only $($info.ActiveNicsCount) active NIC detected." -ForegroundColor Yellow
        Write-Host "  Multichannel requires 2+ physical paths (NICs or switch with LAG/RoCE)." -ForegroundColor Yellow
        Write-Host "  No gain expected. Consider NIC Tuning to optimize existing interface." -ForegroundColor Yellow
        Write-Host ""
    } elseif ($info.EnableMultiChannel) {
        Write-Host "Multichannel enabled. Check active connections above." -ForegroundColor Green
    } else {
        Write-Host "Multichannel disabled. Use -Apply to enable." -ForegroundColor Gray
    }
    
    return
}

# --- APPLY / ROLLBACK require elevation ---
Assert-Elevated

if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir | Out-Null }

if ($Apply) {
    $current = Get-SmbClientConfiguration
    
    if ($PSCmdlet.ShouldProcess("SmbClientConfiguration", "Enable EnableMultiChannel")) {
        # Backup current state
        Save-Backup -value $current.EnableMultiChannel
        
        if (-not $current.EnableMultiChannel) {
            # -Confirm:$false: prevents interactive prompt hanging RMM sessions
            Set-SmbClientConfiguration -EnableMultiChannel $true -ErrorAction Stop -Confirm:$false
            Write-Host "EnableMultiChannel set to $true" -ForegroundColor Green
        } else {
            Write-Host "EnableMultiChannel was already $true — no changes needed." -ForegroundColor Cyan
        }
        
        # Post-application verification
        $verify = Get-SmbClientConfiguration
        Write-Host "   Confirmed state: EnableMultiChannel = $($verify.EnableMultiChannel)" -ForegroundColor Gray
    }
    
    # Feasibility check
    $nics = @(Get-ActiveNics)
    if ($nics.Count -lt 2) {
        Write-Host "" -ForegroundColor Yellow
        Write-Host "WARNING: Only $($nics.Count) active NIC detected." -ForegroundColor Yellow
        Write-Host "  Multichannel requires 2+ physical paths (NICs or switch with LAG/RoCE)." -ForegroundColor Yellow
        Write-Host "  Configure RSS and consider NIC Tuning for optimal utilization." -ForegroundColor Yellow
        Write-Host ""
    } else {
        # Check RSS on each NIC
        $rssOff = @()
        foreach ($nic in $nics) {
            try {
                $rss = Get-NetAdapterRss -Name $nic.Name -ErrorAction Stop
                if (-not $rss.Enabled) { $rssOff += $nic.Name }
            } catch {
                $rssOff += "$($nic.Name) (error: $_)"
            }
        }
        if ($rssOff.Count -gt 0) {
            Write-Host "WARNING: RSS disabled/not found on: $($rssOff -join ', ')" -ForegroundColor Yellow
            Write-Host "  Enable RSS for multichannel to function properly." -ForegroundColor Yellow
            Write-Host ""
        } else {
            Write-Host "Multichannel enabled successfully. $($nics.Count) active NIC(s) detected." -ForegroundColor Green
        }
    }
}

if ($Rollback) {
    $backup = Load-Backup
    if ($backup -eq $null) {
        Write-Host "No backup found in $BackupFile" -ForegroundColor Red
        exit 1
    }
    
    if ($PSCmdlet.ShouldProcess("SmbClientConfiguration", "Revert EnableMultiChannel to previous state")) {
        Set-SmbClientConfiguration -EnableMultiChannel $backup -ErrorAction Stop -Confirm:$false
        Write-Host "EnableMultiChannel reverted to $backup (backup from $($backup.Timestamp))" -ForegroundColor Green
    }
}
