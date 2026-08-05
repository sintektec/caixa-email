# Modelo de dados

A especificação define as entidades `Domains` e `Accounts` e termina truncada no meio
desta última (em `LastSyncAt — Date`). Este documento registra o modelo completo, marcando
o que veio da especificação e o que foi projetado.

Persistência: EF Core 10 sobre SQLite criptografado com SQLCipher. Chaves `Guid` versão 7,
carimbos `DateTimeOffset` em UTC.

## Da especificação

### DomainDirectories

O Diretório de Domínio: pasta raiz lógica que representa um domínio e agrupa as contas
que pertencem a ele.

| Campo | Tipo | Observação |
|---|---|---|
| `Id` | Guid | chave primária |
| `DomainName` | EmailDomain | **único**; normalizado em minúsculas |
| `Description` | string? | |
| `ValidationMode` | enum | `SenderOnly`, `RecipientOnly`, `SenderOrRecipient`, `SenderAndRecipient`, `AnyParticipant` |
| `InvalidEmailAction` | enum | `Block`, `WarnAndConfirm`, `MoveToPending`, `LogOnly` |
| `AllowSubdomains` | bool | falso por padrão |
| `IsActive` | bool | |
| `CreatedAt` / `UpdatedAt` | DateTimeOffset | |
| `IsFavorite`, `SortOrder` | — | *acrescentados*, para a árvore de navegação |

### Accounts

| Campo | Tipo | Observação |
|---|---|---|
| `Id` | Guid | |
| `DomainDirectoryId` | Guid | FK |
| `EmailAddress` | EmailAddress | **único** |
| `DisplayName` | string | |
| `ImapHost` / `ImapPort` / `SmtpHost` / `SmtpPort` | | |
| `UseSsl` | bool | |
| `AuthenticationType` | enum | `Password`, `OAuth2` |
| `IsActive` | bool | |
| `LastSyncAt` | DateTimeOffset? | |
| `ImapSecurity` / `SmtpSecurity` | enum | *acrescentados*: controle fino por protocolo |
| `CredentialKey` | string | *acrescentado*: **identificador** no Credential Manager, nunca a senha |
| `OAuthProvider` | enum | *acrescentado* |
| `SyncStatus`, `LastSyncError`, `SyncIntervalMinutes`, `BodyDownloadPolicy` | | *acrescentados* |

## Projetadas neste plano

### DomainAliases
Domínios adicionais aceitos por um diretório. Atende ao critério "o domínio está
registrado como domínio adicional permitido" da seção 5.3.

### Folders
`AccountId`, `ParentFolderId?`, `Name`, `DisplayName`, `FolderType`, `RemotePath`,
`Delimiter`, `IsFavorite`, `IsSubscribed`, `SyncEnabled`, `IsLocalOnly`, `UnreadCount`,
`TotalCount`, `UidValidity?`, `HighestModSeq?`, `LastSeenUid?`, `SortOrder`.

A restrição por domínio existe em duas formas:

- `RestrictedToDomainDirectoryId` — vínculo **explícito**, definido nesta pasta.
- `EffectiveRestrictionDomainDirectoryId` — vínculo **efetivo**, já resolvido: o explícito
  ou, na falta dele, o herdado do ancestral mais próximo.

O valor efetivo é desnormalizado porque toda operação de arrastar e soltar precisa dele;
recalcular subindo a árvore a cada gesto transformaria a interação em uma sequência de
consultas recursivas.

Índice único `(AccountId, RemotePath)` **filtrado** por `RemotePath <> ''`: pastas locais
(Pendências, Caixa de Saída) têm caminho vazio, e sem o filtro a segunda delas violaria a
restrição.

### Messages
`AccountId`, `FolderId`, `ThreadId?`, `MessageId` (RFC 5322), `InReplyTo?`,
`ReferencesRaw?`, `Uid?`, `ModSeq?`, `Subject`, `SubjectNormalized`, `FromAddress?`,
`SentAt`, `ReceivedAt`, `Preview`, `Size`, `HasAttachments`, `IsRead`, `IsFlagged`,
`Importance`, `IsDraft`, `IsAnswered`, `IsDeleted`, `SyncState`, `ScheduledSendAt?`,
`ReadReceiptRequested`.

Índices: `(FolderId, ReceivedAt DESC)` para a listagem; `(AccountId, MessageId)` para
deduplicação na sincronização; `ThreadId`; `(AccountId, SyncState)` para a fila.

### MessageAddresses
`MessageId`, `Kind` (From/Sender/To/Cc/Bcc/ReplyTo), `Address`, `DisplayName?`, `Domain`.

O campo `Domain` é desnormalizado e indexado — é o que torna a regra de Diretório de
Domínio viável em escala.

### MessageBodies
Tabela separada de `Messages` porque a listagem lê centenas de linhas e nenhuma precisa do
corpo. Guarda `HtmlBody`, `TextBody`, `SanitizedHtml`, `HasRemoteContent`,
`RemoteContentAllowed`, `DownloadedAt`.

