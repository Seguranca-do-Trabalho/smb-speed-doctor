# SMB Speed Doctor

Ferramenta **gratuita, ilimitada e sem custos** (MIT) de diagnóstico de gargalos
de rede SMB em Windows 10/11 e servidores Samba. Coleta métricas de todas as
camadas (rede, SMB, disco origem/destino, CPU, antivírus, workload), correlaciona
e responde em uma frase qual é o gargalo dominante — com sugestão de remediação
que sempre carrega caminho de rollback.

**Release atual:** 1.1.0 — auditoria final **APROVADO COM RESSALVAS**
(34/34 testes, build sem erros, smokes reais OK, zero segredos no histórico).
Parecer completo em [`docs/AUDIT-1.1.0.md`](docs/AUDIT-1.1.0.md).

Criado por André Santo (forg3) | junkyardgoodies.app
Repositório canônico: https://github.com/Seguranca-do-Trabalho/smb-speed-doctor

---

## Descrição

O diagnóstico cruza cinco camadas e aponta o gargalo dominante em uma frase,
com nível de confiança:

- **Rede:** velocidade negociada do enlace, utilização real (só conta com
  tráfego observado), perda de pacotes, adaptadores virtuais filtrados
  (Hyper-V/VPN/Tailscale) para não gerar falso positivo.
- **SMB:** dialeto negociado, assinatura/criptografia obrigatória (incluindo o
  cenário Windows 11 24H2), multichannel ativo.
- **Disco:** saturação na origem e no destino da cópia.
- **Carga:** CPU e filtro de antivírus.
- **Workload:** muitos arquivos pequenos × poucos arquivos grandes muda a
  recomendação (robocopy profiled).

A cópia de teste real (`RealCopyProbe`) escreve/lê um arquivo-probe no share
alvo e mede MB/s verdadeiro — não estimativa. Se a cópia falhar, cai para
aproximação por NIC e registra o motivo em `collectionErrors`.

## Como utilizar

### Compilar

Requisito: .NET 8 SDK (neste host, instalado em `~/.dotnet`).

```bash
./build.sh            # restore + build Debug + testes unitários
./build.sh publish    # binários win-x64 em dist/ (CLI + GUI)
```

### CLI no Windows (terminal/RMM)

Copie `dist/` para a máquina Windows. Binário: `SmbSpeedDoctor.Cli.exe`.

```text
scan [--json] [--quiet] [--path <share>] [--no-copy]
     [--save <arquivo.json>] [--compare <arquivo.json>]
fix  --export <script.ps1>
```

| Comando | O que faz |
|---|---|
| `SmbSpeedDoctor.Cli.exe scan` | Diagnóstico em texto legível |
| `SmbSpeedDoctor.Cli.exe scan --json` | JSON completo (integração RMM) |
| `SmbSpeedDoctor.Cli.exe scan --path \\servidor\share` | Foca em um share UNC específico |
| `SmbSpeedDoctor.Cli.exe scan --no-copy` | Scan rápido, sem cópia de teste real |
| `SmbSpeedDoctor.Cli.exe scan --save antes.json` | Salva baseline para comparação futura |
| `SmbSpeedDoctor.Cli.exe scan --compare antes.json` | Compara com baseline: MELHOROU/PIOROU/ESTÁVEL por métrica |
| `SmbSpeedDoctor.Cli.exe fix --export correcao.ps1` | Gera script PowerShell de correção (**NÃO executa nada**) |

Contrato de exit codes para RMM: `0` = ok · `1` = warning/erro · `2` = gargalo
crítico. Flag desconhecida → exit 1 com texto de uso.

Integração RMM pronta: copie/cole `scripts/smbdoctor-rmm.ps1`.

Exemplo:

```powershell
$out = & 'C:\tools\SmbSpeedDoctor.Cli.exe' scan --json | ConvertFrom-Json
$out.summary      # frase única do gargalo dominante
$out.copyMethod   # comando robocopy profiled + rationale + estimativa
$LASTEXITCODE     # 0 ok / 1 warning / 2 crítico
```

### GUI

Execute `SmbSpeedDoctor.Gui.exe`: janela única com botão MEDIR AGORA e lista
de achados.

