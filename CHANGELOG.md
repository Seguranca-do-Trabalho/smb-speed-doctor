# CHANGELOG — SMB Speed Doctor

Formato: Keep a Changelog. Datas em America/Sao_Paulo.
Autor de todas as mudanças: engenharia assistida por Hermes, sob direção de André Santo (forg3) | junkyardgoodies.app.

## [1.2.0] — 2026-08-23

Rodada de correção a partir de auditoria com **execução real do app** (não só
leitura de código). Os quatro defeitos principais abaixo passaram pelos 34
testes da 1.1.0 sem serem detectados — todos usavam mocks e nenhum exercitava o
binário de verdade.

### Corrigido — segurança

- **Falso positivo crítico recomendando downgrade de segurança.** `scan --json`
  sem `--path` não executa cópia de teste; o throughput vinha da NIC ociosa
  (~886 B/s) e o motor concluía `dominant: SmbSigning`, `severity: Critical`,
  exit 2, recomendando `Set-SmbClientConfiguration -RequireSecuritySignature
  $false`. Nenhum share havia sido testado.
  Causa: "não medi" e "medi e deu zero" chegavam ao motor como o mesmo número.
  Correção: `MeasurementQuality` (`Measured`/`Approximated`/`Unavailable`) no
  `ScanData`, com **default seguro** `Unavailable`; toda regra derivada de
  throughput passou a exigir medição válida. Ver
  `docs/ADR-0003-qualidade-de-medicao.md`.
  *Observação:* o commit `562c502` já havia corrigido isto para
  `LinkUtilization`; a correção não tinha sido generalizada para as demais
  regras.
- **`Set-SmbSigningOptimized.ps1 -Subnet` prometia escopo inexistente.** O
  parâmetro só era impresso na tela — `Set-SmbClientConfiguration` é
  configuração de máquina e o Windows não oferece política de assinatura SMB por
  sub-rede no cliente. Na prática a assinatura era desabilitada para **todas** as
  conexões, incluindo redes não confiáveis, enquanto a documentação afirmava o
  contrário. Parâmetro removido; `-Apply` agora exige
  `-AcceptGlobalSecurityImpact`; documentação reescrita.
- **Senha em texto plano no repositório.** `docs/AUDIT-1.1.0.md` citava a senha
  do usuário `hermes-smb` para argumentar que ela não estava versionada — no
  próprio arquivo versionado. Removida do texto; o parecer foi retratado.
  **A rotação da credencial no Samba é obrigatória**: o valor permanece em 2
  commits do histórico (`f460636`, `0843945`).

### Corrigido — kit de remediação nunca rodou no Windows padrão

Descoberto ao executar os scripts **elevados sob Windows PowerShell 5.1** (o
shell padrão do Windows 10/11 e o usado por RMM). Uma verificação anterior havia
passado por usar o parser do PowerShell 7, que não reproduz nenhum dos dois
problemas.

- **5 dos 6 scripts não compilavam no PowerShell 5.1** (todos compilavam no 7).
  Os arquivos eram UTF-8 **sem BOM**; o 5.1 assume ANSI nesse caso, os acentos
  viravam mojibake e a corrupção quebrava terminadores de string. Todos os
  `.ps1` passaram a ser gravados como **UTF-8 com BOM**, que funciona nos dois.
  Verificado também via `cmd.exe`.
- **`Optimize-NicTuning.ps1` usava o operador ternário `? :`**, exclusivo do
  PowerShell 7 — erro de sintaxe no 5.1 mesmo depois do BOM. Reescrito com
  `if/else`.
