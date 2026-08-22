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
| Segredos no repositório (`ghp_`, senhas, `password=`) | ✅ zero ocorrências no histórico git |

## Ressalvas (não bloqueiam o release)

1. **Senha do share Samba em texto plano nos logs desta sessão** (`[SENHA REMOVIDA DO HISTORICO]`, usuário `hermes-smb`): usada para benchmarking sob instrução do dono, mas NÃO está em nenhum arquivo do repositório (verificado). Recomendação pós-release: rotacionar a senha do usuário `hermes-smb` no Samba.
2. **Warnings CA1416** (WMI só-suporta-Windows) em `WindowsScanner.cs`: esperado — o Core compila multiplataforma por design e os coletores só rodam no Windows com degradação graciosa. Não é defeito.
3. **GUI WinForms não executável neste host Linux**: validação de abertura real da GUI permanece como passo do lado Windows (offic3). O build cross self-contained completa sem erros, e a estrutura da pasta `dist/Gui/` contém todos os DLLs nativos (coreclr, WPF/WinForms runtime).
4. **`AUDIT.md` anterior cita typo já corrigido**: documento histórico mantido como registro.

## Cobertura de testes

34 testes xUnit: motor de correlação (9), RealCopyProbe (12), FixScriptBuilder (4), RobocopyBuilder (5), WindowsScanner/NRE (4).

## Conclusão

Release 1.1.0 aprovado para uso interno da equipe. Próximos passos recomendados (fora do escopo deste release):
1. Rotacionar credencial `hermes-smb`.
2. Validar abertura da GUI no Windows real (offic3).
3. Assinar os binários antes de distribuição ampla (evita SmartScreen).
