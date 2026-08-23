<#
.SYNOPSIS
    Verificacao completa do repositorio, rodando LOCALMENTE.

.DESCRIPTION
    Substitui o GitHub Actions, que nao executa nesta organizacao: todo job
    morre em ~2s com "The job was not started because recent account payments
    have failed or your spending limit needs to be increased". O workflow em
    .github/workflows/ci.yml existe mas esta dormente (so workflow_dispatch).

    Roda as MESMAS checagens dos jobs do CI, e cada uma existe por causa de um
    defeito que ja chegou ao repositorio:

      Build       dotnet build -warnaserror  (o repo ja teve 52 avisos; volume
                  assim anestesia e esconde aviso novo)
      Testes      dotnet test
      PS 5.1      parse dos .ps1 no Windows PowerShell 5.1 — cinco dos seis
                  scripts nao compilavam nele enquanto compilavam no PS7
      BOM         .ps1 sem BOM UTF-8 quebra no 5.1 (acentos viram mojibake e
                  corrompem terminadores de string)
      PS7-only    ternario '? :', '??' e '?.' nao existem no 5.1
      Segredos    procura credenciais no historico COMPLETO — uma senha em
                  texto plano ja chegou ao repo dentro do proprio parecer que
                  afirmava nao haver segredos

    Ferramenta ausente vira AVISO, nao erro. O que reprova e checagem que
    rodou e falhou.

.PARAMETER Quick
    Pula o que e lento: rebuild completo e varredura de historico.

.PARAMETER Only
    Roda so um grupo: build | test | scripts | secrets

.EXAMPLE
    ./scripts/verify.ps1
    ./scripts/verify.ps1 -Quick
    ./scripts/verify.ps1 -Only scripts

.AUTHOR
    Criado por Andre Santo (forg3) | junkyardgoodies.app
#>
[CmdletBinding()]
param(
    [switch]$Quick,
    [ValidateSet('build', 'test', 'scripts', 'secrets')]
    [string]$Only
)

$ErrorActionPreference = 'Continue'
$raiz = Split-Path -Parent $PSScriptRoot
Set-Location $raiz

$script:falhas = @()
$script:avisos = @()

function Titulo($t) {
    Write-Host ""
    Write-Host ("=" * 68) -ForegroundColor DarkGray
    Write-Host "  $t" -ForegroundColor Cyan
    Write-Host ("=" * 68) -ForegroundColor DarkGray
}
function Ok($m)     { Write-Host "  [OK]    $m" -ForegroundColor Green }
function Falha($m)  { Write-Host "  [FALHA] $m" -ForegroundColor Red;    $script:falhas += $m }
function Aviso($m)  { Write-Host "  [AVISO] $m" -ForegroundColor Yellow; $script:avisos += $m }

function Rodar($grupo) { return (-not $Only) -or ($Only -eq $grupo) }

# --------------------------------------------------------------- Build -----
if (Rodar 'build') {
    Titulo "BUILD (avisos = erro)"
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Aviso "dotnet nao encontrado no PATH; pulando build e testes"
    }
    else {
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        $args = @('build', 'SmbSpeedDoctor.sln', '-c', 'Release', '--nologo',
                  '-v', 'minimal', '-warnaserror')
        if (-not $Quick) { $args += '--no-incremental' }
        $saida = & dotnet @args 2>&1
        $erros = @($saida | Select-String ' error ')
        if ($LASTEXITCODE -eq 0 -and $erros.Count -eq 0) {
            Ok "build limpo, zero avisos"
        }
        else {
            Falha "build reprovou ($($erros.Count) erro(s))"
            $erros | Select-Object -First 8 | ForEach-Object { Write-Host "          $_" -ForegroundColor DarkRed }
        }
    }
}

# -------------------------------------------------------------- Testes -----
if (Rodar 'test') {
    Titulo "TESTES"
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Aviso "dotnet ausente; testes pulados"
    }
    else {
        $saida = & dotnet test SmbSpeedDoctor.sln -c Release --nologo -v quiet 2>&1
        $linha = $saida | Select-String 'Aprovado!|Com falha!' | Select-Object -First 1
        if ($LASTEXITCODE -eq 0) { Ok ($linha -replace '\s+', ' ').Trim() }
        else {
            Falha "testes reprovaram"
            $saida | Select-String '\[FAIL\]' | Select-Object -First 10 |
                ForEach-Object { Write-Host "          $_" -ForegroundColor DarkRed }
        }
    }
}

