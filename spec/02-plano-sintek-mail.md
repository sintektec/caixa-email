# Plano — Sintek.Mail: Cliente de E-mail Desktop Windows 11

## Context

O repositório `sintektec/caixa-email` está vazio (apenas o template SINTEK de skills). A especificação anexa define um cliente de e-mail corporativo nativo para Windows 11, estilo Outlook, com um diferencial obrigatório: **organização rigorosa por Diretório de Domínio** (`Domínio → Conta → Pastas`), operação **offline-first** e base local criptografada.

Dois pontos da especificação exigem decisão de engenharia antes de começar:

1. **O documento termina truncado** na entidade `Accounts` (em `LastSyncAt — Date`). Todo o modelo de dados a partir de `Folders`/`Messages` — incluindo o que sustenta as regras de domínio, a fila de sincronização e a busca offline — precisa ser projetado. Este plano o define por completo.
2. **A stack precisou ser corrigida.** A especificação pede ".NET 8 ou superior". Verificado em 03/08/2026 no índice oficial `dotnet/core`: .NET 8 e .NET 9 têm **ambos EOL em 10/11/2026** (~3 meses). Só o **.NET 10 é LTS ativo (EOL 14/11/2028)**. Decidido: .NET 10. O Windows App SDK também mudou de numeração — está em **2.3.x**, não 1.7.

Escopo aprovado: **aplicação completa** (não MVP), incluindo os três modos de autenticação. Esta sessão entrega o **plano + esqueleto completo da solution** com o núcleo de domínio implementado e testado; as fases seguintes preenchem funcionalidade.

Restrição do ambiente: container **Linux sem .NET SDK**. WinUI 3 não compila aqui. A arquitetura abaixo isola deliberadamente o código Windows-only para que ~80% da solution seja compilável e testável neste container; o restante é validado por CI em `windows-latest`.

---

## Decisões (confirmadas com o usuário)

| Item | Decisão |
|---|---|
| Framework | **.NET 10 LTS** (`net10.0`; UI `net10.0-windows10.0.19041.0`) |
| UI | **WinUI 3 / Windows App SDK 2.3.1**, MVVM, Fluent Design |
| Empacotamento | **MSIX + unpackaged (dual)** |
| Namespace/Solution | **`Sintek.Mail`** / `Sintek.Mail.sln` |
| Autenticação | **Todas**: IMAP/SMTP básico + OAuth 2.0 Microsoft 365 + OAuth 2.0 Google + ponto de extensão |
| Verificação | Instalar .NET SDK no container (camadas cross-platform) + CI GitHub Actions Windows (solution inteira) |

### Versões verificadas no nuget.org (03/08/2026)

| Pacote | Versão | Nota |
|---|---|---|
| `Microsoft.WindowsAppSDK` | 2.3.1 | WinUI lib TFM `net6.0-windows10.0.17763.0` → compatível com net10.0-windows |
| `Microsoft.EntityFrameworkCore.Sqlite` / `.Design` | 10.0.10 | |
| `Microsoft.Data.Sqlite.Core` | 10.0.10 | depende de `SQLitePCLRaw.core` **2.1.11** |
| `SQLitePCLRaw.bundle_e_sqlcipher` | 2.1.11 | traz `core` 2.1.11 — **alinhamento exato, sem conflito** |
| `MailKit` / `MimeKit` | 4.17.0 | |
| `CommunityToolkit.Mvvm` | 8.4.2 | |
| `HtmlSanitizer` | 9.1.974 | id do pacote é `HtmlSanitizer`; namespace é `Ganss.Xss` |
| `Microsoft.Identity.Client` + `.Broker` | 4.87.0 | OAuth Microsoft 365 |
| `Google.Apis.Auth` | 1.75.0 | OAuth Gmail |
| `Microsoft.Web.WebView2` | 1.0.4078.44 | render seguro de HTML |
| `Microsoft.Windows.CsWin32` | 0.3.298 | P/Invoke do Credential Manager |
| `Polly` | 8.7.0 | retry da fila de sync |
| `xunit` 2.9.3 / `AwesomeAssertions` 9.5.0 | | **não usar FluentAssertions 8.x** — licença paga para uso comercial; `AwesomeAssertions` é o fork livre |

---

## Arquitetura

Clean Architecture. Regra de dependência: `Domain ← Application ← {Persistence, Infrastructure} ← App`. `Domain` não referencia nada.

