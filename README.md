# SMB Speed Doctor

Ferramenta **gratuita, ilimitada e sem custos** (MIT) de diagnóstico de gargalos
de rede SMB. Coleta métricas de todas as camadas (rede, SMB, disco
origem/destino, CPU, antivírus, workload), correlaciona e responde em uma frase
qual é o gargalo dominante — com sugestão de remediação que sempre carrega
caminho de rollback.

**Escopo real:** o **diagnóstico roda no cliente Windows 10/11**. Para servidores
Samba há um **script de remediação** (`Optimize-SambaServer.ps1`), mas **não há
coletor Linux** — nada é medido do lado do servidor. Versões anteriores deste
README diziam "diagnóstico em Windows e servidores Samba", o que prometia mais
do que o produto entrega. Coleta em Linux está no roadmap, não no release.

**Release atual:** 1.2.0 — correção de um falso positivo crítico que recomendava
downgrade de segurança sem medição (ver
[`docs/ADR-0003-qualidade-de-medicao.md`](docs/ADR-0003-qualidade-de-medicao.md)),
separação de plataforma
([`docs/ADR-0002`](docs/ADR-0002-separacao-plataforma.md)) e wrapper RMM que
passou a de fato executar. **44/44 testes, build com 0 avisos.**
Histórico completo em [`CHANGELOG.md`](CHANGELOG.md).

> ⚠️ **Meça com `--path`.** Sem um compartilhamento alvo não há cópia de teste e,
> portanto, **não há medição de throughput** — o diagnóstico não conclui sobre
> assinatura, multichannel, disco ou CPU, e diz isso explicitamente no relatório.

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

### Procedência da medição

Cada scan declara **de onde veio** o número de throughput, e o motor só conclui
o que a evidência sustenta:

| Procedência | Quando | O que o motor conclui |
|---|---|---|
| `Measured` | cópia de teste real executada | tudo |
| `Approximated` | sem cópia, mas com tráfego de rede acima de 1 MB/s | tudo, com confiança menor |
| `Unavailable` | sem cópia e rede ociosa | **nada** que dependa de throughput |

Com `Unavailable`, sinais diretos continuam valendo (perda de pacote, enlace
negociado, dialeto, criptografia, antivírus) — o scan rápido segue útil. O que
não acontece mais é o programa concluir "assinatura SMB é o gargalo" a partir de
uma rede parada. Detalhes e o caso real em
[`docs/ADR-0003`](docs/ADR-0003-qualidade-de-medicao.md).

## Como utilizar

### Compilar

Requisito: .NET 8 SDK (neste host, instalado em `~/.dotnet`).

```bash
./build.sh                        # restore + build Debug + testes unitários
./build.sh publish                # win-x64 em dist/ (CLI) e dist/Gui/ (GUI)
./build.sh publish-selfcontained  # win-x64 sem dependência de .NET instalado
```

O `publish` normal é **framework-dependent**: a máquina alvo precisa do **.NET 8
Desktop Runtime (x64)**. Para endpoints que não têm .NET, use
`publish-selfcontained` (~150 MB por app, sem pré-requisito).

> ⚠️ **Nunca publique por cima de uma pasta `dist/` antiga.** O `dotnet publish`
> não remove arquivos que sobraram. Um publish self-contained anterior deixa
> `hostfxr.dll`/`hostpolicy.dll`/`coreclr.dll` na pasta; ao publicar
> framework-dependent por cima, o `.exe` usa esse host local antigo em vez do
> host do sistema e falha com *"You must install or update .NET to run this
> application"* — **numa máquina que tem o .NET instalado**. Já aconteceu.
> O `build.sh` limpa `dist/` automaticamente; se publicar à mão, limpe antes.

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

Integração RMM pronta: copie/cole `scripts/smbdoctor-rmm.ps1`. Ele localiza o
binário nos dois `Program Files` e no `PATH`, e aceita `-Path` para medir:

```powershell
.\smbdoctor-rmm.ps1 -Path '\\servidor\share'
```

