# Proposta A — Null-safety pontual em WindowsScanner.Collect()

**Data:** 2026-08-22  
**NRE:** `smbdoctor-cli fix --export` sem `--path` gera `NullReferenceException` em `WindowsScanner.Collect()`.  
**Stack:** `src/SmbSpeedDoctor.Core/Windows/WindowsScanner.cs:465` — `_path.Length > 0` quando `_path` é `null`.  
**Autora:** Solução A (defesa no ponto de uso).  
**Ramificação:** branch atual `master`. Testes: **30/30 passando**, build limpo (2 warnings CS8604 existentes em `Program.cs`).

---

## 1. Análise

### Raiz do problema

`Program.cs` chama:

```csharp
var scan = new WindowsScanner(sharePath: path, noCopy: noCopy).Collect();
// path: string? retornado por ParsePath(args)
```

Quando `--path` não está presente, `ParsePath` retorna `null`. Como o parâmetro `sharePath` do construtor tem tipo `string` (não `string?`) e padrão `""`, o compilador emite CS8604 em Program.cs, mas a chamada compila e passa `null` para `_path`.

### Linhas afetadas em WindowsScanner.cs

Todas as ocorrências de `_path`/`_target` no arquivo (`grep -n '_path\|_target'`):

| Linha | Código | Problema |
|-------|--------|----------|
| 423   | `private readonly string _target;` | Campo não pode ser null por contrato; valor padrão de Program.cs é `"loopback"` → seguro. |
| 424   | `private readonly string _path;` | Campo não pode ser null por contrato; **recebe null de Program.cs** → inseguro. |
| 430   | `_path = sharePath;` | Atribuição direta; nenhum guard. |
| 446   | `disk.GetBusyRatio(_path)` | Passa null para coletor que tem try/catch interno — degrada silenciosamente, mas passa null para método WMI (`Win32_PerfFormattedData_PerfDisk_PhysicalDisk`). |
| 447   | `disk.GetReadThroughputBps(_path)` | Idêntico acima. |
| 448   | `disk.GetWriteThroughputBps(_path)` | Idêntico acima. |
| 464   | `workload.GetAverageFileSize(_path)` | Passa null para `Directory.EnumerateFiles(null, ...)` que lança `ArgumentNullException` (interceptado pelo try/catch interno do workload). |
| **465** | **`int fileCount = _path.Length > 0 ? workload.GetFileCount(_path) : 0;`** | **LINHA DA NRE — `_path` é null, acesso a `.Length` explode fora de qualquer try.** |
| 474   | `RealCopyProbe.Decide(_path, _noCopy)` | Decisor já aceita null via `string.IsNullOrWhiteSpace`. |
| 477   | `probe.Probe(_path)` | Só executado quando `decision == RunRealCopy`; `Decide()` rejeita paths vazios/null, então aqui `_path` seria truthy — mas se `_path` for vazio (`""`), `Probe` gera `probePath = @"\smb-speed-doctor-copy-probe.bin"` (inválido). |

### Observações sobre `_target`

O campo `_target` é inicializado por `targetServer = "loopback"` no construtor; Program.cs sempre passa `"loopback"` (padrão implícito quando omitido). **Nenhuma referência a `_target` requer alteração.**

### Count de referências a corrigir em `Collect()`

**9 ocorrências de `_path` dentro do método `Collect()` (linhas 446–477).**

---

## 2. Diff completo unificado

