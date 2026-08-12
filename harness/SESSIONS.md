# SESSIONS.md — Sintek.Mail

> Registro de sessões de trabalho. Append-only.

---

## 2026-08-03 18:20 — Sessão 3: Implementação completa + Build falhou

**Objetivo:** Implementar todas as camadas + testes + CI.

**O que foi feito:**
- Implementado Domain completo (17 enums, 2 VOs, 6 exceções, 20 entidades, 2 serviços)
- Implementado Application completo (6 portas, 8 DTOs, 6 handlers)
- Implementado Persistence completo (DbContext, 15 configurations, SqlCipherInterceptor, MailRepository, SyncQueue, DI)
- Implementado Infrastructure completo (MailKitTransport, HtmlSanitizerService, MsalOAuthProvider, GoogleOAuthProvider, DI)
- Implementado Infrastructure.Windows completo (CredentialManagerStore com P/Invoke, DI)
- Implementado App WinUI 3 completo (App.xaml, MainWindow, 5 ViewModels, csproj)
- Implementado testes (DomainTests, ApplicationTests, PersistenceTests, InfrastructureTests)
- Implementado CI (.github/workflows/ci.yml)
- Adicionado projetos de teste à solution
- Build falhou com 54 erros de compilação

**Erros identificados:**
1. Testes usam construtores/métodos incorretos (DomainMembershipEvaluator, FolderDomainValidator, handlers)
2. Falta package Microsoft.Extensions.DependencyInjection em Infrastructure e Infrastructure.Windows
3. Ambiguidade IMailTransport entre MailKit e Application.Ports
4. DbConnectionEventData não encontrado em SqlCipherInterceptor

**Próxima sessão:**
- Corrigir 54 erros de compilação
- Build e validação (dotnet build, dotnet test)
- Commit e push

**Contexto:** 88% usado. Estado salvo em STATUS.md.

---

## 2026-08-03 17:45 — Sessão 2: Harness de memória + .NET SDK 10

**Objetivo:** Criar harness de memória e instalar .NET SDK 10.

**O que foi feito:**
- Criado AGENTS.md com diretrizes de comportamento
- Criado harness/ com STATUS.md, CONTEXT.md, SESSIONS.md, DECISIONS.md, GLOSSARY.md, README.md
- Instalado .NET SDK 10.0.100-preview.5
- Criado esqueleto da solution Sintek.Mail.sln com 6 projetos src + 4 testes

**Próxima sessão:**
- Implementar Domain, Application, Persistence, Infrastructure, Infrastructure.Windows, App

---

## 2026-08-03 16:30 — Sessão 1: Especificação + Plano

**Objetivo:** Criar especificação unificada e plano de implementação.

**O que foi feito:**
- Criado spec/01-especificacao-unificada.md (consolidação de 3 documentos)
- Criado spec/02-plano-sintek-mail.md (plano de implementação em fases)
- Commit e push para GitHub

**Próxima sessão:**
- Criar harness de memória
- Instalar .NET SDK 10

---

## 2026-08-03 19:46 — Sessão 4: Dúvida externa

**Objetivo:** Responder dúvida do usuário sobre erro 402 no Cline (kimi-k3).

**O que foi feito:**
- Identificado que o erro 402 é "Payment Required" (falta de saldo no OpenRouter/provedor).
- O projeto `Sintek.Mail` não foi alterado.

**Próxima sessão:**
- Retomar a Sessão 3: Corrigir 54 erros de compilação.

---

## 2026-08-03 20:03 — Sessão 5: Dúvida externa

**Objetivo:** Responder dúvida do usuário sobre encerramento de conta NVIDIA.

**O que foi feito:**
- Respondido que a conta pode ser encerrada tanto voluntariamente quanto por violação de termos.
- Nenhuma alteração no código do projeto.

**Próxima sessão:**
- Retomar a Sessão 3: Corrigir 54 erros de compilação.

---

## 2026-08-04 10:04 — Sessão 6: Sync com GitHub

**Objetivo:** Sincronizar código local com repositório remoto.

