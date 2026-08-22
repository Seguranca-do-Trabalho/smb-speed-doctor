# SMB Speed Doctor — Versão de Teste

Ferramenta **gratuita, ilimitada e sem custos** para diagnóstico de gargalos
de rede SMB em Windows 10/11. Coleta métricas de todas as camadas (rede, SMB,
disco, CPU, carga) e responde em uma frase qual é o gargalo dominante.

Criado por André Santo (forg3) | junkyardgoodies.app
Licença: MIT (arquivo `LICENSE`).

---

## Como compilar (Linux, cross-build para Windows)

```bash
./build.sh            # build + testes unitários
./build.sh publish    # binários win-x64 em dist/
```

Requisito: .NET 8 SDK instalado em `~/.dotnet`.

## Como testar no Windows

1. Copie a pasta `dist/` para uma máquina Windows 10/11.
2. **GUI:** execute `SmbSpeedDoctor.Gui.exe` → janela com o botão MEDIR AGORA.
3. **CLI (RMM/terminal):**

| Comando | Saída | Exit code |
|---|---|---|
| `SmbSpeedDoctor.Cli.exe scan --json` | JSON completo | 0 = ok, 2 = gargalo |
| `SmbSpeedDoctor.Cli.exe scan` | texto legível | idem |
| `SmbSpeedDoctor.Cli.exe scan --path \\server\share` | foca no share | idem |

Exemplo de integração RMM:

```powershell
$out = & 'C:\tools\SmbSpeedDoctor.Cli.exe' scan --json | ConvertFrom-Json
$out.summary          # frase única do gargalo dominante
$LASTEXITCODE         # 0 ok / 1 warning / 2 crítico
```

4. **Wrapper pronto:** `scripts/smbdoctor-rmm.ps1` (MIT, copiar e colar no RMM).

## O que esperar da versão de teste

- Motor de correlação completo: assinatura/criptografia SMB (cenário 24H2),
  saturação de disco origem/destino, perda de pacote, workload de arquivos
  pequenos, CPU e filtro de antivírus.
- Coletores Windows reais via WMI + Ping; primeira execução pode pedir
  elevação de administrador.
- Degradação graciosa: se uma coleta falhar, o scan continua com valor
  neutro e registra a falha em `collectionErrors` no JSON.
- Toda remediação sugerida carrega rollback explícito (decisão do produto:
  mexer em postura de segurança exige caminho de volta).

## Exit codes (contrato RMM)

| Código | Significado |
|---|---|
| 0 | Sem gargalo dominante |
| 1 | Aviso / erro de execução |
| 2 | Gargalo crítico encontrado |

## Estrutura

```
src/SmbSpeedDoctor.Core          motor puro (testável no Linux)
src/SmbSpeedDoctor.Core/Windows  coletores WMI/Ping
src/SmbSpeedDoctor.Cli           console (--json, exit codes)
src/SmbSpeedDoctor.Gui           WinForms (um botão, uma lista)
tests/SmbSpeedDoctor.Tests       xUnit (TDD)
scripts/smbdoctor-rmm.ps1        wrapper MIT p/ RMM
docs/ADR-0001-stack.md           decisão de arquitetura
build.sh                         build + testes + publish
```

---
**Versão:** 1.0.0-test · **Data:** 2026-08-22 · **Autor:** André Santo (forg3)
**Site:** junkyardgoodies.app