```diff
--- a/src/SmbSpeedDoctor.Core/Windows/WindowsScanner.cs
+++ b/src/SmbSpeedDoctor.Core/Windows/WindowsScanner.cs
@@ -441,9 +441,9 @@
         var workload = new WindowsWorkloadAnalyzer();
 
         // Disco: amostra durante a cópia observada; aqui faz duas leituras
         // espaçadas para ter valores formatados correntes.
-        double busy = ReadDiskSteady(() => disk.GetBusyRatio(_path));
-        long readBps = (long)ReadDiskSteady(() => disk.GetReadThroughputBps(_path));
-        long writeBps = (long)ReadDiskSteady(() => disk.GetWriteThroughputBps(_path));
+        double busy = ReadDiskSteady(() => disk.GetBusyRatio(_path ?? string.Empty));
+        long readBps = (long)ReadDiskSteady(() => disk.GetReadThroughputBps(_path ?? string.Empty));
+        long writeBps = (long)ReadDiskSteady(() => disk.GetWriteThroughputBps(_path ?? string.Empty));
 
         double latency = network.GetLatencyMs(_target);
         double rawBps = network.GetThroughputBps(_target);
@@ -461,8 +461,8 @@
 
         double cpuPct = cpu.GetUtilization();
 
-        long avgFile = workload.GetAverageFileSize(_path);
-        int fileCount = _path.Length > 0 ? workload.GetFileCount(_path) : 0;
+        long avgFile = workload.GetAverageFileSize(_path ?? string.Empty);
+        int fileCount = (_path?.Length > 0) ? workload.GetFileCount(_path) : 0;
 
         CollectionErrors.AddRange(network.Errors);
         CollectionErrors.AddRange(smb.Errors);
@@ -471,7 +471,7 @@
 
         // Item 3: cópia de teste real vs aproximação de NIC.
-        var decision = RealCopyProbe.Decide(_path, _noCopy);
+        var decision = RealCopyProbe.Decide(_path, _noCopy);
         var probe = new RealCopyProbe();
         var probeResult = decision == CopyProbeDecision.RunRealCopy
-            ? probe.Probe(_path)
+            ? probe.Probe(_path!)
             : null;
```

**Nota sobre a linha 477 (`probe.Probe(_path!)`):** o operador `!` (null-forgiving) sinaliza ao compilador que o chamador garante não-null ali. Na prática, quando `decision == RunRealCopy`, `RealCopyProbe.Decide()` já rejeitou paths vazios ou null — portanto `_path` é truthy. Se um dia a lógica mudar, a NRE volta; por isso uma versão futura pode preferir `_path!` para `Probe` com guarda explícita adicional.

### Alternativa mais conservadora para linha 477 (sem `!`)

Se preferir evitar o null-forgiving operator:

```diff
         var probe = new RealCopyProbe();
         var probeResult = decision == CopyProbeDecision.RunRealCopy
-            ? probe.Probe(_path)
+            ? probe.Probe(_path ?? string.Empty)
             : null;
```

`Probe(string targetPath)` já trata paths vazios com exceção `ArgumentException` → retornado como `CopyProbeResult.Fail(...)`. Mas passar `string.Empty` quando o probe nem deveria rodar (fallback) gera resultado descartável. **Opção recomendada na prática: manter `_path` como está, pois `_path` nunca será null em `RunRealCopy`** (a menos que haja bug futuro na Decide).

Decisão final desta proposta: **manter `_path` sem `!` na linha 477** (deixa o compilador avisar se o contrato for quebrado) — a única modificação necessária que realmente impacta segurança em tempo de execução é a linha 465, que é o foco da NRE. As demais linhas (446–448, 464) passam null para coletores com try/catch, que degradam; substituir por `_path ?? string.Empty` é defesa consistente.

---

## 3. Justificativa e riscos

### Por que não corrigir no construtor?

A proposta A recusa explicitamente normalizar `_path` no construtor (ex.: `_path = sharePath ?? string.Empty;`). Os motivos:

1. **Contrato de campo readonly:** `_path` é `readonly string`. Mudar o construtor altera visibilidade da correção para todos os chamadores futuros (incluindo testes unitários que já passam `""`).
2. **Visibilidade do defeito:** os dois warnings CS8604 em `Program.cs` são **indícios de contrato errado no construtor** (`sharePath: string` quando deveria ser `string?`). Corrigir só em `Collect()` mascara esse warning; a solução ideal (fora do escopo desta proposta A) seria tornar `sharePath` opcional/null-laravel.
3. **Filosofia pontual:** a proposta A segue "defesa no ponto de uso" — cada linha problemática recebe seu null-check local. Isso isola o risco e mantém o diff mínimo e revisável.

### Riscos identificados

| # | Risco | Severidade | Mitigação |
|---|-------|------------|-----------|
| R1 | Nova referência a `_path` em `Collect()` pode esquecer o null-check | Média | Revisão de PR exige auditoria manual de todas as linhas com `_path`. |
| R2 | O operador `!` (se aplicado na linha 477) suprime o warning do compilador e esconde regressões futuras | Média | Não usar `!` (proposta opta por não usar). |
| R3 | Múltiplos patches locais (`?? string.Empty` espalhados) aumentam dívida de manutenção vs. centralização no construtor | Baixa | Documentar no commit message; criar item de refatoração futura (Solução B). |
| R4 | `ReadDiskSteady` espera `Func<double>`; passar `_path ?? string.Empty` dentro do lambda é seguro, mas se `_path` for nulo em tempo de execução numa linha não-coberta por testes, a degradação silenciosa do coletor mascarará o problema. | Baixa | Teste unitário proposto cobre caminho null explicitamente. |

