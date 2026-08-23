# ADR-0003 — Procedência da medição como pré-requisito para diagnóstico

**Status:** Aceito
**Data:** 2026-08-23

---

## Contexto

Rodando `smbdoctor-cli scan --json` numa máquina Windows 11 comum, com a rede
ociosa e **sem** `--path`, o resultado era:

```json
{
  "exitCode": 2,
  "dominant": "SmbSigning",
  "severity": "Critical",
  "confidence": 60,
  "summary": "Rede é 1,0 Gb/s, mas cópia SMB cai para 1 kB/s. O gargalo dominante é a assinatura SMB.",
  "remediation": {
    "Id": "SMB_SIGNING_DISABLE_CLIENT",
    "Commands": ["Set-SmbClientConfiguration -RequireSecuritySignature $false", "..."]
  }
}
```

Nenhum compartilhamento foi testado. Nenhuma cópia foi feita. Ainda assim o
programa declarou gargalo crítico, devolveu exit 2 e recomendou **desligar a
assinatura SMB** — um downgrade de segurança.

### Como acontecia

Sem `--path`, `RealCopyProbe.Decide()` devolve `FallbackToApproximation` e o
throughput passa a vir de:

```csharp
private static double EstimateObservedCopy(double rawNicBps)
    => rawNicBps / 8.0;
```

Numa rede parada, o tráfego de fundo da NIC rende ~886 bytes/s. No motor:

```csharp
double efficiency = (d.ObservedCopyThroughputBps * 8.0) / d.LinkSpeedBps; // ≈ 0,000007
double smbScore   = efficiency < 0.15 ? 9.0 : ...;                        // → 9.0 = Critical
```

O defeito de fundo: **"não medi" e "medi e deu quase zero" chegavam ao motor
como o mesmo `double`.** Não havia como distinguir ausência de evidência de
evidência de problema.

Esse erro já era conhecido no projeto. O commit `562c502` — *"LinkUtilization
exige tráfego observado (rede ociosa não é gargalo)"* — corrigiu exatamente
isto, mas **apenas na regra de `LinkUtilization`**. As regras de `SmbSigning`,
`Multichannel`, disco, CPU e workload continuaram lendo o mesmo número
indistinguível. A correção pontual não virou invariante.

## Decisão

Tornar a **procedência** do throughput parte explícita do modelo, e não uma
convenção implícita:

```csharp
public enum MeasurementQuality
{
    Unavailable,   // sem medição válida — não sustenta conclusão
    Approximated,  // estimado de tráfego NIC acima do piso de credibilidade
    Measured,      // cópia de teste real executada e cronometrada
}
```

Regras adotadas:

1. `ScanData.ThroughputQuality` acompanha `ObservedCopyThroughputBps`. O valor
   **default é `Unavailable`** — seguro por omissão: quem não declara que mediu
   não recebe conclusão sobre throughput.
2. Toda regra que conclui a partir de throughput exige
   `HasUsableThroughput`: assinatura, multichannel, disco origem/destino, CPU e
   utilização de enlace.
3. Sinais **diretos** seguem valendo sem medição: perda de pacote, velocidade de
   enlace negociada, dialeto, criptografia, filtro de antivírus. O scan rápido
   continua útil.
4. Sem medição, o relatório traz um achado explícito (`ThroughputQuality =
   unavailable`) dizendo o que não foi verificado e como verificar, e a
   confiança cai (95 → 55 quando nada mais é encontrado). Ausência de evidência
   não é evidência de ausência.
5. `RealCopyProbe.ResolveQuality()` decide a procedência num único lugar
   testável. Piso de credibilidade da aproximação: **1 MB/s**
   (`MinCredibleApproximationBps`). Abaixo disso é tráfego de fundo, não cópia.

### Correções acopladas, da mesma família

- **Dialeto desconhecido tratado como saudável.** `"desconhecido"` caía no
  `_ =>` do switch e virava *"dialeto moderno"* — dado ausente convertido em
  parecer positivo (fail-open). Agora é reportado como "não determinado", sem
  classificação.
- **Multichannel somando no balde de assinatura.** O achado de multichannel
  incrementava `scores[Bottleneck.SmbSigning]`; o relatório listava um problema
  e culpava outro, recomendando desligar assinatura para algo que não era ela.
  Passou a ter `Bottleneck.SmbMultichannel` e remediação própria — que, ao
  contrário de desligar assinatura, **não reduz a postura de segurança**.
- **Dialeto legado (SMB1/2.0) também apontava para `SmbSigning`.** Movido para
  `Bottleneck.Protocol`, com remediação de protocolo.

## Alternativas consideradas

| Alternativa | Por que não |
|---|---|
| Tornar `--path` obrigatório | Resolve, mas quebra o contrato da CLI e chamadas RMM existentes que usam `scan` puro. |
| Só elevar o piso da aproximação | É a correção pontual do `562c502` repetida. Sem um conceito no modelo, a próxima regra nasce com o mesmo defeito. |
| Manter e documentar no README | O comportamento perigoso continua sendo o padrão; ninguém lê README antes de um exit code 2 do RMM. |

## Consequências

- `scan` sem `--path` deixou de recomendar downgrade de segurança sem evidência.
- Exit code do cenário padrão mudou de **2** para **0** — quem alertava com base
  nisso no RMM deve reavaliar (o alerta anterior era falso positivo).
- Cenários realmente medidos ficam idênticos: 95% de confiança, mesmo veredicto.
- Custo: mais um campo no `ScanData` e a disciplina de declará-lo ao construir.

## Verificação

Testes que falham sem a correção e passam com ela:

- `Sem_medicao_real_nao_acusa_assinatura_como_gargalo`
- `Sem_medicao_real_nao_recomenda_desabilitar_assinatura`
- `Sem_medicao_real_declara_a_limitacao_e_baixa_a_confianca`
- `Sem_medicao_real_ainda_reporta_achados_independentes_de_throughput`
- `Dialeto_desconhecido_nao_e_interpretado_como_moderno`
- `Multichannel_desabilitado_nao_e_contabilizado_como_assinatura`
- `Copia_real_bem_sucedida_e_medicao_Measured`
- `Sem_copia_e_com_rede_ociosa_e_Unavailable`
- `Sem_copia_mas_com_trafego_real_e_Approximated`
- `Copia_real_que_falhou_nao_vira_Measured`

Verificação de ponta a ponta, na máquina real:

```
scan --json            → dominant None, severity Ok, exit 0, sem remediação  (antes: SmbSigning/Critical/exit 2)
scan --json --path X   → dominant None, severity Ok, exit 0, confiança 95     (inalterado)
```