# ------------------------------------------------------------- Scripts -----
if (Rodar 'scripts') {
    Titulo "SCRIPTS POWERSHELL"

    $ps1 = @(Get-ChildItem scripts -Recurse -Filter *.ps1 -ErrorAction SilentlyContinue)
    if ($ps1.Count -eq 0) { Aviso "nenhum .ps1 encontrado em scripts/" }

    # -- BOM UTF-8 --------------------------------------------------------
    $semBom = @()
    foreach ($f in $ps1) {
        $b = [System.IO.File]::ReadAllBytes($f.FullName)
        if ($b.Length -lt 3 -or $b[0] -ne 0xEF -or $b[1] -ne 0xBB -or $b[2] -ne 0xBF) {
            $semBom += $f.Name
        }
    }
    if ($semBom.Count -eq 0) { Ok "todos os .ps1 com BOM UTF-8" }
    else { Falha "sem BOM UTF-8 (quebram no PowerShell 5.1): $($semBom -join ', ')" }

    # NOTA: nao existe aqui uma busca textual por sintaxe exclusiva do PS7
    # (ternario '? :', '??', '?.'). Ela seria REDUNDANTE — essas construcoes sao
    # erro de sintaxe no 5.1, entao o parse abaixo ja as reprova, e foi assim
    # que o ternario em Optimize-NicTuning.ps1 foi encontrado. Pior: a busca
    # textual casava com a propria DOCUMENTACAO que menciona esses operadores,
    # gerando falso positivo. O parser decide; o regex so atrapalhava.

    # -- parse no Windows PowerShell 5.1 REAL ------------------------------
    # Precisa ser o powershell.exe 5.1: o parser do PS7 aceita os arquivos que
    # o 5.1 rejeita, e foi exatamente assim que o defeito passou batido.
    $ps51 = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path $ps51)) {
        Aviso "powershell.exe 5.1 nao encontrado; parse do 5.1 pulado"
    }
    else {
        $tmp = Join-Path $env:TEMP "verify-parse51-$PID.ps1"
        @'
$falhas = 0
Get-ChildItem (Join-Path $args[0] 'scripts') -Recurse -Filter *.ps1 | ForEach-Object {
  $erros = $null
  [void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$null, [ref]$erros)
  if ($erros -and $erros.Count) { Write-Output "FALHA|$($_.Name)|$($erros[0].Message)"; $falhas++ }
  else { Write-Output "OK|$($_.Name)|" }
}
exit $falhas
'@ | Set-Content -Path $tmp -Encoding UTF8
        $res = & $ps51 -NoProfile -ExecutionPolicy Bypass -File $tmp $raiz 2>&1
        $ruins = @($res | Where-Object { $_ -like 'FALHA|*' })
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        if ($ruins.Count -eq 0) { Ok "$($ps1.Count) script(s) compilam no Windows PowerShell 5.1" }
        else {
            Falha "$($ruins.Count) script(s) NAO compilam no PowerShell 5.1"
            $ruins | ForEach-Object {
                $p = $_ -split '\|'
                Write-Host "          $($p[1]): $($p[2])" -ForegroundColor DarkRed
            }
        }
    }
}

# ------------------------------------------------------------- Segredos ----
if (Rodar 'secrets') {
    Titulo "SEGREDOS"

    $padroes = @(
        @{ nome = 'PAT do GitHub (ghp_)';   re = 'ghp_[A-Za-z0-9]{30,}' },
        @{ nome = 'PAT novo (github_pat_)'; re = 'github_pat_[A-Za-z0-9_]{30,}' },
        @{ nome = 'token OAuth (gho_)';     re = 'gho_[A-Za-z0-9]{30,}' },
        @{ nome = 'chave privada';          re = 'BEGIN [A-Z ]*PRIVATE KEY' },
        @{ nome = 'senha embutida';         re = '(?i)(password|senha|passwd)\s*[:=]\s*["\x27][^"\x27]{3,}' }
    )

    # arvore de trabalho
    $achados = @()
    foreach ($p in $padroes) {
        $r = & git grep -nEI $p.re -- . 2>$null
        if ($r) { $achados += "$($p.nome): $($r | Select-Object -First 3)" }
    }
    if ($achados.Count -eq 0) { Ok "nenhum segredo na arvore de trabalho" }
    else { $achados | ForEach-Object { Falha "arvore: $_" } }

    # historico completo — foi onde a senha se escondeu antes
    if ($Quick) {
        Aviso "-Quick: varredura de historico pulada"
    }
    elseif (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Aviso "git ausente; varredura de historico pulada"
    }
    else {
        $hist = @()
        foreach ($p in $padroes) {
            $r = & git log -p --all -G $p.re --oneline 2>$null | Select-Object -First 1
            if ($r) { $hist += "$($p.nome) em $r" }
        }
        if ($hist.Count -eq 0) { Ok "nenhum segredo no historico completo" }
        else { $hist | ForEach-Object { Falha "historico: $_" } }
    }
}

# -------------------------------------------------------------- Resumo -----
Titulo "RESUMO"
if ($avisos.Count) {
    Write-Host "  avisos ($($avisos.Count)):" -ForegroundColor Yellow
    $avisos | ForEach-Object { Write-Host "    - $_" -ForegroundColor Yellow }
}
if ($falhas.Count -eq 0) {
    Write-Host ""
    Write-Host "  TUDO VERDE" -ForegroundColor Green
    Write-Host ""
    exit 0
}
Write-Host ""
Write-Host "  $($falhas.Count) FALHA(S):" -ForegroundColor Red
$falhas | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
Write-Host ""
exit 1
