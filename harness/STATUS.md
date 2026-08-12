# STATUS.md — Sintek.Mail

> Última atualização: 2026-08-12 17:40

---

## Fase atual

**Fase 1 — Fundação (em andamento)**

## Marco atual

**O repositório compila pela primeira vez.** B0 encerrado. Os 5 pacotes com vulnerabilidade conhecida foram eliminados e o job de audit confirma por ferramenta. Os testes executaram pela primeira vez na história do repositório.

O esqueleto das 6 camadas existe e a modelagem de domínio é sólida, mas a fiação entre elas não foi feita: credenciais, OAuth, sanitização de HTML e drenagem da fila offline estão registradas no DI e não têm nenhum consumidor. **Nada funciona de ponta a ponta ainda.**

## Build atual (fonte: CI, não estimativa)

Antes de 12/08 o CI estava vermelho desde 08/08 com 2 erros — não os "54" que este arquivo alegava. Os dois eram o mesmo problema: o `XamlCompiler.exe` do Windows App SDK 1.6 não lida com `net10.0-windows`, morre, e sem ele o `InitializeComponent` não é gerado.

Depois das correções de 12/08: `Sintek.Mail.App` compila (o analisador emite diagnóstico nos ViewModels, o que só acontece se o projeto compilar), `Dependency audit` verde, e o job `Test` roda de fato — antes ele usava `--no-build` acoplado ao build da solution, então nenhum teste jamais executava.

## O que existe

- [x] Especificação (`spec/01`), plano (`spec/02`), parecer (`.analysis/PARECER-VALIDACAO.md`), análise de código (`.analysis/ANALISE-CODIGO.md`)
- [x] Harness de memória (AGENTS.md + harness/)
- [x] Solution `Sintek.Mail.slnx` com 6 projetos src + 8 de teste (4 deles vazios — ver M1)
- [x] **Domain** — 17 enums, 2 VOs, 6 exceções, 20 entidades, 2 serviços. A parte mais sólida do código.
- [x] **Application** — 6 portas, 8 DTOs, 6 handlers. Portas definidas; três delas sem implementação conectada.
- [x] **Persistence** — DbContext, 15 configurations, interceptor, repositório, fila. **Sem migrations** (B8).
- [x] **Infrastructure** — transporte MailKit, sanitizador, 2 providers OAuth. Sanitizador e OAuth não são chamados por ninguém.
- [x] **Infrastructure.Windows** — `CredentialManagerStore` implementado. Nenhum consumidor.
- [x] **App WinUI 3** — janela e 4 ViewModels. Compila. Todos os comandos ainda são TODO.
- [x] **Testes** — 4 projetos com testes reais, 4 stubs vazios. Executam; A9/A10 devem falhar.
- [x] **CI** — build, test, `Dependency audit` (falha em pacote vulnerável), CodeQL e Dependabot.
- [x] **Build** — `Directory.Packages.props` (CPM), `Directory.Build.props` (analisadores), `.editorconfig`.

## Bloqueadores (ordem de ataque)

Detalhe completo em `.analysis/ANALISE-CODIGO.md`.

0. ~~**B0** — Windows App SDK 1.6 contra `net10.0-windows` quebrava o build.~~ **✅ RESOLVIDO em 12/08.** WASDK 2.3.1, `xmlns:dto` no `MainWindow.xaml`, e remoção de `MainPage.*` — que declarava `x:Class="Sintek_Mail_App.MainPage"` com code-behind em `namespace Sintek.Mail.App`, a causa direta do `CS0103`.
1. **B1** — Chave de criptografia do banco sorteada a cada start, nunca persistida (`App.xaml.cs:54`). Perda total de dados a cada reinicialização se o SQLCipher estiver ativo. **É o próximo.**
2. **B2** — ⚠️ **metade feito.** Trocado para `.Sqlite.Core` + `Batteries_V2.Init()`, e o `lib.e_sqlite3` sumiu do grafo. **Falta o teste que prova que o `.db` está cifrado** — sem ele a regressão volta sem ninguém notar, que é exatamente o modo de falha do achado.
3. **B3** — `PRAGMA key` montado por interpolação de string.
4. **B4** — `GetPasswordAsync` retorna `string.Empty`; `ICredentialStore` nunca é injetado. Nenhuma conta autentica.
5. **B5** — `IOAuthProvider` nunca é usado pelo transporte. Pior: `App.xaml.cs:32` chama `AddInfrastructure()` sem argumentos e todos os parâmetros são opcionais, então **nenhum provider chega a ser registrado**. D-006 não está implementado.
6. **B6** — `Sanitize()` nunca é chamado; `WebView2` renderiza HTML cru. O sanitizador de regex é inseguro e o pacote `HtmlSanitizer` já é dependência sem uso.
7. **B7** — `FromAddress` guarda o endereço formatado e `Message.Addresses` nunca é populado no sync: os 5 `ValidationMode` falham para mensagens reais. É o diferencial do produto.
8. **B8** — Não há migrations nem chamada de `Migrate()`. A primeira query lança.

## Próximos passos

1. Ler o resultado dos testes que agora rodam — A9/A10 (asserções esperando `ArgumentException` contra `DomainException`, e `EmailDomain.Parse` sem validação de formato) devem aparecer.
2. Decidir o alvo da Fase 1: fazer **um** caminho funcionar de ponta a ponta (adicionar conta → sync → ler mensagem) em vez de continuar ampliando superfície.
3. Fechar B2 com o teste de criptografia, depois B1, B4, B5, B6, B7, B8.
4. Corrigir A1–A10 (outbox preso em `Processing`, pasta de origem errada no payload de move, herança de restrição não aplicada, duplicação de mensagens no sync).
5. Desduplicar os 8 projetos de teste (M1) e decidir sobre `ISyncQueue` (CA1711/CA1716 silenciados: nome termina em "Queue", parâmetro chamado `error` — renomear porta pública é mudança de contrato).
6. Introduzir logging: **não existe `ILogger` em lugar nenhum** do `src/`, e o CONTEXT.md tem uma restrição de segurança sobre o que os logs podem registrar.
7. Passada de `ConfigureAwait(false)` nas camadas de biblioteca — 42 pontos só na Application (CA2007, hoje warning).

## Bloqueios

- Não é possível compilar neste container: sem SDK .NET e o proxy nega `builds.dotnet.microsoft.com` (403 no CONNECT). Para build local é preciso máquina Windows; para sinal automático, o CI serve.

## Notas

- O `.analysis/PARECER-VALIDACAO.md` §2.3 avisou sobre o conflito de bundles do SQLCipher **antes** da implementação, e o aviso não foi seguido por meses. O log do CI mostrou o bundle errado entrando.
- O catálogo `skills-globais` precisa estar **anexado ao ambiente** para o bootstrap funcionar em container remoto. Sem isso o script registra o erro e sai com 0.
- Lição da rodada de 12/08: três das quatro falhas de CI foram causadas por decisões minhas tomadas "por cautela" (deixar uma versão de pacote como estava, promover `CA2007` a erro). Cautela sem verificar o grafo de dependências é palpite.