**O que foi feito:**
- Feito `git add .`, commit e `git push` de todas as alterações pendentes (incluindo as atualizações de harness recentes).

**Próxima sessão:**
- Retomar a Sessão 3: Corrigir 54 erros de compilação da Solution.

---

## 2026-08-11 21:30 — Sessão 7: Análise de código

**Objetivo:** Analisar o código de `src/` e `tests/` (pedido: "analise o codigo").

**O que foi feito:**
- Leitura estática completa das 6 camadas de `src/` e dos 8 projetos de `tests/`.
- Produzido `.analysis/ANALISE-CODIGO.md`: 8 bloqueadores, 11 bugs de correção, 11 itens de dívida, 3 de processo.
- Corrigido o `STATUS.md`, que estava desatualizado: os "54 erros de compilação" não existem mais (3 das 4 causas listadas já estavam corrigidas no código), e as camadas marcadas como "completas" têm portas registradas no DI sem nenhum consumidor.
- **Não foi possível compilar:** o container não tem SDK .NET e o proxy nega `builds.dotnet.microsoft.com` (403 no CONNECT). A análise é estática.

**Principais achados (detalhe em `.analysis/ANALISE-CODIGO.md`):**
- B1: chave de criptografia do banco sorteada a cada start e nunca persistida (`App.xaml.cs:54`).
- B2: `EntityFrameworkCore.Sqlite` + `bundle_e_sqlcipher` = dois bundles; `PRAGMA key` pode virar no-op silencioso. O `PARECER-VALIDACAO.md` §2.3 já tinha avisado.
- B4/B5: `ICredentialStore` e `IOAuthProvider` registrados no DI e injetados em lugar nenhum; `GetPasswordAsync` devolve string vazia — nenhuma conta autentica.
- B6: `Sanitize()` nunca é chamado e o `WebView2` renderiza HTML cru; o sanitizador de regex é inseguro e o pacote `HtmlSanitizer` já é dependência sem uso.
- B7: `FromAddress` guarda `"Nome" <a@b.com>` e `Message.Addresses` nunca é populado no sync — os 5 `ValidationMode` falham para mensagens reais.
- B8: não existem migrations nem chamada de `Migrate()`.
- A2: a fila do outbox nunca é drenada e fica presa em `Processing` para sempre.
- P2: o hook `SessionStart` está duplicado no `settings.json`; as duas instâncias competem pelo mesmo clone e o script não limpa clone parcial — é por isso que `.claude/skills/` está vazio.

**Próxima sessão:**
- Atacar B1–B8 na ordem. Antes disso, decidir se o alvo é fazer a Fase 1 funcionar de ponta a ponta (uma conta, um sync, uma leitura) ou continuar ampliando superfície.

**Correção durante a sessão (build real do CI):**
Após a primeira versão da análise, recuperei o log do CI (run 31237494762, `main`, 08/08) e corrigi dois achados:
- O build falha com **2 erros, não 54**, e nenhuma das 4 causas listadas no STATUS antigo. A causa real é o Windows App SDK **1.6** contra `net10.0-windows`: o `XamlCompiler.exe` morre e o `InitializeComponent` de `MainPage.xaml.cs` deixa de existir. Promovido a **B0** — é o único item entre o repositório e um build verde. Os outros 5 projetos de `src/` e os 8 de `tests/` compilam.
- **P3 estava errado.** O CI não é inexistente: são 76 execuções, ele roda em PR, e está vermelho em `main` desde 08/08 sem que ninguém agisse. Reescrito.
- Ganho: o log prova B2. `SQLitePCLRaw.lib.e_sqlite3 2.1.11` (bundle **sem** criptografia, e com vulnerabilidade de severidade alta) está sendo restaurado em `Persistence` — o conflito de bundles não é hipótese.
- Novo em M2: 5 pacotes com vulnerabilidade conhecida no log (MailKit, MimeKit, HtmlSanitizer, AngleSharp moderadas; `lib.e_sqlite3` alta).

