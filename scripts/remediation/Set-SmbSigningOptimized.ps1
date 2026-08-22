<#
.SYNOPSIS
    SMB Signing Tuner — configuração escopada para Windows 11 24H2.

.DESCRIPTION
    Windows 11 24H2 tornou RequireSecuritySignature obrigatório por padrão,
    causando queda típica de ~110 MB/s para ~38 MB/s em redes internas
    confiáveis. Este script aplica tuning escopado:
      - Remove obrigatoriedade global (RequireSecuritySignature $false)
      - Mantém signing negociável para conexões fora da sub-rede (EnableSecuritySignature $true)
      - Documenta justificativa e backup de estado para rollback

    GANHO ESPERADO: retorno à velocidade normal (~110 MB/s) em segmento confiável.

    COMO VALIDAR:
      1) Antes: smbdoctor-cli.exe scan --json > before.json
      2) Execute: .\Set-SmbSigningOptimized.ps1 -Apply -Subnet 10.0.0.0/8
      3) Depois: smbdoctor-cli.exe scan --json > after.json
      4) Compare SigninEnabled e throughput entre before/after.

.AUTHOR
    Criado por André Santo (forg3) | junkyardgoodies.app

.LICENSE
    MIT License — veja LICENSE no repositório.

.RISK
    Signing protege contra tampering/MITM em SMB. Só desative em segmento
    confiável (LAN interna, VLAN gerenciada). Em rede compartilhada ou DMZ,
    mantenha signing obrigatório.
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $false)]
    [switch]$Apply,

    [Parameter(Mandatory = $false)]
    [switch]$Rollback,

    [Parameter(Mandatory = $false)]
    [switch]$Status,

    [Parameter(Mandatory = $false)]
    [string]$Subnet = '10.0.0.0/8'
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
    Write-Host "=== Aplicando tuning escopado de SMB Signing ===" -ForegroundColor Cyan
    Write-Host "Sub-rede confiável: $Subnet"
    Write-Host "Justificativa: Windows 11 24H2 exige signing global, mas em LAN confiável"
    Write-Host "  o signing causa overhead significativo (~65% de perda de throughput)."
    Write-Host ""

    # Backup antes de alterar
    Save-State

    # Configuração escopada
    # RequireSecuritySignature $false → remove obrigatoriedade global
    # EnableSecuritySignature $true   → mantém negociável (não obrigatório)
    if ($PSCmdlet.ShouldProcess("SMB Client Configuration", "Aplicar tuning escopado")) {
        Set-SmbClientConfiguration -RequireSecuritySignature $false
        Set-SmbClientConfiguration -EnableSecuritySignature $true
        Write-Host "Configuração aplicada:" -ForegroundColor Green
        Write-Host "  RequireSecuritySignature = $false (não obrigatório)"
        Write-Host "  EnableSecuritySignature  = $true (negociável)"
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
Write-Host "Uso: Set-SmbSigningOptimized.ps1 [-Apply | -Rollback | -Status] [-Subnet <rede>]" -ForegroundColor Yellow
Write-Host "  -Apply     Aplica tuning escopado (backup antes de alterar)"
Write-Host "  -Rollback  Restaura estado anterior a partir do backup"
Write-Host "  -Status    Exibe configuração atual e estado do backup"
Write-Host "  -Subnet    Sub-rede confiável (default: 10.0.0.0/8)"
exit 0