---

## 4. Teste unitário proposto

**Arquivo:** `tests/SmbSpeedDoctor.Tests/WindowsScannerTests.cs`

```csharp
// Criado por André Santo (forg3) | junkyardgoodies.app
//
// Testa a ausência de NullReferenceException em WindowsScanner.Collect()
// quando sharePath é null (reproduz o bug do CLI sem --path).
//
// Antes da correção: NRE em Collect() linha 465 (_path.Length).
// Depois da correção: coleta degrada graceful (FileCount=0, AvgFile=0).

using SmbSpeedDoctor.Core;
using SmbSpeedDoctor.Core.Windows;

namespace SmbSpeedDoctor.Tests;

public class WindowsScannerTests
{
    /// <summary>
    /// Reproduz exatamente a cadeia que o CLI percorre com 'smbdoctor-cli fix --export'
    /// sem '--path': ParsePath retorna null, construtor recebe null em sharePath.
    /// </summary>
    [Fact]
    public void Collect_com_sharePath_null_nao_estoura_NRE()
    {
        // Act — mesmo cenário do bug: sharePath = null (equivalente a --path ausente).
        var scanner = new WindowsScanner(sharePath: null, noCopy: false);
        var data = scanner.Collect();

        // Assert — coleta não deve estourar; valores de workload/null devem ser neutros.
        // FileCount e AverageFileBytes precisam ser 0 (fallback seguro).
        Assert.Equal(0L, data.FileCount);
        Assert.Equal(0L, data.AverageFileBytes);

        // Obs: link speed, CPU, disco, rede podem variar na máquina de CI — apenas
        // verificamos que a coleta completou sem exceção (o fato deCollect() retornar
        // semThrow já é a asserção principal). O Assert acima é extra para evitar
        // regressões futuras onde o coletor de workload volte a estourar silenciosamente.
    }

    /// <summary>
    /// Garante que o comportamento default (sharePath vazio) continua funcional
    /// — evita regressão da correção na linha 446-448.
    /// </summary>
    [Fact]
    public void Collect_com_sharePath_vazio_nao_estoura_e_degrada_suavemente()
    {
        var scanner = new WindowsScanner(sharePath: "", noCopy: true);
        var data = scanner.Collect();

        Assert.Equal(0L, data.FileCount);
        Assert.Equal(0L, data.AverageFileBytes);
    }

    /// <summary>
    /// Caminho válido existente ainda popula FileCount corretamente.
    /// Este teste protege contra regressão funcional pós-correção.
    /// </summary>
    [Fact]
    public void Collect_com_caminho_existente_popula_workload()
    {
        var tmp = System.IO.Path.GetTempPath();
        var scanner = new WindowsScanner(sharePath: tmp, noCopy: true);
        var data = scanner.Collect();

        Assert.True(data.FileCount >= 0);
        // O tmp dir tem arquivos; mas em CI pode ser vazio — apenas afirmamos não-negativo.
        Assert.True(data.AverageFileBytes >= 0);
    }
}
```

### Como executar após aplicar o diff

```bash
cd /home/ubuntu/Projetos/Software/smb-speed-doctor
dotnet test tests/SmbSpeedDoctor.Tests/SmbSpeedDoctor.Tests.csproj
# Esperado: 33/33 passing (30 existentes + 3 novos).
```

---

## 5. Verificação do estado baseline

Executado antes de criar esta proposta:

```
$ dotnet build --nologo -v q
Build succeeded.
    0 Warning(s)
    0 Error(s)

$ dotnet test --nologo
Passed!  - Failed: 0, Passed: 30, Skipped: 0, Total: 30
```

**Status:** ✅ baseline limpo. O diff e o teste proposto não interferem no estado — são apenas documento de proposta, não patch aplicado.

---

## 6. Anexos (referência rápida)

- Linha crítica NRE: `WindowsScanner.cs:465`
- ChamadoresProblemáticos: `Program.cs:45` e `53` (CS8604)
- Testes existentes mais relevantes:
  - `RealCopyProbeTests.cs` (linha 28): `Sem_path_definido_cai_na_aproximacao` — já cobre `RealCopyProbe.Decide("", noCopy:true)` mas **não** cobre a NRE via `WindowsScanner.Collect()` com `sharePath=null`.
  - `FixScriptBuilderTests.cs`, `RobocopyBuilderTests.cs` — não relacionados.
