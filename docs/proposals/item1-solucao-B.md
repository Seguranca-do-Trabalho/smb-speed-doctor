# Proposta B — Normalização na fronteira (construtor)

**Data:** 2026-08-22 · **Agente:** ox-alpha (execução interrompida por max_iterations; essência reconstruída do transcript pelo auditor)
**Status:** proposta parcial — a ideia central foi APLICADA como refinamento da Solução A no commit `7e3fdce`.

## Ideia central

Normalizar os parâmetros no construtor do `WindowsScanner`:

```csharp
_target = targetServer ?? "loopback";
_path = sharePath ?? string.Empty;
```

Com isso, todas as referências internas a `_path`/`_target` ficam seguras sem
alteração ponto a ponto. Defesa na fronteira: dados entram válidos ou viram
válidos na porta.

## Riscos identificados

- Null ainda possível se alguém adicionar outro caminho de construção que
  atribua os campos diretamente (mitigado: campos são `readonly`, único
  construtor público).
- Mascara o warning CS8604 do chamador em vez de forçá-lo a corrigir.

## Desdobramento

O auditor adotou a normalização COMBINADA com o null-safety pontual da
Proposta A (cinturão + suspensórios): a fronteira normaliza, e os pontos de
uso críticos (linha 465) também são defensivos. Ver `item1-solucao-A.md` e o
commit `7e3fdce`.