### Scripts de remediação (sempre com Apply/Rollback/Status)

Em `scripts/remediation/`, todos fazem backup do estado anterior e revertem:

| Script | Função |
|---|---|
| `Optimize-NicTuning.ps1` | RSS, TCP autotuning, power-saving da NIC |
| `Enable-JumboFrames.ps1` | Testa MTU fim-a-fim (DF set) antes de aplicar |
| `Enable-SmbMultichannel.ps1` | Ativa multichannel quando há viabilidade (NICs + RSS) |
| `Set-SmbSigningOptimized.ps1` | Tuning de SMB Signing p/ Win11 24H2 (escopado) |
| `Optimize-SambaServer.ps1` | Perfil servidor Samba (TCP_NODELAY, buffers 128K, AIO 16K) |

Uso padrão: `-Apply` aplica (com backup), `-Rollback` reverte, `-Status`
consulta. Leia a saída antes de aplicar — alguns passos exigem elevação.
O script gerado por `fix --export` também nunca executa nada sozinho:
rode primeiro com `-WhatIf`.

### Baseline antes/depois (fluxo recomendado)

```powershell
SmbSpeedDoctor.Cli.exe scan --save antes.json        # antes da remediação
.\scripts\remediation\Optimize-NicTuning.ps1 -Apply  # aplica correção
SmbSpeedDoctor.Cli.exe scan --compare antes.json     # prova do ganho
```

## O que ficou faltando (ressalvas da auditoria 1.1.0)

Nada disso bloqueia o uso interno; está registrado no
[parecer](docs/AUDIT-1.1.0.md):

1. **GUI não validada em Windows real** — o cross-build win-x64 completa e a
   pasta contém todos os DLLs nativos, mas ninguém abriu a GUI num Windows
   físico ainda.
2. **Binários não assinados** — primeira execução pode levantar SmartScreen;
   distribuição ampla exige code signing.
3. **Rotação da credencial `hermes-smb`** — senha de benchmark usada em sessão
   de teste (nunca commitada; verificado no histórico git). Recomenda-se
   trocá-la no Samba.
4. **Coletores WMI só-Windows** — os warnings CA1416 no build Linux são
   esperados (o Core é multiplataforma por design; coletores rodam só no
   Windows com degradação graciosa). Não é defeito.

## Próximos passos

Para o time, em ordem de prioridade:

1. **Validar a GUI no Windows real** (máquina offic3): abrir
   `dist/SmbSpeedDoctor.Gui.exe`, rodar MEDIR AGORA contra um share real e
   registrar resultado em issue.
2. **Rodar o fluxo baseline→remediação→compare** em um cenário com gargalo
   conhecido e validar que o veredito MELHOROU/PIOROU corresponde ao esperado.
3. **Rotacionar credencial `hermes-smb`** no Samba do host de testes.
4. **Avaliar code signing** dos binários win-x64 antes de qualquer distribuição
   externa (hoje o uso é interno à equipe).
5. **Ampliar cobertura de teste do DiagnosisEngine** para os perfis novos do
   robocopy profiled (`/ZB` por link instável ainda tem poucos casos).

## Estrutura

```text
src/SmbSpeedDoctor.Core          motor puro (testável no Linux)
src/SmbSpeedDoctor.Core/Windows  coletores WMI/Ping + RealCopyProbe
src/SmbSpeedDoctor.Cli           console (--json, baselines, fix --export)
src/SmbSpeedDoctor.Gui           WinForms (um botão, uma lista)
tests/SmbSpeedDoctor.Tests       xUnit — 34 testes
scripts/remediation/             5 kits Apply/Rollback/Status
scripts/smbdoctor-rmm.ps1        wrapper MIT pronto p/ RMM
docs/AUDIT-1.1.0.md              parecer da auditoria final do release
docs/ADR-0001-stack.md           decisão de arquitetura
CHANGELOG.md                     histórico 1.0.0 → 1.1.0
build.sh                         build + testes + publish
```

---

**Versão:** 1.1.0 · **Data:** 2026-08-22 · **Autor:** André Santo (forg3)
**Licença:** MIT · **Site:** junkyardgoodies.app