`SanitizedHtml` é o único conteúdo que pode chegar ao WebView2. `HtmlBody` preserva o
original apenas para reprocessar caso as regras de sanitização mudem.

### Attachments
`MessageId`, `FileName`, `ContentType`, `Size`, `ContentId?`, `IsInline`, `StoragePath?`,
`PartSpecifier`, `IsDownloaded`, `IsSuspicious`.

O conteúdo fica em arquivo no disco, não como BLOB: um anexo de dezenas de megabytes
inflaria o banco permanentemente — o SQLite não devolve espaço sem `VACUUM` — e faria
cada backup carregar todos os anexos junto.

`FileName` é sanitizado contra travessia de diretório na criação: um servidor hostil pode
anunciar um anexo chamado `..\..\Startup\x.exe`.

### OutboxOperations
`AccountId`, `OperationType`, `EntityId`, `PayloadJson`, `Status`, `Sequence`,
`DependsOnId?`, `AttemptCount`, `MaxAttempts`, `NextAttemptAt?`, `LastError?`,
`CompletedAt?`.

Índice único `(AccountId, Sequence)` e índice de consulta
`(AccountId, Status, NextAttemptAt)`.

### RecipientHistory
`AccountId`, `Address`, `DisplayName?`, `UseCount`, `LastUsedAt`.

Índice único `(AccountId, Address)`: o endereço acumula usos em vez de se repetir, e o
índice é a rede que segura uma corrida entre dois envios simultâneos. Índice de consulta
`(AccountId, LastUsedAt DESC)`.

A entrada nasce no **envio**, não na entrega — ver D-020.

### Contacts e ContactEmails
`Contacts`: `AccountId`, `DisplayName`, `GivenName?`, `FamilyName?`, `Organization?`,
`JobTitle?`, `PhoneNumber?`, `Notes?`, `ExternalId?`.

`ContactEmails`: `ContactId`, `Address`, `Label?`, `IsPrimary`.

`ExternalId` guarda o `UID` do vCard; o índice único `(AccountId, ExternalId)` é o que
permite reimportar a exportação do Outlook sem duplicar o catálogo. Único por conta, e não
global, porque o mesmo contato pode legitimamente existir em duas contas.

### CalendarEvents e EventAttendees
`CalendarEvents`: `AccountId`, `Uid`, `Sequence`, `Summary`, `Description?`, `Location?`,
`MeetingUrl?`, `StartsAt`, `EndsAt`, `IsAllDay`, `TimeZoneId?`, `Status`,
`OrganizerAddress?`, `OrganizerDisplayName?`, `RecurrenceRule?`, `SourceMessageId?`,
`HasReminder`, `ReminderMinutesBefore`.

`EventAttendees`: `CalendarEventId`, `Address`, `DisplayName?`, `Role`, `Response`.

Índice único `(AccountId, Uid)`: o `UID` é a identidade do evento na norma, e é por ele que
a atualização enviada pelo organizador encontra o compromisso local. Único por conta porque
duas contas podem ter sido convidadas para a mesma reunião.

`SourceMessageId` tem exclusão `SET NULL`: a limpeza de cache apaga mensagens antigas, e a
agenda não depende delas para existir. `StartsAt` e `EndsAt` guardam o instante já resolvido;
`TimeZoneId` é informativo, para exibir "14h em São Paulo" e para reemitir o convite no fuso
que o organizador escolheu.

`RecurrenceRule` guarda o `RRULE` cru: quem sabe expandir uma recorrência é a biblioteca de
iCalendar, e uma decomposição própria criaria uma segunda interpretação da norma para
divergir da primeira.

### Demais
`MessageThreads`, `Categories` + `MessageCategories`, `Rules` + `RuleConditions` +
`RuleActions`, `SavedSearches`, `Signatures`, `MessageTemplates`, `AppSettings`,
`AuditLog`.

## Ordenação e comparação por data

Nenhuma consulta em LINQ ordena nem compara diretamente uma coluna `DateTimeOffset`: o
provedor do SQLite recusa as duas coisas, e a quebra só aparece em tempo de execução. Ambas
passam por `SqliteFunctions.JulianDay(...)`, traduzida para o `julianday()` do SQLite, com
desempate por `Id` na ordenação. Ver D-022.

## Busca offline

Tabela espelho `MessagesSearch` — que liga o `rowid` inteiro exigido pelo FTS5 ao `Guid` da
mensagem e guarda o texto indexado — mais a tabela virtual `MessagesFts` em modo
**external content** (`content='MessagesSearch'`, `content_rowid='Rowid'`), com
`tokenize='unicode61 remove_diacritics 2'`. Gatilhos sobre o espelho e sobre as tabelas de
origem mantêm o índice em dia. Ver D-015.

## O que nunca entra no banco

Senhas, tokens OAuth e a chave do SQLCipher — todos no Windows Credential Manager. As
entidades guardam apenas o identificador da credencial.

Conteúdo de mensagem também não entra no `AuditLog` nem no `PayloadJson` da fila de saída.
