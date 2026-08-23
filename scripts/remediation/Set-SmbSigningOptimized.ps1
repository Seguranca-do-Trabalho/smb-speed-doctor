<#
.SYNOPSIS
    SMB Signing Tuner — remove a obrigatoriedade de assinatura SMB do cliente.
    ATENÇÃO: o efeito é GLOBAL, para TODAS as conexões desta máquina.

.DESCRIPTION
    Windows 11 24H2 tornou RequireSecuritySignature obrigatório por padrão,
    causando queda típica de ~110 MB/s para ~38 MB/s em redes internas
    confiáveis. Este script:
      - Remove a obrigatoriedade (RequireSecuritySignature $false)
      - Mantém a assinatura NEGOCIÁVEL (EnableSecuritySignature $true), de modo
        que o servidor ainda pode exigi-la
      - Salva o estado anterior para rollback

    ESCOPO — leia antes de usar:
    Set-SmbClientConfiguration é uma configuração DE MÁQUINA. O Windows não
    oferece política de assinatura SMB por sub-rede no cliente. Portanto NÃO há
    como limitar este ajuste a uma rede confiável: ao aplicar, a exigência de
    assinatura cai para toda conexão SMB desta máquina — Wi-Fi público, VPN,
    DMZ, hotel, qualquer uma.

    Versões anteriores deste script aceitavam um parâmetro -Subnet e diziam
    aplicar "tuning escopado". Isso era FALSO: o valor nunca era usado em nada
    além de uma mensagem na tela, enquanto o efeito real sempre foi global. O
    parâmetro foi removido para não induzir a uma falsa sensação de contenção.

    Se você precisa de postura diferenciada por rede, a contenção tem de vir da
    topologia (interface/VLAN dedicada ao tráfego confiável) ou de política
    aplicada por escopo administrativo — não deste cmdlet.

    COMO VALIDAR:
      1) Antes: smbdoctor-cli.exe scan --json --path \\servidor\share > before.json
      2) Execute: .\Set-SmbSigningOptimized.ps1 -Apply -AcceptGlobalSecurityImpact
      3) Depois: smbdoctor-cli.exe scan --json --path \\servidor\share > after.json
      4) Compare SigningEnabled e throughput entre before/after.

    Observação: meça com --path. Sem cópia real não há medição de throughput e
    não há como afirmar que a assinatura é o gargalo.

.AUTHOR
    Criado por André Santo (forg3) | junkyardgoodies.app

.LICENSE
    MIT License — veja LICENSE no repositório.

.RISK
    A assinatura SMB protege contra adulteração e ataques de relay/MITM. Sem a
    obrigatoriedade, um atacante em posição de rede pode tentar rebaixar a
    sessão. Como o efeito aqui é global e não por sub-rede, só aplique em
    máquinas que não saem de um segmento controlado. Em notebook que circula
    por redes de terceiros, NÃO aplique.
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $false)]
    [switch]$Apply,

    [Parameter(Mandatory = $false)]
    [switch]$Rollback,

    [Parameter(Mandatory = $false)]
    [switch]$Status,

    # Reconhecimento explícito de que o efeito é global (todas as conexões SMB
    # desta máquina). Exigido em -Apply para que a decisão seja deliberada e
    # fique registrada na linha de comando, no histórico e nos logs do RMM.
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
    Write-Error "Este script requer elevação (Execute como Administrador)."
    exit 1
}

# ── Status ────────────────────────────────────────────────────────────────────
if ($Status) {
    Write-Host "=== Estado atual do SMB Signing ===" -ForegroundColor Cyan
    $config = Get-SmbClientConfiguration
    Write-Host "RequireSecuritySignature : $($config.RequireSecuritySignature)"
    Write-Host "EnableSecuritySignature  : $($config.EnableSecuritySignature)"
    Write-Host ""
    Write-Host "Backup disponível: $(if (Test-Path $BackupFile) { 'SIM' } else { 'NÃO' })"
    if (Test-Path $BackupFile) {
        Write-Host "Conteúdo do backup:"
        Get-Content $BackupFile | ForEach-Object { Write-Host "  $_" }
    }
    exit 0
}

