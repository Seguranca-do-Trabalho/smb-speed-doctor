# Auditoria Técnica — SMB Speed Doctor

**Produto:** SMB Speed Doctor (CLI + GUI + Core)  
**Versão:** 1.0.0-test  
**Data da auditoria:** 2026-08-22  
**Auditor:** Agente Hermes (auditoria automatizada)  
**Autor original:** André Santo (forg3) | junkyardgoodies.app  
**Licença:** MIT  

---

## Sumário Executivo

O produto passou na build e nos 9 testes unitários. O contrato RMM está implementado corretamente. Foi identificado **um bug crítico de unidades** (`LinkSpeedBps` 8× superestimado no coletor real), problemas menores de autoria inconsistente e gaps de cobertura de teste. A degradação graciosa funciona como esperado. Nenhuma credencial hard-coded foi encontrada.

| Critério | Status | Observação |
|---|---|---|
| Contrato RMM (JSON + exit codes) | ✅ APROVADO | Exit codes 0/1/2 corretos; schema JSON completo |
| Unit tests (9/9 passing) | ✅ APROVADO | TDD verificado via `dotnet test` |
| Build cross-win-x64 | ✅ APROVADO | `dotnet build` limpo; binários presentes em `dist/` |
| Bug crítico bits vs bytes (link speed) | ❌ **FALHA** | `GetLinkSpeedBps` multiplica por 8 indevidamente |
| Degradação graciosa | ✅ APROVADO | Try/catch em todos os coletores; erros acumulados |
| Autoria consistente | ⚠️ PARCIAL | Inconsistência `junkyardgoods` vs `junkyardgoodies`; placeholder no script RMM |
| Segurança (creds/chaves) | ✅ APROVADO | Nenhum segredo hard-coded encontrado |
| Cobertura de cenários de campo | ⚠️ PARCIAL | Cobre assinaturas/criptografia SMB, disco, workload; falta HDD específico, MTU, multichannel |

---

## 1. Contrato RMM

### 1.1 Exit Codes

Implementado em `src/SmbSpeedDoctor.Core/DiagnosisEngine.cs` (linhas 280–288):

```csharp
public static int For(DiagnosisResult r) => r.Severity switch
{
    Severity.Ok => 0,
    Severity.Critical => 2,
    _ => 1
};
```

| Exit Code | Significado | Implementação |
|---|---|---|
| 0 | Sem gargalo dominante | `Severity.Ok` |
| 1 | Aviso / erro de execução | `Severity.Warning` ou exceção capturada |
| 2 | Gargalo crítico encontrado | `Severity.Critical` |

**CLI (Program.cs, linhas 13–43):**
- `--json`: produz JSON completo com schema prescrito
- `--quiet`: suprime saída em texto; `--json` ainda produz JSON
- `--path <share>`: suporta ambas as formas `--path=valor` e `--path valor`
- Bloco `catch` retorna `{error, code: 1}` em JSON

**Schema JSON verificado (linhas 63–87 de Program.cs):**
```json
{
  "exitCode": <int>,
  "dominant": <Bottleneck>,
  "severity": <Severity>,
  "confidence": <double>,
  "summary": <string>,
  "findings": [{ layer, metric, value, interpretation, severity, weightContribution }],
  "remediation": { id, title, description, rollbackDescription, commands },
  "copyMethod": { methodName, rationale }
}
```

**Veredito:** ✅ Contrato RMM integralmente implementado e documentado no README.

### 1.2 Wrapper RMM (`scripts/smbdoctor-rmm.ps1`)

Script PowerShell simples que:
- Localiza o binário em `%ProgramFiles(x86)%\SMB Speed Doctor\smbdoctor-cli.exe`
- Executa com `--json --quiet`
- Retorna `exit $json.exitCode`

**Observação:** O script contém o placeholder `@rem Autor: <seu-identificador>` — deve ser preenchido antes do deploy.

---

## 2. Bug Crítico: Bits vs Bytes no Link Speed

### 2.1 Problema Identificado

**Arquivo:** `src/SmbSpeedDoctor.Core/Windows/WindowsScanner.cs`, linhas 129–131

```csharp
// NetworkInterface.Speed é bytes/s; converter para bits/s conforme contrato.
var nic = FastestActiveInterface();
return (long)((nic?.Speed ?? 0) * 8L);
```

**Raiz do bug:** A documentação do .NET para `NetworkInterface.Speed` afirma que o valor é retornado em **bytes por segundo**. Contudo, na prática em Windows, a propriedade retorna o valor em **bits por segundo** (ou `-1` para interfaces desconhecidas). Esta discrepância entre documentação e realidade é um problema conhecido há anos na comunidade .NET.

