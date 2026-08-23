# Criado por André Santo (forg3) | junkyardgoodies.app
# Licença: MIT
#
# Item 7 — Jumbo Frames condicional
# NUNCA ativa jumbo frames às cegas: primeiro testa se MTU 9000 passa fim-a-fim,
# só então aplica na interface. MTU inconsistente ao longo do caminho causa
# fragmentação (pior que 1500) ou perda total de conectividade.
#
# USO:
#   .\Enable-JumboFrames.ps1 -Test                          # testa sem aplicar
#   .\Enable-JumboFrames.ps1 -Apply -Gateway 192.168.0.1    # testa e aplica se passar
#   .\Enable-JumboFrames.ps1 -Rollback                      # volta para 1500
#   .\Enable-JumboFrames.ps1 -Status
#
# PRÉ-REQUISITO DE REDE: TODOS os saltos (NIC, switch, servidor) precisam suportar 9000.
# Se o destino for um share via VPN (Tailscale/WireGuard), jumbo NÃO se aplica.

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Test,
    [switch]$Apply,
    [switch]$Rollback,
    [switch]$Status,
    [string]$Gateway = "",
    [string]$AdapterName = "",
    [int]$Mtu = 9000,
    [int]$ProbeSize = 8972   # 9000 - 28 bytes de cabeçalho IP+ICMP
)

$ErrorActionPreference = 'Stop'
$BackupDir = Join-Path $env:ProgramData 'SmbSpeedDoctor'
$BackupFile = Join-Path $BackupDir 'jumbo-backup.json'

function Get-NicTarget {
    if ($AdapterName) { return Get-NetAdapter -Name $AdapterName | Where-Object Status -eq 'Up' }
    # Interface com rota default = a física de verdade
    $gw = (Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 1).InterfaceAlias
    return Get-NetAdapter -Name $gw
}

function Test-MtuPath {
    param([string]$Target, [int]$Size)
    # -f = não fragmentar: se o pacote não passa inteiro, falha honestamente
    $result = ping -n 3 -f -l $Size $Target 2>&1
    $lost = ($result | Select-String 'Perdidos: 3|Lost = 3|100% loss|100% de perda').Count
    return ($lost -eq 0)
}

function Assert-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Exige elevação de administrador."
    }
}

if ($Status) {
    Write-Host "=== JUMBO FRAMES — STATUS ===" -ForegroundColor Cyan
    Get-NicTarget | ForEach-Object {
        $mtuAtual = (Get-NetIPInterface -InterfaceAlias $_.Name -AddressFamily IPv4).NlMtu
        Write-Host ("{0}: MTU = {1}" -f $_.Name, $mtuAtual)
    }
    if (-not $Gateway) { $Gateway = (Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 1).NextHop }
    Write-Host "`nTestando caminho até $Gateway com pacote $ProbeSize bytes..."
    if (Test-MtuPath -Target $Gateway -Size $ProbeSize) {
        Write-Host "  PASSA: caminho suporta jumbo frames." -ForegroundColor Green
    } else {
        Write-Host "  NÃO PASSA: algum salto limita a <9000. Mantenha MTU 1500." -ForegroundColor Yellow
    }
    return
}

if ($Test) {
    if (-not $Gateway) { throw "Informe -Gateway para testar." }
    Write-Host "Teste fim-a-fim ($Gateway, pacote $ProbeSize B, DF set):"
    if (Test-MtuPath -Target $Gateway -Size $ProbeSize) {
        Write-Host "  APROVADO — pode aplicar jumbo frames." -ForegroundColor Green
    } else {
        Write-Host "  REPROVADO — não aplique. Verifique switch/servidor." -ForegroundColor Red
    }
    return
}

Assert-Elevated

if ($Apply) {
    if (-not $Gateway) {
        $Gateway = (Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric | Select-Object -First 1).NextHop
        Write-Host "Gateway detectado: $Gateway"
    }

    Write-Host "Passo 1/2 — teste condicional..."
    if (-not (Test-MtuPath -Target $Gateway -Size $ProbeSize)) {
        Write-Host "ABORTADO: caminho NÃO suporta MTU $Mtu. Nada foi alterado." -ForegroundColor Red
        exit 1
    }
    Write-Host "  Caminho aprovado." -ForegroundColor Green

    if (-not (Test-Path $BackupDir)) { New-Item -ItemType Directory -Path $BackupDir | Out-Null }

    $nic = Get-NicTarget
    $mtuAntigo = (Get-NetIPInterface -InterfaceAlias $nic.Name -AddressFamily IPv4).NlMtu
    @{
        adapter = $nic.Name; previousMtu = $mtuAntigo
        capturedAt = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json | Set-Content $BackupFile -Encoding UTF8

    if ($PSCmdlet.ShouldProcess($nic.Name, "MTU -> $Mtu")) {
        Set-NetIPInterface -InterfaceAlias $nic.Name -AddressFamily IPv4 -NlMtuByte $Mtu
        Write-Host "Passo 2/2 — aplicado: $($nic.Name) MTU $mtuAntigo -> $Mtu" -ForegroundColor Green

        # Validação pós-aplicação: conectividade básica precisa continuar de pé
        if (-not (Test-MtuPath -Target $Gateway -Size 1200)) {
            Write-Host "ALERTA: conectividade degradada após mudança! Revertendo..." -ForegroundColor Red
            Set-NetIPInterface -InterfaceAlias $nic.Name -AddressFamily IPv4 -NlMtuByte $mtuAntigo
            Write-Host "Revertido para $mtuAntigo." -ForegroundColor Yellow
            exit 1
        }
        Write-Host "`nConectividade OK. Ganho esperado em cópias sequenciais grandes (~5-15%)."
        Write-Host "Valide: smbdoctor-cli scan --compare <baseline.json>"
    }
    return
}

if ($Rollback) {
    if (-not (Test-Path $BackupFile)) { throw "Sem backup em $BackupFile — nada para reverter." }
    $b = Get-Content $BackupFile -Raw | ConvertFrom-Json
    Assert-Elevated
    Set-NetIPInterface -InterfaceAlias $b.adapter -AddressFamily IPv4 -NlMtuByte $b.previousMtu
    Remove-Item $BackupFile -Force
    Write-Host "Revertido: $($b.adapter) -> MTU $($b.previousMtu)" -ForegroundColor Green
}