# ── Helper: Capturar estado atual ─────────────────────────────────────────────
function Save-State {
    if (-not (Test-Path $BackupDir)) {
        New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null
    }
    $state = Get-SmbClientConfiguration | Select-Object RequireSecuritySignature, EnableSecuritySignature
    $state | ConvertTo-Json | Out-File -FilePath $BackupFile -Encoding UTF8
    Write-Host "Estado salvo em: $BackupFile" -ForegroundColor Green
}

# ── Helper: Restaurar estado ──────────────────────────────────────────────────
function Restore-State {
    if (-not (Test-Path $BackupFile)) {
        Write-Error "Nenhum backup encontrado em $BackupFile"
        exit 1
    }
    $state = Get-Content $BackupFile | ConvertFrom-Json
    Write-Host "Restaurando estado: $($state | ConvertTo-Json)" -ForegroundColor Yellow
    Set-SmbClientConfiguration -RequireSecuritySignature $state.RequireSecuritySignature
    Set-SmbClientConfiguration -EnableSecuritySignature $state.EnableSecuritySignature
    Write-Host "Estado restaurado com sucesso." -ForegroundColor Green
}

# ── Apply ─────────────────────────────────────────────────────────────────────
if ($Apply) {
    if (-not $AcceptGlobalSecurityImpact) {
        Write-Error @'
Recusado: este ajuste é GLOBAL, não por sub-rede.

Set-SmbClientConfiguration vale para a máquina inteira. Ao aplicar, a exigência
de assinatura SMB cai para TODAS as conexões desta máquina, inclusive em redes
não confiáveis (Wi-Fi público, VPN, DMZ).

Se isso é aceitável para esta máquina, repita com -AcceptGlobalSecurityImpact:
  .\Set-SmbSigningOptimized.ps1 -Apply -AcceptGlobalSecurityImpact
'@
        exit 1
    }

    Write-Host "=== Removendo obrigatoriedade de assinatura SMB (efeito GLOBAL) ===" -ForegroundColor Cyan
    Write-Host "ATENÇÃO: vale para todas as conexões SMB desta máquina." -ForegroundColor Yellow
    Write-Host "Justificativa: Windows 11 24H2 exige signing por padrão, o que em LAN confiável"
    Write-Host "  causa overhead significativo (~65% de perda de throughput)."
    Write-Host ""

    # Backup antes de alterar
    Save-State

    # RequireSecuritySignature $false → remove a obrigatoriedade (efeito global)
    # EnableSecuritySignature $true   → mantém negociável (servidor ainda pode exigir)
    if ($PSCmdlet.ShouldProcess("SMB Client Configuration (máquina inteira)",
                                "Remover obrigatoriedade de assinatura SMB")) {
        Set-SmbClientConfiguration -RequireSecuritySignature $false
        Set-SmbClientConfiguration -EnableSecuritySignature $true
        Write-Host "Configuração aplicada:" -ForegroundColor Green
        Write-Host "  RequireSecuritySignature = false (não obrigatório) — TODAS as conexões"
        Write-Host "  EnableSecuritySignature  = true  (negociável)"
    }

    Write-Host ""
    Write-Host "=== Validação recomendada ===" -ForegroundColor Yellow
    Write-Host "Execute: smbdoctor-cli.exe scan --json"
    Write-Host "Compare com backup anterior (salve antes/depois para diff)."
    exit 0
}

# ── Rollback ──────────────────────────────────────────────────────────────────
if ($Rollback) {
    Write-Host "=== Rollback de SMB Signing ===" -ForegroundColor Cyan
    Restore-State
    exit 0
}

# ── Sem parâmetro válido ──────────────────────────────────────────────────────
Write-Host "Uso: Set-SmbSigningOptimized.ps1 [-Apply -AcceptGlobalSecurityImpact | -Rollback | -Status]" -ForegroundColor Yellow
Write-Host "  -Apply                        Remove a obrigatoriedade de assinatura (backup antes)"
Write-Host "  -AcceptGlobalSecurityImpact   Obrigatório com -Apply: confirma ciência de que o"
Write-Host "                                efeito é GLOBAL (todas as conexões SMB da máquina)"
Write-Host "  -Rollback                     Restaura estado anterior a partir do backup"
Write-Host "  -Status                       Exibe configuração atual e estado do backup"
exit 0