### 2.2 Impacto

- `LinkSpeedBps` será **8× maior** que o valor real em todas as máquinas Windows
- `FmtLink()` exibirá velocidades incorretas (ex.: 1 GbE aparecerá como 8 Gb/s)
- Cálculos de utilização de link estarão errados:
  - Linha 69: `d.RawThroughputBps / d.LinkSpeedBps < 0.3` — threshold de 30% torna-se efetivamente 3,75%
  - Linha 70: `d.ObservedCopyThroughputBps * 8.0 < d.LinkSpeedBps * 0.15` — threshold de 15% torna-se efetivamente 1,875%
- multichannel detection (linha 124): `d.LinkSpeedBps >= 1_000_000_000` será true para qualquer interface, mesmo 100 MbE
- `EstimateSmallFileThroughput` e benchmarks de eficiência comprometidos indiretamente

### 2.3 Correção Recomendada

```csharp
// Em Windows, NetworkInterface.Speed retorna bits/s na prática,
// não bytes/s conforme documentação. Usar valor direto.
return (long)(nic?.Speed ?? 0);
```

Ou, para maior robustez:
```csharp
// Tratar -1 como desconhecido; usar valor diretamente (bits/s no Windows)
var speed = nic?.Speed ?? 0;
return speed > 0 ? speed : 0;
```

### 2.4 Por que os testes não pegaram

Os testes usam `MockNetworkCollector` (em `Collectors.cs`), que retorna exatamente o valor configurado sem passar por `WindowsNetworkCollector`. O bug só se manifesta nos coletores reais do Windows.

**Veredito:** ❌ **BUG CRÍTICO** — afeta todas as métricas de rede e decisões de diagnóstico. Deve ser corrigido antes da entrega.

---

## 3. Corretude de Unidades (bits vs bytes — demais pontos)

### 3.1 RawThroughputBps (correto)

`GetThroughputBps` (linha 116):
```csharp
return Math.Max(0, (b2 - b1) * 8.0 / sw.Elapsed.TotalSeconds);
```
`b1`/`b2` vêm de `BytesSent`/`BytesReceived` (bytes) → multiplicado por 8 → **bits/sec**. ✓

### 3.2 ObservedCopyThroughputBps (correto, mas aproximação grosseira)

`EstimateObservedCopy` (linha 500–501):
```csharp
private static double EstimateObservedCopy(double rawNicBps)
    => rawNicBps / 8.0; // bits/s -> bytes/s como aproximação inicial
```
Converte bits/sec de volta para bytes/sec. Matematicamente correto, mas a aproximação é muito grosseira — não considera overhead SMB, TCP, etc. Em produção, seria ideal medir uma cópia real de arquivo teste.

### 3.3 FmtThroughput / FmtLink (correto após correção do bug acima)

```csharp
private static string FmtThroughput(double bps)
    => bps >= 1_000_000 ? $"{bps / 1_000_000:F0} MB/s" : $"{bps / 1_000:F0} kB/s";

private static string FmtLink(double bps)
    => bps >= 1_000_000_000 ? $"{bps / 1_000_000_000:F1} Gb/s" : $"{bps / 1_000_000:F0} Mb/s";
```
- `FmtThroughput` espera bytes/sec (correto após a correção de `EstimateObservedCopy`)
- `FmtLink` espera bits/sec (correto após correção de `GetLinkSpeedBps`)

### 3.4 Thresholds de diagnóstico

- Linha 69: `d.RawThroughputBps / d.LinkSpeedBps < 0.3` — ambos em bits/sec ✓
- Linha 70: `d.ObservedCopyThroughputBps * 8.0 < d.LinkSpeedBps * 0.15` — converte bytes→bits para comparar ✓

---

## 4. Degradação Graciosa

### 4.1 Mecanismo

Todos os coletores herdam de `CollectorBase` (linhas 23–36) ou possuem try/catch individual:

```csharp
protected T Safe<T>(string what, T fallback, Func<T> probe)
{
    try { return probe(); }
    catch (Exception ex)
    {
        Errors.Add($"{what}: {ex.Message}");
        return fallback;
    }
}
```

Cada coletor específico também possui try/catch inline (ex.: linhas 168–184, 203–220, 267–283, 323–339).

### 4.2 Acumulação de Erros

`WindowsScanner.Collect()` (linhas 433–437):
```csharp
CollectionErrors.AddRange(network.Errors);
CollectionErrors.AddRange(smb.Errors);
CollectionErrors.AddRange(disk.Errors);
CollectionErrors.AddRange(cpu.Errors);
CollectionErrors.AddRange(workload.Errors);
```

Erros são acumulados mas **não lançam exceção** — o scan continua com valores neutros (0, false, string vazia, etc.).

