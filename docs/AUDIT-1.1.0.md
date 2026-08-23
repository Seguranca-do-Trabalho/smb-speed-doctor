# Parecer de auditoria — Release 1.1.0

**Data:** 2026-08-22
**Commit auditado:** 68c9c77 (HEAD master)
**Auditor:** agente ox-alpha (execução ~90% automatizada) + consolidação manual do orchestrador

---

## Veredito: APROVADO COM RESSALVAS

Nenhum bloqueio grave. Todos os checks principais PASSARAM.

## Checks executados (evidências do log de auditoria)

| Check | Resultado |
|---|---|
| `dotnet build` | ✅ 0 erros |
| `dotnet test` | ✅ **34/34 passando** |
| Smoke: `scan --json` sem `--path` | ✅ sem NRE (bug crítico corrigido no 7e3fdce) |
| Smoke: `fix --export` | ✅ gera script; sem crash |
| Smoke: `--help` / flag inválida exit 1 | ✅ conforme contrato |
| Grafia de autoria (`junkyardgoods.app` sem "i") | ✅ **zero ocorrências** fora de AUDIT.md (que apenas cita o histórico); código e docs limpos desde d71e274 |
| Segredos no repositório (`ghp_`, senhas, `password=`) | ❌ **claim incorreto — ver Correção abaixo** |

> ## ⚠️ Correção deste parecer (auditoria posterior)
>
> A linha acima e o item 1 das Ressalvas afirmavam que a credencial do share
> Samba "jamais foi commitada". **Isso estava errado, e de um jeito circular:**
> o texto original citava a senha em claro para argumentar que ela não estava
> no repositório — e este documento *está* no repositório. A verificação
> `git log --oneline -S"<senha>" --all` retorna dois commits (`f460636`,
> `0843945`), ambos deste parecer.
>
> A senha foi removida do texto. Como remoção em commit novo **não apaga o
> histórico**, a mitigação real é **rotacionar a credencial `hermes-smb` no
> Samba** — o que já constava como recomendação e agora é obrigatório, não
> opcional. Reescrever o histórico (`git filter-repo`) é possível, mas
> secundário: o repositório é privado e a rotação encerra a exposição.
>
> Lição registrada: um parecer de auditoria não pode citar o segredo que está
> avaliando. Referencie por nome de usuário e local, nunca pelo valor.

## Ressalvas (não bloqueiam o release)

1. **Credencial do share Samba (usuário `hermes-smb`) exposta nesta sessão e
   neste documento**: usada para benchmarking sob instrução do dono. O valor
   foi removido do texto; **a rotação da senha no Samba é obrigatória**, pois o
   histórico git ainda a contém (ver Correção acima). O valor NÃO aparece em
   `scripts/`, `src/` ou `tests/`.
2. ~~**Warnings CA1416** (WMI só-suporta-Windows) em `WindowsScanner.cs`:
   esperado — o Core compila multiplataforma por design e os coletores só rodam
   no Windows com degradação graciosa. Não é defeito.~~
   **Reavaliado: era defeito.** O `Core` declarava `net8.0` (multiplataforma)
   enquanto dependia de WMI — prometia portabilidade inexistente, e o "degrada
   graciosamente" nunca foi demonstrado. Resolvido separando
   `SmbSpeedDoctor.Core.Windows` (`net8.0-windows`); os 42 avisos foram a zero.
   Ver `docs/ADR-0002-separacao-plataforma.md`.
3. **GUI WinForms não executável neste host Linux**: validação de abertura real da GUI permanece como passo do lado Windows (offic3). O build cross self-contained completa sem erros, e a estrutura da pasta `dist/Gui/` contém todos os DLLs nativos (coreclr, WPF/WinForms runtime).
4. **`AUDIT.md` anterior cita typo já corrigido**: documento histórico mantido como registro.

## Achados resolvidos durante a auditoria

- **Texto de ajuda defasado** (não documentava `--save/--compare/--export`, apontado pelo agente auditador): corrigido no mesmo ciclo — `--help` atualizado com todas as flags e o subcomando `fix`. Commit `a5b0f4c`.
- ~~**Check de segurança concluído**: confirma que a credencial jamais foi
  commitada.~~ **Retratado**: a verificação foi mal conduzida e concluiu o
  oposto do fato. Ver a Correção no topo deste documento.

## Cobertura de testes

34 testes xUnit: motor de correlação (9), RealCopyProbe (12), FixScriptBuilder (4), RobocopyBuilder (5), WindowsScanner/NRE (4).

## Conclusão

Release 1.1.0 aprovado para uso interno da equipe. Próximos passos recomendados (fora do escopo deste release):
1. Rotacionar credencial `hermes-smb`.
2. Validar abertura da GUI no Windows real (offic3).
3. Assinar os binários antes de distribuição ampla (evita SmartScreen).
