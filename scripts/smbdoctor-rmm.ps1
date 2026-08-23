# SMB Speed Doctor — wrapper para RMM (Action1, NinjaOne, Datto RMM, etc.)
# Licença: MIT
# Criado por André Santo (forg3) | junkyardgoodies.app
#
# Fluxo típico:
#   1) Implante o binário smbdoctor-cli.exe via deployment de software do RMM.
#   2) Execute este script (PowerShell) em cada endpoint gerenciado.
#   3) Leia a saída JSON no log do RMM; exit code indica gargalo (0=ok, 2=critical).
#
# Nota: este wrapper é código público/MIT. O produto é o binário assinado.
#
# Correção: as linhas de comentário deste arquivo usavam '@rem', que é sintaxe
# de arquivo .bat. Em PowerShell isso é erro de parse — o wrapper falhava na
# primeira linha, antes de qualquer lógica, e a integração RMM nunca rodou.

[CmdletBinding()]
param(
    # Compartilhamento a medir. Sem ele NÃO há cópia de teste e, portanto, não
    # há medição de throughput: o diagnóstico não conclui sobre assinatura,
    # multichannel, disco ou CPU. Para uso em RMM, aponte para um share real.
    [Parameter(Mandatory = $false)]
    [string]$Path
)

$ErrorActionPreference = 'Stop'

# O binário é publicado win-x64. Procura nos dois Program Files e no PATH, em
# vez de fixar um só caminho — instaladores divergem e o custo de errar aqui é
# o wrapper inteiro não rodar.
$candidates = @(
    (Join-Path ${env:ProgramFiles} 'SMB Speed Doctor\smbdoctor-cli.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'SMB Speed Doctor\smbdoctor-cli.exe')
) | Where-Object { $_ -and (Test-Path $_) }

$exe = $candidates | Select-Object -First 1
if (-not $exe) {
    $onPath = Get-Command 'smbdoctor-cli.exe' -ErrorAction SilentlyContinue
    if ($onPath) { $exe = $onPath.Source }
}

if (-not $exe) {
    Write-Host '{"error":"binario nao instalado","searched":["%ProgramFiles%","%ProgramFiles(x86)%","PATH"]}'
    exit 1
}

$smbArgs = @('scan', '--json', '--quiet')
if ($Path) { $smbArgs += @('--path', $Path) }

$out = & $exe @smbArgs 2>&1
$exitFromExe = $LASTEXITCODE

try {
    $json = $out | ConvertFrom-Json
}
catch {
    # Saída não-JSON significa falha antes do diagnóstico. Propaga como erro em
    # vez de deixar o ConvertFrom-Json estourar sem contexto no log do RMM.
    $raw = ($out | Out-String).Trim()
    Write-Host (@{ error = 'saida nao-JSON do binario'; raw = $raw } | ConvertTo-Json -Compress)
    exit 1
}

Write-Host ($json | ConvertTo-Json -Depth 10)

# Prefere o exit code do próprio binário; cai para o do JSON se indisponível.
if ($null -ne $exitFromExe) { exit $exitFromExe }
exit $json.exitCode