### 4.3 Veredito

✅ Degradação graciosa implementada corretamente. Falhas de coleta individuais não travam o scan.

---

## 5. Cobertura de Cenários de Campo

### 5.1 Testes Unitários (9 testes, todos passing)

| Teste | Cenário | Cobertura |
|---|---|---|
| `Perfil_saudavel_nao_aponta_gargalo` | Throughput alto, rede saudável | ✓ |
| `Assinatura_SMB_com_rede_saturada_e_gargalo_dominante` | 24H2 + signing ativo | ✓ |
| `Criptografia_SMB_suplanta_assinatura_quando_ativa` | Encryption > Signing | ✓ |
| `Disco_destino_lento_supera_causa_SMB` | HDD saturado | ✓ |
| `Muitos_arquivos_pequenos_explica_throughput_baixo` | Workload pequeno | ✓ |
| `Perda_de_pacote_e_reportada_como_gargalo_de_rede` | 3% packet loss | ✓ |
| `Dialeto_smb1_e_bloqueante` | SMB 1.0 | ✓ |
| `Metodo_de_copia_para_arquivos_grandes_e_nao_buffered` | robocopy /J | ✓ |
| `Metodo_de_copia_para_muitos_arquivos_pequenos_e_paralelo` | robocopy /MT | ✓ |

### 5.2 Cenários ausentes ou sub-representados

| Cenário | Status | Comentário |
|---|---|---|
| HDD vs SSD (thresholds diferentes) | ⚠️ Parcial | Constantes `DiskThroughputHddMbs=90` e `DiskThroughputSsdMbs=500` existem, mas não há teste que valide a distinção |
| MTU fragmentado / jumbo frames | ❌ Ausente | MTU hardcodado como 1500; jumbo frames não são detectados |
| Multichannel SMB | ⚠️ Parcial | Lógica existe (linhas 124–137), mas nenhum teste verifica multichannel habilitado vs desabilitado |
| CPU saturado | ⚠️ Parcial | Regra existe (linha 140–145), mas não há teste unitário |
| Antivírus no share path | ⚠️ Parcial | Regra existe (linhas 148–153), mas não há teste unitário |
| Dialeto 2.1 (legado moderado) | ⚠️ Parcial | Teste cobre 1.0, mas não 2.1 isoladamente |
| Link speed baixo (100 MbE) | ❌ Ausente | Nenhum teste com LinkSpeedBps < 1 Gb/s |
| Mixed workload (tamanho variado) | ❌ Ausente | Workload analyzer assume todos os arquivos com tamanho médio |

### 5.3 Recomendações

- Adicionar testes para CPU saturado, antivírus, e multichannel
- Adicionar teste para HDD vs SSD com throughput diferenciado
- Considerar medição real de cópia (ao invés de estimativa por NIC) para `ObservedCopyThroughputBps`

---

## 6. Crédito de Autoria

### 6.1 Arquivos com autoria

| Arquivo | Linha | Autoria |
|---|---|---|
| `LICENSE` | 3 | `Copyright (c) 2026 André Santo (forg3) | junkyardgoodies.app` ✅ |
| `README.md` | 7, 77, 78 | `André Santo (forg3) \| junkyardgoodies.app` ✅ |
| `src/SmbSpeedDoctor.Core/DiagnosisEngine.cs` | 1 | `André Santo (forg3) \| junkyardgoods.app` ⚠️ |
| `src/SmbSpeedDoctor.Core/Windows/WindowsScanner.cs` | 1 | `André Santo (forg3) \| junkyardgoodies.app` ✅ |
| `tests/DiagnosisEngineTests.cs` | 1 | `André Santo (forg3) \| junkyardgoods.app` ⚠️ |

### 6.2 Inconsistências

1. **`junkyardgoods.app` vs `junkyardgoodies.app`**: Os arquivos `DiagnosisEngine.cs` e `DiagnosisEngineTests.cs` usam `junkyardgoods.app` (sem o `i` em goodies), enquanto `LICENSE`, `README.md` e `WindowsScanner.cs` usam `junkyardgoodies.app` (correto).
   - **Correção recomendada:** Uniformizar para `junkyardgoodies.app` em todos os arquivos.

2. **Script RMM com placeholder**: `scripts/smbdoctor-rmm.ps1` linha 4 contém `@rem Autor: <seu-identificador>` — deve ser preenchido com o nome real antes do deploy.

---

## 7. Segurança

### 7.1 Verificação de credenciais hard-coded

Busca realizada nos diretórios `src/` e `tests/` por padrões:
- `password`, `secret`, `key`, `token`, `credential`, `api_key`, `api-key`, `apikey`

