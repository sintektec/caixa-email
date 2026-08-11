# STATUS.md — Sintek.Mail

> Última atualização: 2026-08-11 21:30

---

## Fase atual

**Fase 1 — Fundação (em andamento)**

## Marco atual

Análise de código concluída (`.analysis/ANALISE-CODIGO.md`). O esqueleto das 6 camadas existe e a modelagem de domínio está sólida, mas a fiação entre as camadas não foi feita: credenciais, OAuth, sanitização de HTML e drenagem da fila offline estão registradas no DI e não têm nenhum consumidor. **Nada funciona de ponta a ponta ainda.**

## Correção do status anterior

O STATUS.md anterior afirmava "Build falhou com 54 erros" e listava quatro causas. Três já estavam corrigidas no código: o pacote `Microsoft.Extensions.DependencyInjection` está presente nos dois csproj citados, `SqlCipherInterceptor` usa `ConnectionEndEventData` (tipo correto), e a ambiguidade de `IMailTransport` está resolvida por qualificação completa. A quarta ("testes usam APIs incorretas") é real, mas são falhas de asserção, não erros de compilação — ver A9/A10 na análise.

O mesmo documento marcava todas as camadas como "completo". Isso não se sustenta: ver os bloqueadores abaixo.

## O que existe

- [x] Especificação (`spec/01`), plano (`spec/02`), parecer de validação (`.analysis/PARECER-VALIDACAO.md`)
- [x] Harness de memória (AGENTS.md + harness/)
- [x] Solution `Sintek.Mail.slnx` com 6 projetos src + 8 de teste (4 deles vazios — ver M1)
- [x] **Domain** — 17 enums, 2 VOs, 6 exceções, 20 entidades, 2 serviços. A parte mais sólida do código.
- [x] **Application** — 6 portas, 8 DTOs, 6 handlers. Portas definidas; três delas sem implementação conectada.
- [x] **Persistence** — DbContext, 15 configurations, interceptor, repositório, fila. **Sem migrations** (B8).
- [x] **Infrastructure** — transporte MailKit, sanitizador, 2 providers OAuth. Sanitizador e OAuth não são chamados por ninguém.
- [x] **Infrastructure.Windows** — `CredentialManagerStore` implementado. Nenhum consumidor.
- [x] **App WinUI 3** — janela, 5 ViewModels. Todos os comandos são TODO; XAML não compila (A11).
- [x] **Testes** — 4 projetos com testes reais, 4 stubs vazios. Alguns testes falham (A9/A10).
- [x] **CI** — `.github/workflows/ci.yml`. Nunca executou: só dispara em `main` e não há PR (P3).

## Bloqueadores (ordem de ataque)

Detalhe completo em `.analysis/ANALISE-CODIGO.md`.

1. **B1** — Chave de criptografia do banco sorteada a cada start, nunca persistida (`App.xaml.cs:54`). Perda total de dados a cada reinicialização se o SQLCipher estiver ativo.
2. **B2** — `EntityFrameworkCore.Sqlite` traz `bundle_e_sqlite3` junto com `bundle_e_sqlcipher`; o `PRAGMA key` pode virar no-op silencioso e o banco ficar em texto plano. Trocar por `.Sqlite.Core` + `Batteries_V2.Init()` + teste que verifique.
3. **B3** — `PRAGMA key` montado por interpolação de string.
4. **B4** — `GetPasswordAsync` retorna `string.Empty`; `ICredentialStore` nunca é injetado. Nenhuma conta autentica.
5. **B5** — `IOAuthProvider` nunca é usado pelo transporte, e os dois registros no DI se sobrescrevem. D-006 não está implementado.
6. **B6** — `Sanitize()` nunca é chamado; `WebView2` renderiza HTML cru. O sanitizador de regex é inseguro e o pacote `HtmlSanitizer` já é dependência sem uso.
7. **B7** — `FromAddress` guarda o endereço formatado e `Message.Addresses` nunca é populado no sync: os 5 `ValidationMode` falham para mensagens reais. É o diferencial do produto.
8. **B8** — Não há migrations nem chamada de `Migrate()`. A primeira query lança.

## Próximos passos

1. Decidir o alvo da Fase 1: fazer **um** caminho funcionar de ponta a ponta (adicionar conta → sync → ler mensagem) em vez de continuar ampliando superfície.
2. Corrigir B1–B8 na ordem acima.
3. Corrigir A1–A11 (bugs de correção: outbox preso em `Processing`, pasta de origem errada no payload de move, herança de restrição não aplicada, duplicação de mensagens no sync).
4. Desduplicar os projetos de teste e alinhar versões de pacote com o `PARECER-VALIDACAO.md` (M1, M2).
5. Consertar o hook de bootstrap de skills (P2) e habilitar o CI em branches de feature (P3).

## Bloqueios

- Não é possível compilar neste container: sem SDK .NET e o proxy nega `builds.dotnet.microsoft.com` (403 no CONNECT). Build e teste dependem de máquina Windows local ou do CI.
- `.claude/skills/` está vazio: o hook `SessionStart` está duplicado no `settings.json` e as duas instâncias competem pelo mesmo clone (P2).

## Notas

- O `.analysis/PARECER-VALIDACAO.md` §2.3 avisou sobre o conflito de bundles do SQLCipher (B2) **antes** da implementação, e o aviso não foi seguido.
- As versões de pacote no código divergem de todas as versões verificadas no plano (M2). Não há `Directory.Packages.props`.