- **`Optimize-NicTuning.ps1 -Status` lançava exceção** ("chamar um método em uma
  expressão de valor nulo"): procurava a string **em inglês**
  `'Receive Window Auto-Tuning'` na saída do `netsh`, que é **localizada**. Num
  Windows em português não casava, `$autotune` ficava `$null` e o `.Trim()`
  estourava. Passou a usar `Get-NetTCPSetting` (independente de idioma), com
  fallback tolerante a PT/EN e valor `n/d` quando indeterminado.
- **`Enable-SmbMultichannel.ps1 -Status` imprimia contagem vazia**
  ("Apenas ␣ NIC ativa detectada"): com uma única NIC o retorno é escalar e
  `.Count` sai vazio no 5.1. Corrigido com `@()` nos dois pontos de uso.

### Corrigido — funcionalidade

- **Wrapper RMM nunca executou.** `scripts/smbdoctor-rmm.ps1` usava `@rem`
  (sintaxe de `.bat`) como comentário num arquivo `.ps1`; falhava com erro de
  parse na primeira linha. Reescrito com `#`, busca do binário nos dois
  `Program Files` e no `PATH`, parâmetro `-Path`, tratamento de saída não-JSON e
  propagação correta de exit code. Testado ponta a ponta.
- **Dialeto desconhecido reportado como saudável** (fail-open): `"desconhecido"`
  caía no `_ =>` do switch e virava *"dialeto moderno"*. Passou a "não
  determinado", sem classificação.
- **Achado de multichannel somava no score de assinatura**: o relatório listava
  `Multichannel` e culpava `SmbSigning`, recomendando desligar assinatura para
  um problema que não era dela. Novo `Bottleneck.SmbMultichannel` com remediação
  própria — que, ao contrário, não reduz a postura de segurança.
- **Dialeto legado (SMB1/2.0) também era atribuído a `SmbSigning`**: movido para
  `Bottleneck.Protocol`, com remediação de protocolo.
- **Erro espúrio no relatório sem `--path`**: `GetAverageFileSize(null)` era
  chamado sem guarda (só `GetFileCount` tinha), estourava internamente e o
  `catch` amplo registrava `"workload: Value cannot be null"` em
  `CollectionErrors`. Guarda simétrica aplicada.

### Alterado — arquitetura

- **Separação de plataforma**: novo projeto `SmbSpeedDoctor.Core.Windows`
  (`net8.0-windows`) com `WindowsScanner` e `RealCopyProbe`. O `Core` fica puro
  (`net8.0`): modelo, motor, baseline. `Cli` passa a `net8.0-windows` (o publish
  já era `win-x64`). Ver `docs/ADR-0002-separacao-plataforma.md`.
- **Build sem avisos: 52 → 0.** 42 × CA1416 eliminados pela separação; 6 ×
  CS8604, 2 × CS8625 e 2 × CS9191 corrigidos anotando como `string?` as
  fronteiras que de fato aceitam null (`WindowsScanner` ctor, `RealCopyProbe.Decide`)
  e trocando `ref` por `in`.
- Removido `src/SmbSpeedDoctor.Core/Class1.cs` — arquivo vazio do template
  `dotnet new classlib`.

### Alterado — documentação

- **README**: escopo corrigido. Dizia "diagnóstico em Windows 10/11 e servidores
  Samba"; não existe coletor Linux — para Samba há apenas script de remediação.
  Adicionadas a tabela de procedência de medição, o aviso sobre efeito global do
  ajuste de assinatura e a seção de estado atual/pendências.
- `AUDIT.md`: a conclusão "nenhum segredo no repositório" extrapolava a busca,
  que cobriu apenas `src/` e `tests/` — `docs/` ficou de fora e era onde estava
  a senha. Ressalva de escopo registrada.
- `docs/AUDIT-1.1.0.md`: retratação do check de segredos e reavaliação dos
  avisos CA1416, antes classificados como "não é defeito".

### Testes

- **44/44 passando** (34 anteriores + 10 novos), todos escritos em RED antes da
  correção: 6 no `DiagnosisEngine` (medição ausente, dialeto desconhecido,
  multichannel no balde certo) e 4 em `RealCopyProbe.ResolveQuality`.
- Verificação de ponta a ponta na máquina real, além dos testes:
  `scan --json` → exit 0 sem remediação (antes: exit 2 recomendando desligar
  assinatura); `scan --json --path <dir>` → 95% de confiança, 85,3 MB/s
  (inalterado); wrapper RMM executando nos dois modos.
- **Scripts de remediação executados elevados** (UAC) em modo `-Status`
  read-only sob PowerShell 5.1: os 6 compilam em 5.1, `cmd.exe` e 7; NIC tuning
  e multichannel imprimem status correto; `Enable-JumboFrames -Status` testa o
  caminho até o gateway. A trava de segurança foi validada em execução real —
  `-Apply` sem `-AcceptGlobalSecurityImpact` é recusado e o estado SMB da
  máquina permanece idêntico antes/depois.
  Não executado: `-Apply` de fato (altera configuração da máquina) e a GUI.

### Pendente

- Rotação da credencial `hermes-smb` (prioridade 1 — histórico git).
- GUI ainda não validada em Windows real; binários não assinados.
- Sem CI: os 44 testes rodam apenas localmente.
- Sem coletor Linux/Samba.
- Projeto de teste é `net8.0-windows`; separar os testes de domínio puro num
  projeto `net8.0` está registrado como follow-up no ADR-0002.

## [1.1.0] — Estado anterior (master @ ad30e11)

### Corrigido
- **NRE crítica** (`fix --export` sem `--path`): construtor do `WindowsScanner` normaliza null→neutro; linha 465 null-safe. Auditoria de 3 propostas concorrentes documentada em `docs/proposals/item1-solucao-A.md` (A vencedora + refinamento B; C rejeitada por violar degradação graciosa). 4 testes novos reproduzem o bug.
- **Stack trace vazando no JSON de erro**: removido do stdout (contrato RMM limpo); detalhe vai para stderr.
- **Link speed implausível** (adaptadores virtuais reportando ≥4 Pb/s): rejeitado na origem com registro em `CollectionErrors`.
- **Rede ociosa classificada como gargalo**: `LinkUtilization` exige tráfego observado > 1 Mbit.
- **100 Gb/s fantasma no diagnóstico**: filtro de adaptadores virtuais (Hyper-V/VMware/TAP/Tailscale/WireGuard/OpenVPN/vEthernet) + preferência pela interface com gateway default.
- **Faixas de link recalibradas**: piso de credibilidade 10 Mbit (enlaces legítimos); enlace ≤ 100 Mbit vira achado de infraestrutura (cabo/porta), peso 8.

### Adicionado
- **Item 2 — Baseline comparativo**: `scan --save arquivo.json` e `scan --compare arquivo.json` com tabela antes→depois e veredito MELHOROU/PIOROU/ESTÁVEL por métrica (throughput ±10%, latência ±15%, perda ±0,5 pp).
- **Item 3 — Cópia de teste real**: `RealCopyProbe` escreve/lê arquivo-probe no share alvo medindo MB/s real (teto = menor entre write/read); fallback para aproximação NIC com erro registrado; `--no-copy` desativa.
- **Item 9 — Robocopy profiled**: `copyMethod` do JSON agora é gerado pelo perfil medido (`/J` p/ grandes, `/MT:N` p/ muitos pequenos, `/ZB` se link instável) com rationale explicável e estimativa de throughput limitada pelo menor teto (link/cópia/disco).
- **Item 6 — NIC tuning kit**: `scripts/remediation/Optimize-NicTuning.ps1` (RSS, TCP autotuning, power-saving da NIC) com Apply/Rollback/Status e backup em `%ProgramData%\SmbSpeedDoctor\`.
- **Item 7 — Jumbo frames condicional**: `scripts/remediation/Enable-JumboFrames.ps1` testa MTU fim-a-fim (DF set) antes de aplicar; auto-reversão se conectividade degradar.
- **Item 8 — Perfil Samba servidor**: `scripts/remediation/Optimize-SambaServer.ps1` (TCP_NODELAY, buffers 128K, AIO 16K, multichannel condicional a 2+ NICs). **Aplicado de verdade na instância Hermes** — `testparm` confirma; backup em `/var/lib/smb-speed-doctor/`.
- **Item 5 — Multichannel cliente**: `scripts/remediation/Enable-SmbMultichannel.ps1` com detecção de viabilidade (NICs ativas + RSS) e alerta quando não há ganho possível.
- **Item 4 — SMB Signing 24H2**: `scripts/remediation/Set-SmbSigningOptimized.ps1` ~~escopado~~ (Require off + Enable on como tuning), backup/rollback exatos, aviso de risco MITM/tampering.
  > **Correção (1.2.0):** o script **nunca foi escopado**. O parâmetro `-Subnet` era decorativo e o efeito sempre foi global. Ver a seção 1.2.0.
- **Item 1 — Fix assistido**: `fix --export caminho.ps1` gera script PowerShell com autoria, MIT, `#Requires -RunAsAdministrator`, modo `-WhatIf` obrigatório, comandos de aplicação e rollback comentado por família de remediação. O app nunca executa nada.
- Flags novas aceitas no CLI: `--save`, `--compare`, `--export`, `--no-copy`; `--help`/`-h`; flags desconhecidas rejeitadas (exit 1).

### Testes
- 34/34 passando (25 anteriores + 4 WindowsScanner/NRE + 5 RobocopyBuilder).
- Smoke tests reais: baseline save→compare ESTÁVEL; cópia real em tmpdir ~89 MB/s; fix --export gera script; CLI sem crash em Linux.

## [1.0.0] — Versão inicial de teste
- Motor de correlação (rede/SMB signing+encryption/disco origem-destino/CPU/antivírus/workload) com pontuação por camada, remediações com rollback explícito e exit codes RMM 0/1/2.
- Coletores Windows reais via WMI (`MSFT_SmbClientConfiguration`, `MSFT_SmbConnection`, PerfDisk/PerfOS) + Ping (20 sondas) + amostragem NIC; degradação graciosa com `CollectionErrors`.
- CLI console (`--json`, `--quiet`, `--path`) e GUI WinForms (botão MEDIR AGORA).
- Cross-build Linux→win-x64 validado; versão de teste gratuita/ilimitada, MIT.

[1.1.0]: https://github.com/Seguranca-do-Trabalho/smb-speed-doctor/compare/1b6fe56...ad30e11
[1.0.0]: https://github.com/Seguranca-do-Trabalho/smb-speed-doctor/tree/e65801f
