# STATUS.md — Sintek.Mail

> Última atualização: 2026-08-03 18:20

---

## Fase atual

**Fase 1 — Fundação (em andamento)**

## Marco atual

Build falhou com 54 erros. Corrigindo erros de compilação.

## O que foi feito

- [x] Especificação unificada (`spec/01-especificacao-unificada.md`)
- [x] Plano de implementação (`spec/02-plano-sintek-mail.md`)
- [x] Harness de memória (AGENTS.md + harness/)
- [x] Solution `Sintek.Mail.sln` com 6 projetos src + 4 testes
- [x] **Domain** — completo:
  - 17 enums (ValidationMode, InvalidEmailAction, AuthenticationType, FolderType, SyncState, Importance, AddressKind, AccountSyncStatus, BodyDownloadPolicy, OAuthProvider, SecurityProtocol, OutboxOperationType, OutboxOperationStatus, RuleMatchType, RuleActionType)
  - 2 VOs (EmailAddress, EmailDomain) com parsing, normalização, subdomain matching
  - 6 exceções (DomainException, InvalidEmailAddressException, InvalidEmailDomainException, DomainMismatchException, MessageDomainViolationException, FolderAlreadyRestrictedException)
  - 20 entidades (Entity base, DomainDirectory, DomainAlias, Account, Folder, Message, MessageAddress, MessageBody, Attachment, Category, MessageCategory, Rule, RuleCondition, RuleAction, OutboxOperation, SavedSearch, Signature, MessageTemplate, AppSettings, AuditLog)
  - 2 serviços de domínio (DomainMembershipEvaluator, FolderDomainValidator)
- [x] **Application** — completo:
  - 6 portas (IMailRepository, IMailTransport, ICredentialStore, ISyncQueue, IHtmlSanitizer, IOAuthProvider)
  - 8 DTOs (AccountDto, DomainDirectoryDto, FolderDto, MessageDto, MessageAddressDto, MessageBodyDto, AttachmentDto, OutboxOperationDto)
  - 6 handlers (CreateDomainDirectoryHandler, AddAccountHandler, MoveMessageHandler, ChangeDomainNameHandler, SendMessageHandler, SyncAccountHandler)
- [x] **Persistence** — completo:
  - MailDbContext com 19 DbSets
  - 15 configurations (DomainDirectory, Account, Folder, Message, MessageAddress, OutboxOperation, + 9 em RemainingConfigurations)
  - SqlCipherInterceptor (PRAGMA key)
  - MailRepository (implementa IMailRepository)
  - SyncQueue (implementa ISyncQueue com exponential backoff)
  - DependencyInjection (AddPersistence)
- [x] **Infrastructure** — completo:
  - MailKitTransport (IMAP/SMTP com MailKit, fetch folders/messages, send, move, delete, flags)
  - HtmlSanitizerService (remove tags/attrs perigosos, detecta remote content, extrai texto)
  - MsalOAuthProvider (Microsoft OAuth com MSAL.NET)
  - GoogleOAuthProvider (Google OAuth com Google.Apis.Auth)
  - DependencyInjection (AddInfrastructure)
- [x] **Infrastructure.Windows** — completo:
  - CredentialManagerStore (P/Invoke CredWrite/CredRead/CredDelete/CredFree)
  - DependencyInjection (AddWindowsInfrastructure)
- [x] **App WinUI 3** — completo:
  - App.xaml + App.xaml.cs (DI com ServiceCollection, GetOrCreateEncryptionKey)
  - MainWindow.xaml + MainWindow.xaml.cs (3 colunas: Domains, Messages, ReadingPane)
  - 5 ViewModels (MainViewModel, DomainListViewModel, AccountListViewModel, MessageListViewModel, ComposeViewModel)
  - Sintek.Mail.App.csproj (WinUI 3, net10.0-windows, CommunityToolkit.Mvvm)
- [x] **Testes** — completo:
  - DomainTests: EmailAddressTests, EmailDomainTests, DomainMembershipEvaluatorTests, FolderDomainValidatorTests
  - ApplicationTests: CreateDomainDirectoryHandlerTests, AddAccountHandlerTests
  - PersistenceTests: MailDbContextTests (InMemory)
  - InfrastructureTests: HtmlSanitizerServiceTests
- [x] **CI** — completo:
  - .github/workflows/ci.yml (build + test no Windows)

## Próximos passos (em ordem)

1. **Corrigir erros de compilação** — 54 erros identificados:
   - Testes usam APIs incorretas (DomainMembershipEvaluator, FolderDomainValidator, handlers)
   - Falta Microsoft.Extensions.DependencyInjection em Infrastructure e Infrastructure.Windows
   - Ambiguidade IMailTransport (MailKit vs Application.Ports)
   - DbConnectionEventData não encontrado em SqlCipherInterceptor
2. **Build e validação** — dotnet build, dotnet test
3. **Commit e push** — sincronizar com GitHub
4. **Fase 2** — Implementar funcionalidades avançadas (regras, categorias, busca)

## Bloqueios

Nenhum.

## Notas

- Context window em 74% — salvando estado para continuação em nova sessão.
- Build falhou com 54 erros de compilação.
- Próxima sessão deve começar por corrigir erros de compilação.
- Erros principais:
  1. Testes usam construtores/métodos incorretos (DomainMembershipEvaluator, FolderDomainValidator, handlers)
  2. Falta package Microsoft.Extensions.DependencyInjection em Infrastructure e Infrastructure.Windows
  3. Ambiguidade IMailTransport entre MailKit e Application.Ports
  4. DbConnectionEventData não encontrado em SqlCipherInterceptor
- TODOs pendentes:
  - Corrigir 54 erros de compilação
  - Integrar ICredentialStore no MailKitTransport
  - Implementar GetOrCreateEncryptionKey com DPAPI
  - Completar ViewModels com lógica real
  - Adicionar app.manifest e Package.appxmanifest
