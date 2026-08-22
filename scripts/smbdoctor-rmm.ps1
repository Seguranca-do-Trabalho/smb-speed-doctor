@rem SMB Speed Doctor — wrapper para RMM (Action1, NinjaOne, Datto RMM, etc.)
@rem Licença: MIT
@rem Criado por André Santo (forg3) | junkyardgoodies.app
@rem Revisão: %DATE%
@rem
@rem Fluxo típico:
@rem   1) Implante o binário smbdoctor-cli.exe via deployment de software do RMM.
@rem   2) Execute este script (PowerShell) em cada endpoint gerenciado.
@rem   3) Leia a saída JSON no log do RMM; exit code indica gargalo (0=ok, 2=critical).
@rem
@rem Nota: este wrapper é código público/MIT. O produto é o binário assinado.

$ErrorActionPreference = 'Stop'

$exe = Join-Path ${env:ProgramFiles(x86)} 'SMB Speed Doctor\smbdoctor-cli.exe'
if (-not (Test-Path $exe)) {
    Write-Host '{"error":"binario nao instalado"}' -ForegroundColor Red
    exit 1
}

$out = & $exe scan --json --quiet 2>&1
$json = $out | ConvertFrom-Json
Write-Host ($json | ConvertTo-Json -Depth 10)

exit $json.exitCode
