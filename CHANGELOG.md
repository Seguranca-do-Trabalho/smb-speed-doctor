# CHANGELOG — SMB Speed Doctor

Formato: Keep a Changelog. Datas em 2026-08-22 (America/Sao_Paulo).
Autor de todas as mudanças: engenharia assistida por Hermes, sob direção de André Santo (forg3) | junkyardgoodies.app.

## [1.1.0] — Estado atual (master @ ad30e11)

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
- **Item 4 — SMB Signing 24H2**: `scripts/remediation/Set-SmbSigningOptimized.ps1` escopado (Require off + Enable on como tuning), backup/rollback exatos, aviso de risco MITM/tampering.
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
