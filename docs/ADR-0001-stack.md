namespace SmbSpeedDoctor.Core;

/// <summary>
/// ADR-0001 — Stack e estrutura do produto
/// Data: 2026-08-22
/// Status: Proposto
///
## Contexto
- Decisão de arquitetura pendente nos documentos: "Stack: .NET vs Rust/Tauri".
- CANAIS.md define: dois executáveis do mesmo código (GUI + CLI).
- O produto é um EXE assinado, não um script PowerShell.
- A lógica de diagnóstico deve ser testável sem Windows (testes unitários
  no Linux durante o desenvolvimento).

## Decisão
1. **Stack**: C# / .NET 8, biblioteca de classes pura (Core) + dois
   executáveis (Cli, Gui), ambos targeting win-x64.
2. **Motivo**: acesso nativo a WMI, Win32 API, SMB cmdlets e WinForms
   sem dependência de runtime adicional; cross-compile a partir do Linux.
3. **Consequências**:
   - Motor de diagnóstico isolado em Core (interfaces + mocks) → 100%
     testável em CI Linux.
   - CLI (`smbdoctor-cli.exe`) com `--json` e exit codes para RMM.
   - GUI (`smbdoctor.exe`) fina — um botão, uma lista — desenhada a partir
     da mesma saída do CLI.
   - Wrapper PowerShell MIT separado para scripts de RMM.
   - Build via `dotnet publish -r win-x64`.

## Alternativas rejeitadas
- **Rust/Tauri**: melhor tamanho de binário e startup, mas perde-se a
  facilidade de acesso a WMI/Win32 e o ecossistema de segurança/certificação
  de código da Microsoft. Sem necessidade real de performance crítica que
  justifique a troca.
- **Script PowerShell puro**: não passa em marketplaces, não tem UI, não
  escala como produto independente (regra de CANAIS.md § *Formato do produto*).

## Notas
- Cross-compile funciona no Linux (verificar compatibilidade de WMI via
  Microsoft.NETCore.NETCoreApp/ref/...). No Windows, uso direto de
  System.Management / Win32 API.