Exemplo direto pelo binário:

```powershell
$out = & 'C:\tools\SmbSpeedDoctor.Cli.exe' scan --json --path '\\servidor\share' | ConvertFrom-Json
$out.summary      # frase única do gargalo dominante
$out.copyMethod   # comando robocopy profiled + rationale + estimativa
$LASTEXITCODE     # 0 ok / 1 warning / 2 crítico
```

Sem `--path` o scan é parcial: ele reporta os sinais diretos e declara, em
`findings`, que o throughput não foi medido.

### GUI

Execute `dist/Gui/SmbSpeedDoctor.Gui.exe`: janela única com campo de
compartilhamento, botão **MEDIR AGORA** e lista de achados.

**Informe o compartilhamento** no campo (ou use *Procurar…*). Sem caminho, a GUI
faz scan parcial e diz explicitamente que o throughput não foi medido — ela não
inventa um veredito.

### Scripts de remediação (sempre com Apply/Rollback/Status)

Em `scripts/remediation/`, todos fazem backup do estado anterior e revertem:

| Script | Função |
|---|---|
| `Optimize-NicTuning.ps1` | RSS, TCP autotuning, power-saving da NIC |
| `Enable-JumboFrames.ps1` | Testa MTU fim-a-fim (DF set) antes de aplicar |
| `Enable-SmbMultichannel.ps1` | Ativa multichannel quando há viabilidade (NICs + RSS) |
| `Set-SmbSigningOptimized.ps1` | Remove obrigatoriedade de assinatura SMB (Win11 24H2) — **efeito GLOBAL**, exige `-AcceptGlobalSecurityImpact` |
| `Optimize-SambaServer.ps1` | Perfil servidor Samba (TCP_NODELAY, buffers 128K, AIO 16K) |

Uso padrão: `-Apply` aplica (com backup), `-Rollback` reverte, `-Status`
consulta. **Todos exigem PowerShell elevado** (Executar como Administrador);
`-Status` também, por consultar configuração de sistema.

Compatíveis com **Windows PowerShell 5.1** (o padrão do Windows 10/11, também
via `cmd.exe`) e com o PowerShell 7. Até a 1.1.0, 5 dos 6 scripts **não
compilavam no 5.1** — os arquivos eram UTF-8 sem BOM e um deles usava operador
ternário, exclusivo do PS7. Se for editar estes scripts, **mantenha o UTF-8 com
BOM**, ou os acentos quebram o parse no 5.1.
O script gerado por `fix --export` também nunca executa nada sozinho:
rode primeiro com `-WhatIf`.

> ⚠️ **Sobre `Set-SmbSigningOptimized.ps1`.** `Set-SmbClientConfiguration` é uma
> configuração **de máquina**: o Windows não oferece política de assinatura SMB
> por sub-rede no cliente. Ao aplicar, a exigência de assinatura cai para
> **todas** as conexões SMB da máquina — Wi-Fi público, VPN, DMZ inclusive.
>
> Até a versão 1.1.0 o script aceitava um parâmetro `-Subnet` e dizia aplicar
> "tuning escopado". Era falso: o valor só era impresso na tela e nunca limitou
> coisa alguma. O parâmetro foi removido e o `-Apply` agora exige
> `-AcceptGlobalSecurityImpact`, para que a decisão seja deliberada e fique
> registrada. Em máquina que circula por redes de terceiros, **não aplique**.

### Baseline antes/depois (fluxo recomendado)

```powershell
SmbSpeedDoctor.Cli.exe scan --save antes.json        # antes da remediação
.\scripts\remediation\Optimize-NicTuning.ps1 -Apply  # aplica correção
SmbSpeedDoctor.Cli.exe scan --compare antes.json     # prova do ganho
```

## Estado atual e pendências

### Corrigido na 1.2.0

