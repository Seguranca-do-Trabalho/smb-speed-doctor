# ADR-0002 — Separação entre domínio puro e coleta dependente de Windows

**Status:** Aceito
**Data:** 2026-08-23
**Supersede:** nada. Complementa o ADR-0001 (stack).

---

## Contexto

O projeto tinha três projetos: `Core`, `Cli` (ambos `net8.0`) e `Gui`
(`net8.0-windows`). O `Core` concentrava tudo: modelo de domínio, motor de
correlação, baseline **e** a coleta via WMI (`System.Management`).

`net8.0` é um alvo multiplataforma. WMI só existe no Windows. O resultado eram
**42 avisos CA1416** ("só há suporte para ... em: 'windows'") em cada build.

O parecer `docs/AUDIT-1.1.0.md` classificou esses avisos como "esperado — o Core
compila multiplataforma por design e os coletores só rodam no Windows com
degradação graciosa. Não é defeito."

Duas coisas estavam erradas nessa avaliação:

1. **A portabilidade prometida não existia.** Um `Core` marcado `net8.0` diz ao
   consumidor "isto roda em qualquer lugar". Rodar `WindowsScanner.Collect()`
   em Linux não degrada graciosamente: lança `PlatformNotSupportedException` na
   primeira chamada WMI. Nunca houve teste demonstrando a tal degradação.
2. **Havia um build Linux no histórico** (`dist_linux/`, removido em `a02b347`),
   ou seja, o artefato multiplataforma chegou a ser produzido — de código que
   quebraria em runtime.

Além disso, 42 avisos recorrentes anestesiam: viram ruído de fundo e escondem
avisos novos e legítimos. Neste mesmo ciclo, os 10 avisos de nulabilidade que
sobraram estavam encobertos por eles.

## Decisão

Separar em dois projetos, pela fronteira de plataforma:

| Projeto | TFM | Conteúdo |
|---|---|---|
| `SmbSpeedDoctor.Core` | `net8.0` | `Domain`, `DiagnosisEngine`, `BaselineStore`, `Collectors` (interfaces + mocks). Sem WMI, sem P/Invoke, sem API de plataforma. |
| `SmbSpeedDoctor.Core.Windows` | `net8.0-windows` | `WindowsScanner` (WMI), `RealCopyProbe`. Referencia `Core`. |

`Cli` passa a `net8.0-windows` (depende da coleta Windows; o `publish` já era
`win-x64` apenas). `Gui` já era `net8.0-windows` e passa a referenciar
`Core.Windows`. `EnableWindowsTargeting=true` mantém a compilação a partir do
Linux; **executar** continua exigindo Windows, o que agora é declarado em vez de
suposto.

`Class1.cs` — arquivo vazio remanescente do template `dotnet new classlib` — foi
removido no mesmo movimento.

## Alternativas consideradas

| Alternativa | Por que não |
|---|---|
| `[SupportedOSPlatform("windows")]` em `WindowsScanner` | Resolve os avisos com duas linhas e é o mecanismo desenhado para isso. Mas mantém código de plataforma dentro do projeto "puro" — a fronteira continua existindo só na cabeça de quem lê. Boa opção de baixo custo; preterida por não abrir caminho para coleta Linux. |
| Mudar o TFM do `Core` para `net8.0-windows` | Uma linha, mas rotula como Windows-only também o domínio puro (que não é), e impede para sempre rodar os testes de domínio fora do Windows. Piora a modelagem para calar um aviso. |
| Manter como estava | Os avisos continuam mascarando avisos novos, e a promessa de portabilidade continua falsa. |

## Consequências

**Positivas**
- 52 avisos de build → **0**.
- A fronteira de plataforma é estrutural, verificada pelo compilador: `Core` não
  compila se alguém introduzir WMI nele.
- O domínio puro fica isolado e é onde vive toda a lógica de decisão — a parte
  que mais precisa de teste e a que menos precisa de Windows.
- Um `SmbSpeedDoctor.Core.Linux` (coleta de Samba) passa a ser adição, não
  refatoração.

**Negativas / dívida assumida**
- O projeto de teste passou a `net8.0-windows` porque cobre também a coleta
  Windows. Na prática os testes só rodam no Windows — o que corresponde ao
  produto de hoje (publish `win-x64`), mas anula parcialmente o ganho de ter o
  domínio portátil.
  **Follow-up registrado:** quando houver CI Linux ou coleta Linux, separar um
  `SmbSpeedDoctor.Tests` (`net8.0`, só domínio) de um
  `SmbSpeedDoctor.Tests.Windows` (`net8.0-windows`). Não feito agora para não
  ampliar o escopo desta rodada.
- `FixScriptBuilder` e `RobocopyBuilder` são puros mas moram no `Cli`, que agora
  é `net8.0-windows`; por tabela, os testes deles também exigem Windows. Moviam
  bem para o `Core` — fica anotado, não feito.

## Verificação

```
dotnet build SmbSpeedDoctor.sln --no-incremental   → 0 avisos, 0 erros
dotnet test  SmbSpeedDoctor.sln                    → 44/44 aprovados
```