**Atualização 23:25 — P2 corrigido em `main`:**
`main` avançou para `105819a` ("Update the skills bootstrap: single instance, self-healing clone"), que fecha os quatro pontos de P2: hook `SessionStart` único, lock por `mkdir` atômico com quebra de órfão por vivacidade do dono, clone em caminho temporário com `clone_ok()` validando o resultado, e log append-only com rotação. A correção vai além do recomendado: `clone_ok()` exige que `git rev-parse --show-toplevel` seja exatamente o clone, evitando que a descoberta de repositório suba na árvore e o `git pull` acabe atualizando o repositório do usuário.

`main` continua vermelha — a mudança é ortogonal a B0 (Windows App SDK 1.6 vs `net10.0-windows`), que segue de pé. Merge de `main` no branch feito; análise e STATUS atualizados.

---

## 2026-08-12 17:30 — Sessão 8: Skills de varredura e correções de infraestrutura

**Objetivo:** listar e executar as skills de verificação/segurança/performance/estruturação sobre o projeto.

**Desbloqueio:** o catálogo `skills-globais` nunca esteve acessível neste ambiente — o bootstrap falhava porque o repositório não estava anexado. Anexado via `add_repo`, clonado em `/workspace/skills-globais` (1.369 skills) e 11 instaladas em `.claude/skills/`.

**Diagnóstico (claude-doctor):** maturidade **Growing**, score **24%** — 6 ✅ / 2 ⚠️ / 17 ❌ em 25 checks aplicáveis. As falhas são de infraestrutura e processo, distintas dos defeitos de código B0–B8 já mapeados.

**Achados novos desta rodada:**
- Não existe logging: zero `ILogger` em todo o `src/`. A restrição do CONTEXT.md ("logs nunca registram conteúdo sigiloso") é vacuamente satisfeita porque não há log.
- `App.xaml.cs:32` chama `AddInfrastructure()` sem argumentos e todos os parâmetros são opcionais — **nenhum `IOAuthProvider` chega a ser registrado**. Agrava o B5.
- Três `catch` sem filtro que retornam `false` (`MailKitTransport.cs:25`, `MsalOAuthProvider.cs:85`, `GoogleOAuthProvider.cs:81`).
- `MainPage.xaml` declarava `x:Class="Sintek_Mail_App.MainPage"` com code-behind em `namespace Sintek.Mail.App` — namespaces diferentes. Era a origem direta do `CS0103`, mais precisa que "WASDK velho".
- `README.md` descreve o template SINTEK, não o Sintek.Mail.
- Sem `CLAUDE.md`, sem `.editorconfig`, sem analisadores, sem lock file, sem Dependabot.
- `SecurityProtocol.None` permite IMAP/SMTP em texto claro sem trava nem aviso.

**Correções aplicadas (3 commits):**
1. `d6d76f1` — CI: job de audit que falha em pacote vulnerável, CodeQL (csharp, security-extended), Dependabot, e **teste promovido a job próprio** (antes o `--no-build` acoplava os testes ao build da solution, então nenhum teste jamais executou neste repositório).
2. `429fcd5` — `Directory.Packages.props` com as versões do parecer. Elimina os 5 CVEs, troca `EntityFrameworkCore.Sqlite` por `.Sqlite.Core` (+ `Batteries_V2.Init()`), sobe WASDK para 2.3.1, corrige o `xmlns` do `MainWindow.xaml` e remove os restos de template.
3. `01c6714` — `.editorconfig` + `Directory.Build.props` com analisadores e promoção seletiva de aviso a erro.

**Correção do próprio diagnóstico:** o check C0-9 (scan de secrets) foi marcado ❌, mas o repositório tem **GitGuardian** rodando em PR — é ⚠️ parcial, não ausente. O que falta de fato é o hook local (pre-commit/gitleaks).

**Próxima sessão:**
- Confirmar o CI verde. Se estiver, B0 encerrado e os testes rodam pela primeira vez — aí A9/A10 finalmente dão sinal.
- B2 não está encerrado: falta o teste que prova que o arquivo `.db` está cifrado.
- Seguir para B1, B4, B5, B6, B7, B8.