| Era | Virou |
|---|---|
| `scan` sem `--path` acusava gargalo crítico e recomendava desligar assinatura SMB, sem medir nada | Declara que não mediu; não conclui sobre throughput |
| 52 avisos de build (42 CA1416 + 10 de nulabilidade) | **0 avisos** |
| `smbdoctor-rmm.ps1` não compilava (comentários `@rem`, sintaxe .bat em `.ps1`) | Executa; testado ponta a ponta |
| `Set-SmbSigningOptimized.ps1 -Subnet` sugeria escopo que não existia | Parâmetro removido; efeito global explícito e com trava |
| Dialeto desconhecido reportado como "dialeto moderno" | "não determinado" |
| Achado de multichannel somava no score de assinatura | Balde e remediação próprios |
| Senha em texto plano em `docs/AUDIT-1.1.0.md` | Removida (**rotação obrigatória**, ver abaixo) |

### Pendências

1. **Rotacionar a credencial `hermes-smb`** — a senha foi removida dos arquivos,
   mas **permanece em 2 commits do histórico** (`f460636`, `0843945`). Remover
   do working tree não apaga o histórico; a rotação é a mitigação real.
   *Prioridade 1.*
2. **GUI não validada em Windows real** — o build win-x64 completa e a pasta tem
   todos os DLLs nativos, mas ninguém abriu a GUI num Windows físico ainda.
3. **Binários não assinados** — primeira execução pode levantar SmartScreen;
   distribuição ampla exige code signing.
4. **Sem CI** — os 44 testes rodam só localmente. Um workflow no GitHub Actions
   (Windows runner) evitaria que regressões como as acima cheguem ao `master`.
5. **Sem coletor Linux/Samba** — o diagnóstico é do lado cliente Windows. A
   separação `Core` / `Core.Windows` (ADR-0002) abriu o caminho, mas o coletor
   ainda não existe.
6. **Testes exigem Windows** — o projeto de teste é `net8.0-windows` porque cobre
   a coleta WMI. Separar os testes de domínio puro num projeto `net8.0` está
   registrado como follow-up no ADR-0002.

## Próximos passos

Para o time, em ordem de prioridade:

1. **Rotacionar credencial `hermes-smb`** no Samba do host de testes.
2. **Validar a GUI no Windows real** (máquina offic3): abrir
   `dist/SmbSpeedDoctor.Gui.exe`, rodar MEDIR AGORA contra um share real e
   registrar resultado em issue.
3. **Rodar o fluxo baseline→remediação→compare** em um cenário com gargalo
   conhecido e validar que o veredito MELHOROU/PIOROU corresponde ao esperado.
   Use sempre `--path` — sem ele não há medição.
4. **Adicionar CI** (GitHub Actions, runner Windows): `dotnet build` com
   `TreatWarningsAsErrors` e `dotnet test` em cada push.
5. **Avaliar code signing** dos binários win-x64 antes de qualquer distribuição
   externa (hoje o uso é interno à equipe).

## Estrutura

```text
src/SmbSpeedDoctor.Core           domínio PURO, net8.0 (modelo, motor, baseline)
src/SmbSpeedDoctor.Core.Windows   coleta WMI/Ping + RealCopyProbe, net8.0-windows
src/SmbSpeedDoctor.Cli            console (--json, baselines, fix --export)
src/SmbSpeedDoctor.Gui            WinForms (um botão, uma lista)
tests/SmbSpeedDoctor.Tests        xUnit — 44 testes
scripts/remediation/              5 kits Apply/Rollback/Status
scripts/smbdoctor-rmm.ps1         wrapper MIT pronto p/ RMM
docs/ADR-0001-stack.md            decisão de stack
docs/ADR-0002-separacao-plataforma.md   por que Core e Core.Windows são separados
docs/ADR-0003-qualidade-de-medicao.md   por que sem medição não há diagnóstico
docs/AUDIT-1.1.0.md               parecer 1.1.0 (com correções da revisão posterior)
CHANGELOG.md                      histórico 1.0.0 → 1.2.0
build.sh                          build + testes + publish
```

---

**Versão:** 1.2.0 · **Data:** 2026-08-23 · **Autor:** André Santo (forg3)
**Licença:** MIT · **Site:** junkyardgoodies.app
