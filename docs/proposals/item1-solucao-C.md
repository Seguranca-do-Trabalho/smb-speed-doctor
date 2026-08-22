# Proposta C — Contrato explícito com guard clauses

**Data:** 2026-08-22 · **Agente:** ox-alpha (execução interrompida por max_iterations; essência reconstruída do transcript pelo auditor)
**Status:** proposta parcial — REJEITADA pelo auditor, com justificativa.

## Ideia central

Tornar o contrato impossível de violar:

1. Guard clauses no construtor do `WindowsScanner` lançando
   `ArgumentException` se `sharePath` for null (null não é aceitável —
   `string.Empty` é).
2. `Program.cs` normaliza `path=null` → `string.Empty` antes de construir o
   scanner.
3. Falhe rápido e alto na fronteira; nunca aceite estado inválido
   silenciosamente.

## Por que foi rejeitada

O produto tem requisito explícito de **degradação graciosa**: qualquer coleta
que falhe registra o erro e continua com valor neutro (`CollectionErrors`).
Lançar exceção no construtor por um parâmetro que tem default válido
(`sharePath = ""`) transformaria um cenário perfeitamente operável (scan sem
`--path`) em erro de execução — regressão de comportamento para o caso mais
comum do RMM (`scan --json` sem alvo).

A normalização silenciosa (Proposta B) atinge o mesmo rigor de contrato sem o
custo da exceção, e foi adotada no commit `7e3fdce`.

## Observação aproveitada da análise do agente C

O agente C corretamente apontou o campo temporário `trace = ex.StackTrace` no
JSON de erro (adicionado durante o debug da NRE) como risco de vazamento em
logs RMM. Essa observação virou a **Pendência 2**, resolvida no commit
`ad30e11` (trace só no stderr).