```
Sintek.Mail.sln
├─ src/
│  ├─ Sintek.Mail.Domain/                  net10.0    entidades, VOs, enums, regras, exceções
│  ├─ Sintek.Mail.Application/             net10.0    casos de uso, portas (interfaces), DTOs
│  ├─ Sintek.Mail.Persistence/             net10.0    EF Core + SQLite + SQLCipher, migrations, repos
│  ├─ Sintek.Mail.Infrastructure/          net10.0    MailKit, OAuth, sanitização, motor de sync
│  ├─ Sintek.Mail.Infrastructure.Windows/  net10.0-windows10.0.19041.0   Credential Manager, notificações
│  └─ Sintek.Mail.App/                     net10.0-windows10.0.19041.0   WinUI 3, MVVM, MSIX
├─ tests/  Domain.Tests · Application.Tests · Persistence.Tests · Infrastructure.Tests   (net10.0)
├─ docs/   plano, ADRs, modelo de dados
└─ Directory.Build.props · Directory.Packages.props (CPM) · .editorconfig
```

**Por que `Infrastructure.Windows` separado:** isola as APIs Windows-only (Credential Manager, WinRT) para que `Domain`, `Application`, `Persistence` e `Infrastructure` compilem e sejam testados neste container Linux. É o que viabiliza a estratégia de verificação acordada.

---

## Modelo de dados (completo — supre o trecho truncado da especificação)

EF Core 10 + SQLite. Chaves `Guid`, timestamps UTC, exclusão lógica onde a spec exige lixeira/restauração.

**Da especificação (mantidos na íntegra):**
- **Domains** — `Id`, `DomainName`, `Description?`, `ValidationMode`, `InvalidEmailAction`, `AllowSubdomains`, `IsActive`, `CreatedAt`, `UpdatedAt` *(+ `SortOrder`, `IsFavorite`)*
- **Accounts** — `Id`, `DomainId`, `EmailAddress`, `DisplayName`, `ImapHost`, `ImapPort`, `SmtpHost`, `SmtpPort`, `UseSsl`, `AuthenticationType`, `IsActive`, `LastSyncAt` *(+ `ImapSecurity`, `SmtpSecurity`, `CredentialKey`, `OAuthProvider?`, `SyncStatus`, `LastSyncError?`, `SyncIntervalMinutes`, `BodyDownloadPolicy`)*
  → `CredentialKey` guarda **apenas o identificador** no Credential Manager. Nenhuma senha ou token toca o banco.

**Projetados neste plano:**