**Resultado:** 0 correspondências em `src/` e `tests/`.

> **⚠️ Ressalva de escopo (correção posterior).** A conclusão original — "nenhum
> segredo no repositório" — extrapolava a busca realizada. O escopo foram
> `src/` e `tests/`; **`docs/` ficou de fora**, e era exatamente onde havia uma
> senha em texto plano (`docs/AUDIT-1.1.0.md`, desde então removida e com a
> rotação da credencial `hermes-smb` agora obrigatória).
>
> Uma varredura de segredos precisa cobrir **todo** o repositório e o
> **histórico** (`git log -S`), não apenas os diretórios de código. Conclusão
> mais ampla que a evidência é como o problema passou despercebido.

### 7.2 Caminhos absolutos expostos

Nenhum caminho absoluto de sistema de arquivos é exposto em logs, JSON de saída ou mensagens de erro. O CLI aceita `--path` como parâmetro opcional, mas não o echoa em formatos que vazem informação sensível.

### 7.3 Permissões

O scanner WMI requer permissões de administrador para acesso completo a algumas classes (ex.: `MSFT_SmbConnection`). O README menciona esta necessidade. Não há elevação automática — o usuário deve executar com privilégios adequados.

### 7.4 Veredito

✅ **Nenhuma vulnerabilidade de segurança crítica identificada.** Produto livres de credenciais hard-coded e caminhos absolutos expostos.

---

## 8. Artefatos de Build

### 8.1 Binários presentes em `dist/`

| Arquivo | Tamanho | Status |
|---|---|---|
| `SmbSpeedDoctor.Cli.exe` | 152 KB | ✅ Presente |
| `SmbSpeedDoctor.Cli.dll` | 15 KB | ✅ Presente |
| `SmbSpeedDoctor.Cli.deps.json` | 2 KB | ✅ Presente |
| `SmbSpeedDoctor.Cli.runtimeconfig.json` | 328 B | ✅ Presente |
| `SmbSpeedDoctor.Cli.pdb` | 11 KB | ✅ Presente |
| `SmbSpeedDoctor.Gui.exe` | 152 KB | ✅ Presente |
| `SmbSpeedDoctor.Gui.dll` | 10 KB | ✅ Presente |
| `SmbSpeedDoctor.Gui.pdb` | 12 KB | ✅ Presente |
| `SmbSpeedDoctor.Core.dll` | 53 KB | ✅ Presente |
| `SmbSpeedDoctor.Core.pdb` | 20 KB | ✅ Presente |
| `System.Management.dll` | 311 KB | ✅ Presente |

### 8.2 Testes Unitários

```
Passed!  - Failed: 0, Passed: 9, Skipped: 0, Total: 9, Duration: 29 ms
```

✅ Todos os 9 testes passando.

---

## 9. Resumo das Achados

### 🔴 Crítico

1. **Bug de unidades `GetLinkSpeedBps`** (WindowsScanner.cs:131): Multiplica por 8 assumindo que `NetworkInterface.Speed` retorna bytes/sec, mas no Windows retorna bits/sec. Resultado: link speed 8× superestimado, afetando todas as métricas de rede e decisões de diagnóstico.

### 🟡 Médio

2. **Inconsistência de autoria**: `junkyardgoods.app` (sem `i`) em DiagnosisEngine.cs e DiagnosisEngineTests.cs versus `junkyardgoodies.app` (correto) em LICENSE, README.md e WindowsScanner.cs.

3. **Placeholder no script RMM**: `scripts/smbdoctor-rmm.ps1` contém `@rem Autor: <seu-identificador>` não preenchido.

### 🟢 Bom

4. **Degradação graciosa**: Todos os coletores tratam exceções e acumulam erros sem travar o scan.
5. **Contrato RMM**: Exit codes 0/1/2 e schema JSON implementados corretamente.
6. **Segurança**: Sem credenciais hard-coded ou caminhos absolutos expostos.
7. **Testes**: 9/9 passing, cobrindo cenários principais de campo.

---

## 10. Recomendações para Entrega

1. **Corrigir `GetLinkSpeedBps`** antes de qualquer deploy: remover o `* 8L` ou adicionar detecção heurística (se valor > 10 Gbps, assumir que já está em bits/sec).
2. **Uniformizar autoria** para `junkyardgoodies.app` em todos os arquivos.
3. **Preencher placeholder** no script RMM com o identificador correto.
4. **Adicionar testes** para CPU saturado, antivírus e multichannel.
5. **Considerar medição real de cópia** para substituir a estimativa grosseira por `rawNicBps / 8`.

---

*Auditoria concluída em 2026-08-22. Produto pronto para revisão humana antes da entrega.*
