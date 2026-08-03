# GLOSSARY — Sintek.Mail

Termos do domínio. Manter em ordem alfabética.

---

**Diretório de Domínio** — Pasta raiz lógica que representa um domínio de e-mail (ex.: `sintek.com.br`). Só pode conter contas cujo domínio seja **exatamente igual** ao seu (subdomínios só com `AllowSubdomains = true`). Entidade: `Domains`.

**DomainAlias** — Domínio adicional permitido em um Diretório de Domínio, além do `DomainName` principal. Usado na avaliação de pertencimento de mensagens. Entidade: `DomainAliases`.

**DomainMembershipEvaluator** — Serviço de domínio que decide se uma mensagem "pertence" a um Diretório de Domínio, conforme o `ValidationMode` configurado. Único caminho para mover mensagens (incluindo drag & drop).

**InvalidEmailAction** — O que fazer quando uma mensagem não pertence ao domínio de uma pasta restrita: `Block`, `WarnAndConfirm`, `MoveToPending`, `LogOnly`.

**MessageAddress** — Linha normalizada de um participante de mensagem (From/To/Cc/Bcc/ReplyTo) com o `Domain` já extraído em minúsculas e indexado. Peça central para avaliar regras de domínio em SQL.

**OutboxOperation** — Registro da fila de sincronização offline-first. Toda ação do usuário grava no SQLite e enfileira uma operação na mesma transação; o motor de sync a executa quando há conectividade.

**Pasta de Pendências (Pending)** — Pasta especial (`FolderType.Pending`) para onde vão mensagens incompatíveis com as regras de domínio, quando `InvalidEmailAction = MoveToPending`.

**Pasta restrita por domínio** — Pasta com `IsDomainRestricted = true` e `RestrictedToDomainId` definido. Só aceita mensagens que pertencem ao domínio. Subpastas herdam a restrição.

**SyncState** — Estado de sincronização de uma mensagem (ex.: sincronizada, pendente, erro). Distinto do `SyncStatus` da conta (`Offline/Online/Syncing/Error`).

**ValidationMode** — Como avaliar pertencimento ao domínio: `SenderOnly`, `RecipientOnly`, `SenderOrRecipient`, `SenderAndRecipient`, `AnyParticipant`.