| Entidade | Papel e campos-chave |
|---|---|
| **DomainAliases** | Domínios adicionais permitidos (spec 5.3): `DomainId`, `DomainName` |
| **Folders** | `AccountId`, `ParentFolderId?`, `Name`, `FolderType` (Inbox/Sent/Drafts/Trash/Junk/Archive/Custom/**Pending**), `RemotePath`, `Delimiter`, `IsFavorite`, `IsDomainRestricted`, `RestrictedToDomainId?`, `UnreadCount`, `TotalCount`, `UidValidity?`, `HighestModSeq?`, `LastSeenUid?`, `SyncEnabled`. Unique `(AccountId, RemotePath)` |
| **Messages** | `AccountId`, `FolderId`, `ThreadId?`, `MessageId` (RFC 5322), `InReplyTo?`, `ReferencesRaw?`, `Uid?`, `ModSeq?`, `Subject`, `SubjectNormalized`, `FromAddress`, `SentAt`, `ReceivedAt`, `Preview`, `Size`, `HasAttachments`, `IsRead`, `IsFlagged`, `Importance`, `IsDraft`, `IsDeleted`, `SyncState`, `ScheduledSendAt?`, `ReadReceiptRequested`. Índices `(FolderId, ReceivedAt DESC)`, `(AccountId, MessageId)`, `(ThreadId)`, `(SyncState)` |
| **MessageAddresses** | **Peça central das regras de domínio.** `MessageId`, `Kind` (From/To/Cc/Bcc/ReplyTo), `Address`, `DisplayName`, `Domain` (persistido já em minúsculas e **indexado**). Permite avaliar `AnyParticipant` em SQL indexado, sem parse de string por mensagem |
| **MessageBodies** | Tabela separada para não inflar a listagem: `MessageId` PK, `HtmlBody?`, `TextBody?`, `SanitizedHtml?`, `HasRemoteContent`, `DownloadedAt` |
| **Attachments** | `MessageId`, `FileName`, `ContentType`, `Size`, `ContentId?`, `IsInline`, `StoragePath?` (arquivo em disco, **não BLOB**), `PartSpecifier`, `IsDownloaded`, `IsSuspicious` |
| **Categories** / **MessageCategories** | Categorias coloridas: `Name`, `ColorHex`, `Shortcut` + tabela de junção |
| **Rules** / **RuleConditions** / **RuleActions** | Regras automáticas (spec 6.5): `Priority`, `MatchType` (All/Any), `StopProcessing`; condições por `Field`/`Operator`/`Value`; ações Move/Copy/Category/MarkRead/Flag/Delete/**MoveToPending**/Forward |
| **OutboxOperations** | **Fila de sincronização** (spec 3.1): `AccountId`, `OperationType`, `EntityId`, `PayloadJson`, `Status`, `AttemptCount`, `NextAttemptAt`, `LastError?`, `Sequence` (ordem determinística), `DependsOnId?`. Índice `(AccountId, Status, NextAttemptAt)` |
| **SavedSearches**, **Signatures**, **MessageTemplates**, **AppSettings** | Spec 6.2 e 6.4 |
| **AuditLog** | `Timestamp`, `EventType`, `EntityType`, `EntityId`, `Description`, `DetailsJson`, `Severity`. **Sem conteúdo sigiloso de mensagem** (spec 3.2) |

**Busca offline (spec 6.4):** tabela virtual **FTS5** `messages_fts` sobre assunto, preview, corpo texto, remetente, destinatários e nomes de anexo. EF Core não gera FTS5 — criar via `migrationBuilder.Sql()` em migration dedicada, com triggers de sincronização.

---

## Regra crítica: Diretórios de Domínio (spec 5)

O coração do produto. Implementado inteiramente em `Sintek.Mail.Domain`, sem dependência de infraestrutura — por isso é testável de forma exaustiva neste container.

- **`EmailAddress` / `EmailDomain` (value objects)** — parse pelo último `@`, `ToLowerInvariant()`, trim, comparação **ordinal exata**. `AllowSubdomains = false` por padrão; quando habilitado, aceita sufixo `.dominio`.
- **`DomainDirectory.ValidateAccount(...)`** — lança `DomainMismatchException`. Testes cobrem **exatamente as tabelas da spec 5.2** (`contato@sintek.com.br` ✅, `admin@gmail.com` ❌, `usuario@vendas.empresa.com` ❌ por padrão).
- **`DomainMembershipEvaluator`** — `ValidationMode`: `SenderOnly`, `RecipientOnly`, `SenderOrRecipient`, `SenderAndRecipient`, `AnyParticipant`; considera também `DomainAliases` e regras explícitas do usuário.
- **`InvalidEmailAction`** — `Block`, `WarnAndConfirm`, `MoveToPending`, `LogOnly`. Mensagem de bloqueio literal da spec: *"Este e-mail não pertence ao domínio configurado para esta pasta e não pode ser adicionado a este local."*
- **Herança (spec 5.4)** — subpastas herdam a restrição do pai; invariante de que **uma pasta nunca se vincula a mais de um Diretório de Domínio** (validação de domínio + índice).
- **Alteração de domínio de um Diretório existente** — `ChangeDomainNameHandler` em duas etapas: *dry-run* revalidando contas e mensagens → relatório de incompatíveis → confirmação explícita → execução com movimentação para pasta de pendências → registro em auditoria.

O mesmo evaluator é o único caminho para mover mensagens, incluindo **drag & drop** (spec 6.3), garantindo que a UI não consiga contornar a regra.

---

## Offline-first e motor de sincronização

- **Toda ação do usuário grava primeiro no SQLite** e enfileira uma `OutboxOperation` na mesma transação. A UI lê sempre do banco local.
- **Drain da fila** com Polly (backoff exponencial + jitter), idempotência por `Sequence`, dependências entre operações via `DependsOnId`.
- **IMAP**: `CONDSTORE`/`QRESYNC` quando o servidor suporta, senão `UID FETCH` incremental; `IDLE` para push com fallback para polling. Mudança de `UidValidity` dispara resync completo da pasta.
- **Conflitos**: last-writer-wins para flags com registro em auditoria; conflito de movimentação é reaplicado e sinalizado.
- **Estados por conta** (`Offline/Online/Syncing/Error`) expostos na barra superior e ao lado de cada conta.

---

## Segurança (spec 3.2)

- **SQLCipher**: `Microsoft.Data.Sqlite` + `SQLitePCLRaw.bundle_e_sqlcipher` com `SQLitePCL.Batteries_V2.Init()`; chave do banco gerada aleatoriamente e guardada no Credential Manager, nunca em arquivo.
- **Credenciais**: `CredWrite`/`CredRead` via **CsWin32** — funciona nos modos packaged **e** unpackaged, coerente com a decisão de empacotamento dual (`PasswordVault` exigiria identidade de pacote).
- **Render de HTML**: `HtmlSanitizer` → **WebView2** com CSP restritiva, DevTools desabilitado, navegação bloqueada, scripts desabilitados, **imagens remotas bloqueadas por padrão** com barra "Exibir imagens"; anexos inline servidos por `cid:` local.
- **Anexos**: lista de extensões perigosas com alerta, e Mark-of-the-Web aplicado ao salvar.
- **Logs**: Serilog com *destructuring* que exclui corpo e assunto de mensagens.
- **Confirmações** obrigatórias antes de remover contas, expurgar mensagens ou limpar dados locais.

---

## Interface (spec 4 e 7)

Shell WinUI 3 com `TreeView` hierárquico (Favoritos / Contas e Diretórios / Domínio → Conta → Pastas), ícones Fluent por nível e contadores de não lidas; painel central com `ListView` virtualizado e agrupamento por conversa; painel de leitura com WebView2 e barra de ações; barra superior com busca, sync manual, indicador de estado, configurações e alternância de tema. Atalhos no padrão Outlook, `AutomationProperties` em todos os controles interativos, temas claro/escuro por `ThemeResource` e layout responsivo.

---

## Entrega desta sessão (PR draft)

1. `docs/` — este plano, ADRs (framework, SQLCipher, empacotamento dual, credenciais) e o modelo de dados.
2. Solution completa: `Directory.Build.props`, `Directory.Packages.props` (Central Package Management), `.editorconfig`, `.gitignore` .NET, `nullable`/`TreatWarningsAsErrors` ativos.
3. **`Domain` implementado por completo** — entidades, VOs, enums, exceções, `DomainMembershipEvaluator`, validação de contas e herança de regras.
4. **`Application`** — portas (repositórios, `IMailTransport`, `ICredentialStore`, `ISyncQueue`, `IHtmlSanitizer`, `IOAuthProvider`), DTOs e handlers dos casos de uso principais.
5. **`Persistence`** — `MailDbContext`, todas as `IEntityTypeConfiguration`, migration inicial, migration do FTS5 e wiring do SQLCipher.
6. **`Infrastructure`** — adaptadores MailKit (IMAP/SMTP), os três provedores de autenticação, sanitizador e esqueleto do motor de sync.
7. **`Infrastructure.Windows`** — Credential Manager via CsWin32.
8. **`App` (WinUI 3)** — csproj dual packaged/unpackaged, `App.xaml`, host de DI, shell com TreeView, ViewModels principais.
9. **Testes** — cobertura exaustiva das tabelas de validação da spec 5.2/5.3, casos de uso e persistência (SQLite em arquivo temporário).
10. **CI** — GitHub Actions: `ubuntu-latest` (build + test cross-platform) e `windows-latest` (solution inteira + MSIX).

**Fases seguintes** (aplicação completa, conforme acordado): F2 contas e autodiscovery · F3 sync IMAP incremental e fila offline · F4 leitura e composição · F5 pastas, drag & drop e regras de domínio na UI · F6 busca FTS5 e pesquisas salvas · F7 regras automáticas, categorias, assinaturas e modelos · F8 agendamento, confirmação de leitura e acessibilidade · F9 empacotamento, assinatura e instalador.

---

## Verificação

1. **Instalar o SDK**: `dotnet-install.sh --channel 10.0` (não há .NET no container).
2. **Neste container (Linux)**: `dotnet build` e `dotnet test` de `Domain`, `Application`, `Persistence` e `Infrastructure` — inclui a criação real do banco SQLCipher e a aplicação das migrations em arquivo temporário, provando que EF Core 10 + `bundle_e_sqlcipher` 2.1.11 funcionam juntos.
3. **CI Windows**: build da solution completa, incluindo `Infrastructure.Windows` e o app WinUI 3, e geração do MSIX. É a única forma de validar a camada de UI — WinUI 3 não compila em Linux.
4. **Manual (máquina Windows 11)**: instalar o MSIX, criar o Diretório `sintek.com.br`, tentar vincular `admin@gmail.com` (deve bloquear com a mensagem da spec), vincular `contato@sintek.com.br` (deve permitir), sincronizar, cortar a rede, executar ações offline e confirmar que a fila drena ao reconectar.
