# Criado por André Santo (forg3) | junkyardgoodies.app
# Licença: MIT
#
# Item 8 — Perfil de desempenho Samba (lado servidor Linux)
#
# O SMB Speed Doctor diagnostica o cliente; este script prepara o SERVIDOR.
# Gera um bloco [smb-speed-doctor] pronto para /etc/samba/smb.conf com:
#   - TCP_NODELAY + IPTOS_THROUGHPUT: menor latência em cópias
#   - AIO habilitado: I/O assíncrono para cargas grandes
#   - server multi channel support: agrega NICs no servidor (Samba 4.4+)
#   - deadtime curto: libera conexões zumbis
#
# USO:
#   sudo ./Optimize-SambaServer.ps1 -Status            # mostra config atual
#   sudo ./Optimize-SambaServer.ps1 -Apply             # backup + aplica + reload
#   sudo ./Optimize-SambaServer.ps1 -Rollback          # restaura backup
#   sudo ./Optimize-SambaServer.ps1 -Apply -NicCount 2 # declara 2 NICs p/ multichannel
#
# EXECUTA NO LINUX (bash via pwsh) — requer root e samba instalado.

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Apply,
    [switch]$Rollback,
    [switch]$Status,
    [int]$NicCount = 0   # 0 = auto-detecta interfaces físicas up
)

$ErrorActionPreference = 'Stop'

if ($IsWindows) { throw "Este script roda no servidor LINUX (Samba). No Windows Server, use os cmdlets nativos Set-SmbServerConfiguration." }

$SmbConf = '/etc/samba/smb.conf'
$BackupFile = '/var/lib/smb-speed-doctor/smb.conf.pre-doctor'

function Assert-Root {
    if ((id -u) -ne 0) { throw "Execute como root (sudo)." }
}

function Get-PhysicalNics {
    (ip -brief link show | Select-String 'UP' | ForEach-Object {
        ($_.Line -split '@')[0].Trim() -split '\s+' | Select-Object -First 1
    }) | Where-Object { $_ -notmatch 'lo|tailscale|docker|veth|br-|tun|tap' }
}

if ($Status -or (-not $Apply -and -not $Rollback)) {
    Write-Host "=== SAMBA SERVER — STATUS ===" -ForegroundColor Cyan
    testparm -s 2>/dev/null | Select-String 'server multi channel|socket options|aio|deadtime|max protocol|server min protocol|read raw|write raw'
    Write-Host "`nInterfaces físicas UP: $((Get-PhysicalNics) -join ', ')"
    return
}

Assert-Root

if (-not (Test-Path $SmbConf)) { throw "$SmbConf não encontrado — Samba instalado?" }

if ($Apply) {
    if (-not (Test-Path '/var/lib/smb-speed-doctor')) { New-Item -ItemType Directory -Path '/var/lib/smb-speed-doctor' | Out-Null }
    Copy-Item $SmbConf $BackupFile -Force
    Write-Host "Backup: $BackupFile"

    # Multichannel: precisa de >= 2 NICs reais para fazer sentido
    $nics = if ($NicCount -gt 0) { $NicCount } else { @(Get-PhysicalNics).Count }
    $multichannel = if ($nics -ge 2) { 'yes' } else { 'no' }

    $block = @"

# --- Bloco gerado pelo SMB Speed Doctor ($(Get-Date -Format o)) ---
# Rollback: sudo cp '$BackupFile' '$SmbConf' && systemctl restart smbd
[global]
    # Tuning de cópias grandes (Item 8 do roadmap SMB Speed Doctor)
    socket options = TCP_NODELAY IPTOS_THROUGHPUT SO_RCVBUF=131072 SO_SNDBUF=131072
    aio read size = 16384
    aio write size = 16384
    server multi channel support = $multichannel
    deadtime = 10
    getwd cache = yes
    # Fim do bloco SMB Speed Doctor
"@

    if ($PSCmdlet.ShouldProcess($SmbConf, "Adicionar bloco de tuning")) {
        # Idempotente: remove blocos anteriores antes de inserir
        $content = Get-Content $SmbConf -Raw
        $content = $content -replace "(?ms)\r?\n# --- Bloco gerado pelo SMB Speed Doctor.*?# Fim do bloco SMB Speed Doctor\r?\n", "`n"
        Add-Content -Path $SmbConf -Value $block -Encoding UTF8

        # Validação ANTES do reload: conf quebrado não pode derrubar o serviço
        $testOut = testparm -s 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "ERRO: smb.conf inválido após edição! Revertendo..." -ForegroundColor Red
            Copy-Item $BackupFile $SmbConf -Force
            throw $testOut
        }

        systemctl reload smbd
        Write-Host "Aplicado. multichannel=$multichannel ($nics NIC(s)). smbd recarregado." -ForegroundColor Green
        if ($nics -lt 2) {
            Write-Host "AVISO: multichannel desativado — só há $nics NIC física(s)." -ForegroundColor Yellow
        }
        Write-Host "`nValide do cliente:"
        Write-Host "  smbdoctor-cli scan --save antes.json --path \\$(hostname)\projetos"
    }
    return
}

if ($Rollback) {
    if (-not (Test-Path $BackupFile)) { throw "Sem backup em $BackupFile." }
    Copy-Item $BackupFile $SmbConf -Force
    testparm -s 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Backup restaurado mas smb.conf inválido — revise manualmente!" }
    systemctl reload smbd
    Remove-Item $BackupFile -Force
    Write-Host "Revertido ao estado anterior. smbd recarregado." -ForegroundColor Green
}
