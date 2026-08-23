# Criado por André Santo (forg3) | junkyardgoodies.app
# Licença: MIT
#
# Item 6 — NIC Tuning Kit (cliente Windows)
# Otimiza a pilha de rede do CLIENTE para cópias SMB grandes:
#   - RSS (Receive Side Scaling): espalha interrupções de rede entre cores
#   - TCP Autotuning: janela dinâmica para enlaces com latência
#   - Power management da NIC: evita que o adaptador "durma" e perca throughput
#
# USO:
#   .\Optimize-NicTuning.ps1 -Status                # só mostra o estado atual
#   .\Optimize-NicTuning.ps1 -Apply                 # aplica otimizações (exige admin)
#   .\Optimize-NicTuning.ps1 -Rollback              # reverte ao estado salvo
#   .\Optimize-NicTuning.ps1 -Apply -AdapterName "Ethernet"
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
        Where-Object { $_.Virtual -eq $false }   # apenas físicas
}

function Assert-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Este comando exige elevação de administrador. Reabra o PowerShell como admin."
    }
}

if ($Status -or (-not $Apply -and -not $Rollback)) {
    Write-Host "=== NIC TUNING — STATUS ===" -ForegroundColor Cyan
    Get-NicTargets | ForEach-Object {
        $rss = Get-NetAdapterRss -Name $_.Name -ErrorAction SilentlyContinue
        [pscustomobject]@{
            Adapter      = $_.Name
            LinkSpeed    = $_.LinkSpeed
            RssEnabled   = if ($rss) { $rss.Enabled } else { 'n/d' }
            RssQueues    = if ($rss) { "$($rss.NumberOfReceiveQueues) filas" } else { '-' }
            # if/else em vez do operador ternario '? :': ternario so existe no
            # PowerShell 7, e este kit precisa rodar no Windows PowerShell 5.1,
            # que e o shell padrao do Windows 10/11 e o usado por RMM.
            PowerSaving  = if (Get-Member -InputObject $_ -Name AllowComputerToTurnOffDevice) {
                               (Get-NetAdapterPowerManagement -Name $_.Name -ErrorAction SilentlyContinue).AllowComputerToTurnOffDevice
                           } else { 'n/d' }
        } | Format-Table -AutoSize
    }
    # Get-NetTCPSetting e independente de idioma. O netsh imprime texto
    # LOCALIZADO: procurar 'Receive Window Auto-Tuning' nao casa num Windows em
    # portugues, $autotune vinha $null e o .Trim() lancava
    # "chamar um metodo em uma expressao de valor nulo".
    $autotune = $null
    try {
        $autotune = (Get-NetTCPSetting -SettingName Internet -ErrorAction Stop).AutoTuningLevelLocal
    }
    catch {
        $line = netsh int tcp show global |
                Select-String -Pattern 'Auto-Tuning|Autoajuste|Ajuste autom' -ErrorAction SilentlyContinue
        if ($line) { $autotune = $line.Line.Trim() }
    }
    if (-not $autotune) { $autotune = 'n/d (nao foi possivel determinar)' }
    Write-Host "TCP Autotuning: $autotune"
    return
}

Assert-Elevated

if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir | Out-Null }

if ($Apply) {
    Write-Host "=== APLICANDO NIC TUNING ===" -ForegroundColor Yellow

    # Captura estado atual para rollback
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
    Write-Host "Estado capturado em $BackupFile"

    foreach ($nic in Get-NicTargets) {
        if ($PSCmdlet.ShouldProcess($nic.Name, "Habilitar RSS")) {
            try {
                Enable-NetAdapterRss -Name $nic.Name -NoRestart:$false
                Write-Host "  [OK] RSS habilitado: $($nic.Name)" -ForegroundColor Green
            } catch { Write-Host "  [AVISO] RSS não suportado por $($nic.Name): $($_.Exception.Message)" -ForegroundColor Yellow }
        }
        if ($PSCmdlet.ShouldProcess($nic.Name, "Desativar power-saving da NIC")) {
            try {
                $pm = Get-NetAdapterPowerManagement -Name $nic.Name -ErrorAction Stop
                $pm.AllowComputerToTurnOffDevice = 'Disabled'
                Set-NetAdapterPowerManagement -InputObject $pm
                Write-Host "  [OK] Power-saving desativado: $($nic.Name)" -ForegroundColor Green
            } catch { Write-Host "  [AVISO] Power management não disponível para $($nic.Name)" -ForegroundColor Yellow }
        }
    }

    if ($PSCmdlet.ShouldProcess('TCP global', 'Autotuning = normal')) {
        netsh int tcp set global autotuninglevel=normal | Out-Null
        Write-Host "  [OK] TCP autotuning = normal" -ForegroundColor Green
    }

    Write-Host "`nConcluído. Valide o ganho:" -ForegroundColor Cyan
    Write-Host "  smbdoctor-cli scan --compare <baseline.json> --path \\servidor\share"
    return
}

if ($Rollback) {
    if (-not (Test-Path $BackupFile)) {
        throw "Nenhum backup encontrado em $BackupFile — nada para reverter. Rode -Apply primeiro."
    }
    $state = Get-Content $BackupFile -Raw | ConvertFrom-Json
    Write-Host "=== REVERTENDO NIC TUNING (capturado em $($state.capturedAt)) ===" -ForegroundColor Yellow

    foreach ($a in $state.adapters) {
        if ($null -ne $a.rssWasEnabled) {
            if ($a.rssWasEnabled) { Enable-NetAdapterRss -Name $a.name }
            else { Disable-NetAdapterRss -Name $a.name }
            Write-Host "  [OK] RSS revertido: $($a.name) -> $a.rssWasEnabled"
        }
        if ($null -ne $a.allowSleep) {
            $pm = Get-NetAdapterPowerManagement -Name $a.name -ErrorAction SilentlyContinue
            if ($pm) {
                $pm.AllowComputerToTurnOffDevice = $a.allowSleep
                Set-NetAdapterPowerManagement -InputObject $pm
                Write-Host "  [OK] Power management revertido: $($a.name)"
            }
        }
    }
    if ($state.autotuning) {
        netsh int tcp set global autotuninglevel=$($state.autotuning) | Out-Null
        Write-Host "  [OK] Autotuning revertido -> $($state.autotuning)"
    }
    Remove-Item $BackupFile -Force
    Write-Host "Reversão concluída." -ForegroundColor Green
}
