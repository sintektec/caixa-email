# STATUS.md — Sintek.Mail

> Última atualização: 2026-08-11 21:30

---

## Fase atual

**Fase 1 — Fundação (em andamento)**

## Marco atual

Análise de código concluída (`.analysis/ANALISE-CODIGO.md`). O esqueleto das 6 camadas existe e a modelagem de domínio está sólida, mas a fiação entre as camadas não foi feita: credenciais, OAuth, sanitização de HTML e drenagem da fila offline estão registradas no DI e não têm nenhum consumidor. **Nada funciona de ponta a ponta ainda.**

## Correção do status anterior

O STATUS.md anterior afirmava "Build falhou com 54 erros" e listava quatro causas. **O build real do CI mostra 2 erros, não 54**, e as quatro causas listadas não são nenhuma delas. Três já estavam corrigidas no código (`Microsoft.Extensions.DependencyInjection` presente nos dois csproj, `SqlCipherInterceptor` usando `ConnectionEndEventData`, ambiguidade de `IMailTransport` resolvida por qualificação completa); a quarta ("testes usam APIs incorretas") é real, mas são falhas de asserção, não erros de compilação — e nunca chegaram a rodar.

O mesmo documento marcava todas as camadas como "completo". Isso não se sustenta: ver os bloqueadores abaixo.

## Build atual (fonte: CI, não estimativa)

Último build de `main` — run [31237494762](https://github.com/sintektec/caixa-email/actions/runs/31237494762), 08/08/2026 — **FAILED, 2 erros e 21 warnings**:

```
error CS0103: The name 'InitializeComponent' does not exist in the current context
    src/Sintek.Mail.App/MainPage.xaml.cs(12,9)
error MSB3073: ...microsoft.windowsappsdk\1.6.240923002\...\XamlCompiler.exe ... exited with code 1
```

Os dois são o mesmo problema: o compilador de XAML do Windows App SDK 1.6 não lida com `net10.0-windows` e morre; sem ele, `InitializeComponent` não é gerado. **Todos os outros 5 projetos de `src/` e os 8 de `tests/` compilam.** O passo `dotnet test` usa `--no-build` e nunca executa — nenhum teste deste repositório jamais rodou no CI.

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

0. **B0** — O build quebra: Windows App SDK **1.6** contra `net10.0-windows`. D-001 e o parecer registraram **2.3.1**. Corrigir junto: `xmlns:local` ausente em `MainWindow.xaml` e os restos de template `MainPage.*`.
1. **B1** — Chave de criptografia do banco sorteada a cada start, nunca persistida (`App.xaml.cs:54`). Perda total de dados a cada reinicialização se o SQLCipher estiver ativo.
2. **B2** — `EntityFrameworkCore.Sqlite` traz `bundle_e_sqlite3` junto com `bundle_e_sqlcipher`; o `PRAGMA key` pode virar no-op silencioso e o banco ficar em texto plano. **O log do CI confirma:** `SQLitePCLRaw.lib.e_sqlite3 2.1.11` está sendo restaurado em `Persistence` (e ainda tem vulnerabilidade de severidade alta). Trocar por `.Sqlite.Core` + `Batteries_V2.Init()` + teste que verifique.
3. **B3** — `PRAGMA key` montado por interpolação de string.
4. **B4** — `GetPasswordAsync` retorna `string.Empty`; `ICredentialStore` nunca é injetado. Nenhuma conta autentica.
5. **B5** — `IOAuthProvider` nunca é usado pelo transporte, e os dois registros no DI se sobrescrevem. D-006 não está implementado.
6. **B6** — `Sanitize()` nunca é chamado; `WebView2` renderiza HTML cru. O sanitizador de regex é inseguro e o pacote `HtmlSanitizer` já é dependência sem uso.
7. **B7** — `FromAddress` guarda o endereço formatado e `Message.Addresses` nunca é populado no sync: os 5 `ValidationMode` falham para mensagens reais. É o diferencial do produto.
8. **B8** — Não há migrations nem chamada de `Migrate()`. A primeira query lança.

## Próximos passos

1. **B0 primeiro** — é o único item que separa o repositório de um build verde, e é pequeno: subir o Windows App SDK para 2.3.1, declarar `xmlns:local` em `MainWindow.xaml`, apagar `MainPage.*` e `MainPageViewModel`. Com o build passando, o `dotnet test` finalmente roda e dá o primeiro sinal real sobre A9/A10.
2. Decidir o alvo da Fase 1: fazer **um** caminho funcionar de ponta a ponta (adicionar conta → sync → ler mensagem) em vez de continuar ampliando superfície.
3. Corrigir B1–B8 na ordem acima.
4. Corrigir A1–A10 (outbox preso em `Processing`, pasta de origem errada no payload de move, herança de restrição não aplicada, duplicação de mensagens no sync).
5. Desduplicar os projetos de teste e alinhar versões de pacote com o `PARECER-VALIDACAO.md` — 5 pacotes têm vulnerabilidade conhecida, 1 de severidade alta (M1, M2).
6. Consertar o hook de bootstrap de skills (P2) e ajustar o `ci.yml` (P3).

## Bloqueios

- Não é possível compilar neste container: sem SDK .NET e o proxy nega `builds.dotnet.microsoft.com` (403 no CONNECT). Para build local é preciso máquina Windows; para sinal automático, o CI já serve.
- `.claude/skills/` está vazio: o hook `SessionStart` está duplicado no `settings.json` e as duas instâncias competem pelo mesmo clone (P2).

## Notas

- O `.analysis/PARECER-VALIDACAO.md` §2.3 avisou sobre o conflito de bundles do SQLCipher (B2) **antes** da implementação, e o aviso não foi seguido. O log do CI mostra o bundle errado entrando.
- As versões de pacote no código divergem de todas as versões verificadas no plano (M2), e o Windows App SDK 1.6 é o que quebra o build. Não há `Directory.Packages.props`.
- O CI está vermelho em `main` desde 08/08 e nenhum commit desde então tentou corrigir. O sinal existia; foi ignorado.
