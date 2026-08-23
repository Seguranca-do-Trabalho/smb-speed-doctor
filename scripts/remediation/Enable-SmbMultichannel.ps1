# Criado por André Santo (forg3) | junkyardgoodies.app
# Licença: MIT
#
# Item 5 — SMB Multichannel (cliente Windows)
# Habilita/desabilita/mostra a configuração de multicanal SMB do cliente.
#
# Multichannel SMB requer:
#   1) Pelo menos 2 NICs ativas (mesma VLAN ou switch com LAG/RoCE)
#   2) RSS habilitado em cada adaptador
#   3) EnableMultiChannel = $true no SmbClientConfiguration
#
# Se apenas 1 NIC estiver disponível, o multichannel não trará ganho —
# o script emite aviso AMARELO e aponta para o Item 6 (NIC tuning).
#
# USO:
#   .\Enable-SmbMultichannel.ps1 -Status
#   .\Enable-SmbMultichannel.ps1 -Apply
#   .\Enable-SmbMultichannel.ps1 -Apply -ComputerName SRV01
#   .\Enable-SmbMultichannel.ps1 -Rollback
#   .\Enable-SmbMultichannel.ps1 -WhatIf
#
# VALIDAÇÃO DO GANHO: rode antes e depois
#   smbdoctor-cli.exe scan --save antes.json --path \\servidor\share
#   (aplica este script)
#   smbdoctor-cli.exe scan --compare antes.json --path \\servidor\share

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
        throw "Este comando exige elevação de administrador. Reabra o PowerShell como admin."
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
    
    # @() forca array: com UMA NIC o retorno e escalar e $nics.Count sai VAZIO
    # no Windows PowerShell 5.1 — o status imprimia "Apenas  NIC ativa detectada".
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
    Write-Host "Computador : $($info.ComputerName)" -ForegroundColor White
    Write-Host "MultiChannel : $(if ($info.EnableMultiChannel) { 'HABILITADO' } else { 'DESABILITADO' })" -ForegroundColor White
    
    Write-Host ""
    Write-Host "--- Adaptadores ativos ($($info.ActiveNicsCount)) ---" -ForegroundColor Yellow
    foreach ($nic in $info.Nics) {
        $rss = $info.RssStatus[$nic]
        $rssStr = if ($rss) { if ($rss.Enabled) { 'RSS ON' } else { 'RSS OFF' } } else { 'n/d' }
        Write-Host "  [$nic] $rssStr"
    }
    
    Write-Host ""
    Write-Host "--- Conexões SMB ativas ($($info.ActiveConnections)) ---" -ForegroundColor Yellow
    if ($info.ActiveConnections -gt 0) {
        Get-MultichannelStatus -Computer $ComputerName | ForEach-Object {
            # connections info apenas exibida se existirem
        }
        $conns = if ($ComputerName) {
            Get-SmbMultichannelConnection -ComputerName $ComputerName -ErrorAction SilentlyContinue
        } else {
            Get-SmbMultichannelConnection -ErrorAction SilentlyContinue
        }
        $conns | Format-Table -AutoSize
    } else {
        Write-Host "  Nenhuma conexão multicanal ativa no momento." -ForegroundColor Gray
    }
    
    # Aviso se apenas 1 NIC
    if ($info.ActiveNicsCount -lt 2) {
        Write-Host "" -ForegroundColor Yellow
        Write-Host "⚠ AVISO: Apenas $($info.ActiveNicsCount) NIC ativa detectada." -ForegroundColor Yellow
        Write-Host "  Multichannel requer 2+ caminhos reais (NICs ou switch com LAG/RoCE)." -ForegroundColor Yellow
        Write-Host "  Sem ganho esperado. Considere o Item 6 (NIC Tuning) para otimizar a interface existente." -ForegroundColor Yellow
        Write-Host ""
    } elseif ($info.EnableMultiChannel) {
        Write-Host "✅ Multichannel habilitado. Verifique conexões ativas acima." -ForegroundColor Green
    } else {
        Write-Host "ℹ Multichannel desabilitado. Use -Apply para habilitar." -ForegroundColor Gray
    }
    
    return
}

# --- APPLY / ROLLBACK exigem elevação ---
Assert-Elevated

if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir | Out-Null }

if ($Apply) {
    $current = Get-SmbClientConfiguration
    
    if ($PSCmdlet.ShouldProcess("SmbClientConfiguration", "Habilitar EnableMultiChannel")) {
        # Backup do estado atual
        Save-Backup -value $current.EnableMultiChannel
        
        if (-not $current.EnableMultiChannel) {
            # -Confirm:$false: sem isto o cmdlet abre prompt interativo e o
            # script trava quando rodado por RMM ou sessao nao-interativa.
            Set-SmbClientConfiguration -EnableMultiChannel $true -ErrorAction Stop -Confirm:$false
            Write-Host "✅ EnableMultiChannel definido como $true" -ForegroundColor Green
        } else {
            Write-Host "ℹ EnableMultiChannel já estava $true — nenhuma alteração necessária." -ForegroundColor Cyan
        }
        
        # Verificação pós-aplicação
        $verify = Get-SmbClientConfiguration
        Write-Host "   Estado confirmado: EnableMultiChannel = $($verify.EnableMultiChannel)" -ForegroundColor Gray
    }
    
    # Aviso de viabilidade — @() pelo mesmo motivo do Get-Status
    $nics = @(Get-ActiveNics)
    if ($nics.Count -lt 2) {
        Write-Host "" -ForegroundColor Yellow
        Write-Host "⚠ AVISO: Apenas $($nics.Count) NIC ativa detectada." -ForegroundColor Yellow
        Write-Host "  Multichannel requer 2+ caminhos reais (NICs ou switch com LAG/RoCE)." -ForegroundColor Yellow
        Write-Host "  Configure RSS e considere o Item 6 (NIC Tuning) para melhor aproveitamento." -ForegroundColor Yellow
        Write-Host ""
    } else {
        # Verificar RSS em cada NIC
        $rssOff = @()
        foreach ($nic in $nics) {
            try {
                $rss = Get-NetAdapterRss -Name $nic.Name -ErrorAction Stop
                if (-not $rss.Enabled) { $rssOff += $nic.Name }
            } catch {
                $rssOff += "$($nic.Name) (erro: $_)"
            }
        }
        if ($rssOff.Count -gt 0) {
            Write-Host "⚠ AVISO: RSS desabilitado/não encontrado em: $($rssOff -join ', ')" -ForegroundColor Yellow
            Write-Host "  Habilite RSS para que o multichannel funcione adequadamente." -ForegroundColor Yellow
            Write-Host ""
        } else {
            Write-Host "✅ Multichannel habilitado com sucesso. $($nics.Count) NIC(s) ativa(s) detectada(s)." -ForegroundColor Green
        }
    }
}

if ($Rollback) {
    $backup = Load-Backup
    if ($backup -eq $null) {
        Write-Host "❌ Nenhum backup encontrado em $BackupFile" -ForegroundColor Red
        exit 1
    }
    
    if ($PSCmdlet.ShouldProcess("SmbClientConfiguration", "Reverter EnableMultiChannel para estado anterior")) {
        # -Confirm:$false — o rollback nao pode travar num prompt.
        Set-SmbClientConfiguration -EnableMultiChannel $backup -ErrorAction Stop -Confirm:$false
        Write-Host "✅ EnableMultiChannel revertido para $backup (backup de $($backup.Timestamp))" -ForegroundColor Green
    }
}
