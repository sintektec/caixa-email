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

## 2026-08-04 — Sessão 7: Troca de implementação e Fase 1

**Objetivo:** Integrar a implementação desenvolvida em paralelo, resolvendo a divergência
com a `main`.

**O que foi feito:**
- Resolvidos 38 conflitos mantendo `src/` e `tests/` da versão nova e preservando `spec/`,
  `AGENTS.md`, `harness/`, `.analysis/` e `.continue/` (ver `DECISIONS.md`, D-007).
- Removidos 1521 arquivos de `bin/`/`obj/` do versionamento (D-008); repositório de 1691
  para 143 arquivos.
- PR #1 aberto e levado ao verde em quatro rodadas de CI. As seis correções foram todas de
  erro que só o compilador de destino revela, e estão registradas em `CLAUDE.md`.
- 169 testes.

**Observação:** esta entrada foi escrita retroativamente na sessão 8 — a sessão 7 encerrou
sem cumprir a regra de ouro da seção 3.

---

## 2026-08-04 — Sessão 8: Fase 2 (Contas)

**Objetivo:** Executar a fase 2 do `docs/roadmap.md`.

**O que foi feito:**
- Descoberta automática completa em cinco etapas, com origem registrada no resultado e três
  defesas contra documento remoto hostil (D-009).
- Ciclo de vida completo de contas e Diretórios de Domínio: consentimento OAuth interativo,
  teste de configuração isolado, edição que testa antes de alterar, remoção em duas etapas
  com impacto medido.
- ViewModels movidos para `Sintek.Mail.Presentation`, multiplataforma e coberto pelo job
  Linux do CI (D-010).
- Diálogos WinUI: assistente de conta, editor de diretório e tela de configurações.
- 169 → 272 testes.
- GitGuardian reprovou o primeiro push por cinco literais de senha em teste. Substituídos
  por `FakeSecret.For(...)` e os commits fundidos para tirá-los do histórico do PR.

**Próxima sessão:**
- Aguardar a decisão do usuário entre seguir para a fase 3 (Sincronização) ou parar para
  validação manual em Windows 11.

---

## 2026-08-05 — Sessão 9: Fase 3 (Sincronização)

**Objetivo:** Executar a fase 3 do `docs/roadmap.md`.

**O que foi feito:**
- Espelhamento da árvore de pastas, com a regra de nunca apagar pasta ausente da listagem
  (D-012) e recálculo da herança de restrição para pastas novas.
- Sincronização incremental por UID, ressincronização completa na troca de UIDVALIDITY,
  reconciliação de exclusões feitas fora do cliente e precedência da alteração local
  pendente sobre o marcador do servidor.
- `SyncAccountHandler` com a ordem fila-antes-de-leitura (D-011) e a classificação na
  chegada com tabela de decisão própria (D-013).
- `OutboxProcessor` ampliado para envio, rascunhos, cópia e operações de pasta;
  `MimeMessageWriter` compartilhado entre SMTP e APPEND.
- `SyncSchedule` (política pura) e `AccountSyncWorker` (laço com IDLE e sondagem).
- Fila de sincronização visível na interface, com descarte de operação.
- 272 → 336 testes.

**Próxima sessão:**
- Aguardar a decisão do usuário entre seguir para a fase 4 (Leitura e composição) ou parar
  para validação manual em Windows 11.

---

## 2026-08-05 — Sessão 9 (continuação): Fase 4 (Leitura e composição)

**Objetivo:** Concluir a fase 4, com as adições de escopo pedidas pelo usuário (spam/lixo
eletrônico e IA) registradas no roadmap.

**O que foi feito:**
- Roadmap ganhou a origem do escopo novo: spam distribuído nas fases 4/5/7, IA como fase 8
  com política de privacidade antes de recursos.
- Veredito do servidor (SPF/DKIM/DMARC, X-Spam-*) lido na chegada; `SenderTrustEvaluator`
  com detecção de remetente disfarçado; faixa de confiança no painel de leitura.
- Download sob demanda de corpo e anexos; anexos em disco fora do banco
  (`FileAttachmentStore`, com nome físico próprio — o nome do remetente é hostil).
- `DraftComposer` (resposta, resposta a todos, encaminhamento), `ForgottenAttachmentDetector`
  e `ComposeMessageHandler` (enviar = entregar à fila, D-014). Compositor WinUI completo.
- Duas armadilhas do `InvariantGlobalization` documentadas em `CLAUDE.md`.
- 336 → 426 testes.

**Próxima sessão:**
- Aguardar decisão do usuário: fase 5, validação manual em Windows, ou revisão do PR #1.

---

## 2026-08-05 — Sessão 9 (continuação): Fase 5 (Pastas e regras na interface)

**O que foi feito:**
- `ManageFolderHandler`: criar (com caminho montado pelo delimitador da mãe e herança de
  restrição), renomear (propagando os caminhos das descendentes — o RENAME do IMAP renomeia
  a subárvore), excluir em duas etapas com impacto, favoritos.
- `MarkAsSpamHandler`: mover + `$Junk`/`$NotJunk`, com a palavra-chave enfileirada ANTES da
  movimentação (a fila é sequencial; depois do MOVE o UID antigo não aponta para nada).
  "Não é spam" passa pelo `MoveMessageHandler` — Caixa de Entrada restrita desvia para
  pendências, como a regra manda.
- Troca de domínio na interface: analisar impacto → exibir relatório → confirmar, sobre o
  `ChangeDomainNameHandler` da fase 1.
- WinUI: diálogo de pasta com vínculo a diretório, menus de contexto na árvore e na lista
  de mensagens, seção de troca de domínio no diálogo de diretório.
- 426 → 441 testes.

**Próxima sessão:**
- Aguardar decisão do usuário: fase 6 (Pesquisa), validação manual em Windows, ou revisão
  do PR #1.

---

## 2026-08-05 — Sessão 9 (continuação): Fase 6 (Pesquisa)

**O que foi feito:**
- Migração `RebuildSearchIndex`: o FTS5 contentless vira external content sobre a tabela
  física `MessagesSearch` (D-015). Corpo, participantes e nomes de anexo agora entram e
  saem do índice conforme as tabelas de origem mudam — inclusive no download sob demanda.
- `Fts5SearchService` (Persistence): MATCH com termos por prefixo e sem acento, filtros de
  campo (`Subject:`, `TextBody:`, `FromAddress:` com nome exibido, `AttachmentNames:`),
  filtros estruturais da seção 6.4 (conta, pasta, diretório, categoria, datas com
  `datetime()` dos dois lados, lida, sinalizador, anexos, importância, status de
  sincronização), Para/CC como EXISTS por tipo de participante.
- `SavedSearchesHandler`: salvar atualiza pela identidade do nome; JSON tolerante a
  conteúdo corrompido. `SearchViewModel` testável no Linux; flyout de pesquisa avançada e
  modo de resultados no painel central (`ShowSearchResultsAsync`, com `FolderId` nulo de
  propósito).
- Armadilha nova em `CLAUDE.md`: `Guid` em SQL manual vai como `Guid`, nunca `ToString()`
  (o provider grava TEXT maiúsculo; a comparação minúscula falha em silêncio).
- 441 → 478 testes.

**Próxima sessão:**
- Aguardar decisão do usuário: fase 7 (Automação e filtragem local), validação manual em
  Windows, ou revisão do PR #1.

---

## 2026-08-05 — Sessão 9 (continuação): pendências das fases 4/6 e Fase 7 (Automação)

**O que foi feito:**
- Pendências fechadas: editor rico no compositor (WebView2 contenteditable, barra de
  formatação, CSP sem origem externa), rascunho automático por período de silêncio
  (política no ViewModel, testável no Linux) e pesquisas salvas na barra lateral.
- Fase 7: `RuleEvaluator` puro (campos/operadores/E-OU da 6.5), `ApplyArrivalRulesHandler`
  na chegada (bloqueados primeiro, depois regras por prioridade; só Caixa de Entrada;
  movimentação sempre via `MoveMessageHandler`, recusa vira auditoria — D-016).
- `SenderReputation` (+migração `SenderReputationLists`): bloqueado → lixo eletrônico pelo
  caminho do spam; confiável → imagens remotas liberadas no painel de leitura.
- Gestão completa: `ManageRulesHandler` (definição validada, gravação reconstrói),
  categorias (CRUD + menu de contexto "Categorizar" + filtro na pesquisa), modelos de
  mensagem (CRUD + aplicação no compositor), diálogos WinUI encadeados das configurações.
- 485 → 530 testes.

**Próxima sessão:**
- Aguardar decisão do usuário: fase 8 (Assistência por IA), validação manual em Windows,
  ou revisão do PR #1.

---

## 2026-08-05 — Sessão 9 (continuação): pendências da Fase 7 fechadas

**O que foi feito:**
- `MoveMessageHandler.HandleCopyAsync`: cópia enfileirada como `CopyMessage`, com a regra
  de domínio da pasta de destino aplicada. Cópia incompatível é recusada em qualquer modo
  — não existe desviar cópia para pendências.
- Encaminhamento automático de regra: baixa corpo e anexos, monta com `DraftComposer` e
  entrega o envio à fila (D-014). Conteúdo indisponível recusa o encaminhamento inteiro.
- Condição de corpo passa a baixar o corpo antes de avaliar; a prévia vira o recuo quando
  o download falha.
- `AGENTS.md` seção 5: pendência que a sessão pode resolver não fica para trás (diretriz
  do usuário).
- 530 → 535 testes.

**Nota de infraestrutura:** o container foi revertido para o commit da fase 4 no meio da
sessão. Nada se perdeu porque tudo já estava no remoto; o working tree foi restaurado com
`git reset --hard origin/<branch>`. Lição registrada: enviar cedo, não acumular trabalho
não commitado.

**Próxima sessão:**
- Fase 8 (Assistência por IA), conforme decisão do usuário.

---

## 2026-08-05 — Sessão 9 (continuação): Fase 8 (Assistência por IA)

**O que foi feito:**
- Política antes dos recursos, como o roadmap exigia (D-017): `IAssistantProvider` com
  `AssistantLocality`, `AssistantGateway` como porta única, consentimento em
  `DomainDirectory.AllowsCloudAssistant` (migração `CloudAssistantConsent`, padrão falso)
  e auditoria do envio externo antes da chamada, sem conteúdo.
- Provedores na Infrastructure falando a API no formato OpenAI: `LocalAssistantProvider`
  (Ollama/LM Studio/llama.cpp na máquina) e `CloudAssistantProvider` (chave do cofre).
- Recursos: resumo, sugestão de resposta e reescrita no compositor, com corte de 12 mil
  caracteres e envio só do texto puro.
- Interface: botões de IA no leitor (visíveis só quando há assistente utilizável), faixa
  de recusa com atalho para as configurações do diretório, caixa de consentimento no
  diálogo de Diretório de Domínio e "Reescrever com IA" no compositor.
- 535 → 569 testes.

**Próxima sessão:**
- Fase 9 (Acabamento) ou validação manual em Windows, conforme decisão do usuário.

---

## 2026-08-05 — Sessão 9 (continuação): Fases 9 e 10

**O que foi feito:**
- Fase 9: envio agendado (a fila já respeita `NextAttemptAt` — agendar é enfileirar com a
  data certa, sem segundo relógio), confirmação de leitura com pergunta antes de enviar e
  recusa registrada, agrupamento por conversa, atalhos completos do Outlook (Ctrl+F
  encaminha, como lá), limpeza de cache em duas etapas que só descarta o que o servidor
  ainda tem.
- Fase 10: `release.yml` disparado por tag, com assinatura vinda de segredo em base64 e
  apagada ao fim do job; `.appinstaller` para atualização automática a cada 8 horas de
  uso; `install-unpackaged.ps1` instalando sob `%LOCALAPPDATA%` sem administrador e
  preservando `appsettings.Local.json`; `docs/implantacao.md` com o registro dos apps
  OAuth nos dois provedores.
- Armadilha nova em `CLAUDE.md`: `InfoBar.ActionButton` aceita um `ButtonBase`, não um
  painel — o job Windows do CI foi quem pegou (WMC0015), como previsto.
- 584 testes.

**Próxima sessão:**
- Nada de código pendente. O que resta é validação manual em Windows 11, que exige máquina
  real.

---

## 2026-08-05 — Sessão 9 (continuação): planejamento das fases 11 e 12

**O que foi feito:**
- Skills globais clonadas em `/workspace/skills-globais` (1.366). O hook `SessionStart`
  falhava por falta de credencial; o repositório foi anexado à sessão explicitamente.
  Relevantes para as fases novas: `csharp-pro`, `dotnet-architect`,
  `dotnet-backend-patterns`, `ddd-tactical-patterns`, `ddd-strategic-design`,
  `architecture-decision-records`. **Não há skill de iCalendar/CalDAV** no acervo.
- Apurado que o histórico de contatos **não existe**: `KnownCorrespondent` guarda só nome
  e domínio, e só de mensagens lidas e não-spam — serve à detecção de disfarce, não a
  autocompletar.
- Roadmap ganhou as fases 11 (contatos) e 12 (agenda), com a origem registrada em "Origem
  do escopo". Nenhum código escrito ainda.

**Próxima sessão:**
- Medir o risco de fuso horário da fase 12 antes de qualquer código; decidir a ordem com o
  usuário.

---

## 2026-08-05 — Sessão 9 (continuação): fase 11, contatos e histórico de destinatários

**O que foi feito:**
- Fase 11 inteira: `RecipientHistory`, `Contact`/`ContactEmail` e `RecipientSuggestionRanker`
  no Domain; `RecipientHistoryHandler`, `ManageContactsHandler` e `VCardSerializer` na
  Aplicação; configurações EF, repositórios e a migração `ContactsAndRecipientHistory`;
  `ContactsViewModel` e o autocompletar no `ComposerViewModel`; `AutoSuggestBox` em
  Para/CC/CCO e o `ContactsDialog` no WinUI, ligado por um botão na barra superior.
- O histórico é alimentado **no envio** e nunca derruba um envio ao falhar (D-020). Sugestão
  fora do domínio da conta aparece marcada, nunca escondida (D-021).
- vCard 3.0 e 4.0 escritos à mão: desdobra linha continuada, aceita as duas sintaxes de
  `PREF` e nunca lança por cartão malformado — o cartão ruim é contado e ignorado.
- **Quatro defeitos reais encontrados por testes novos:** o provedor do SQLite não ordena nem
  compara `DateTimeOffset`. Quebravam, desde a fase 1, a listagem de mensagens da pasta (a
  tela principal), o registro de auditoria, a limpeza de cache e — a pior — a fila de saída,
  que nunca drenaria. Nenhuma dessas consultas tinha teste contra o banco real. Corrigidas
  com `SqliteFunctions.JulianDay`, que traduz para o `julianday()` do SQLite (D-022).
- Armadilha nova: `dotnet ef migrations add --no-build` lê o assembly de `Debug`. Compilar
  só em `Release` fez a primeira tentativa gerar de novo o conteúdo da migração anterior.
- Armadilha nova: o `AutoSuggestBox` escreve o item escolhido na caixa **antes** de avisar
  quem o escolheu; com ligação de duas vias, a propriedade chega destruída em
  `QuerySubmitted`. O ViewModel guarda o texto de base e aplica a troca sobre ele.
- "Adicionar aos contatos" no painel de leitura, com o remetente já no catálogo não sendo
  duplicado nem sobrescrito — o contato pode ter sido editado à mão desde então.
- 692 testes (Domain 185, Application 259, Infrastructure 94, Presentation 119,
  Persistence 35).

**Próxima sessão:**
- Fase 12 — Agenda. O risco de fuso já está medido: ler o `VTIMEZONE` embutido no `.ics` e
  emitir o da `TimeZoneInfo.Local`, sem depender do mapeamento IANA que o
  `InvariantGlobalization` remove.

---

## 2026-08-05 — Sessão 9 (continuação): fase 12, agenda

**O que foi feito:**
- Fase 12 inteira: `CalendarEvent`/`EventAttendee` e `EventMoveEvaluator` no domínio;
  `ICalendarSerializer` como porta e `IcalNetCalendarSerializer` (Ical.Net 5.2.3) como
  adaptador; `ImportInvitationHandler`, `RespondToInvitationHandler`, `MoveEventHandler` e
  `ManageEventsHandler`; migração `CalendarEvents`; `CalendarViewModel` e o `CalendarDialog`
  com grade de dia/semana/mês, ligado por um botão na barra superior.
- Convite entra ao abrir a mensagem — a sincronização traz só cabeçalhos, e é no download do
  corpo que a parte `text/calendar` aparece. Falha na importação não derruba o download.
- Resposta e remarcação saem pela fila de saída como parte `text/calendar` em
  `multipart/alternative` (`CalendarPartBuilder`), não como anexo.
- **O risco de fuso medido na sessão anterior não se materializou:** o Ical.Net traz o
  NodaTime com a própria base IANA, e usa o `VTIMEZONE` embutido para nomes do Windows. Os
  dois formatos foram medidos e devolvem o instante correto com `InvariantGlobalization`
  ligado. Nada precisou ser desligado.
- **Comportamento medido que mudou o desenho:** o Ical.Net inventa um `UID` quando o
  documento não traz, e diferente a cada leitura. A importação ganhou segunda via de
  identidade pela mensagem de origem, sem a qual rebaixar o corpo criaria um compromisso a
  cada vez.
- 790 testes (Domain 210, Application 285, Infrastructure 114, Presentation 137,
  Persistence 44). D-023 a D-025 registradas.

**Próxima sessão:**
- Nada de código pendente nas doze fases. O que resta é validação manual em Windows 11 e
  contra servidores reais — a pendência humana registrada em "Bloqueios". A fase 13
  (CalDAV/EWS) está descrita no roadmap e fora do escopo acordado.

---

## 2026-08-05 — Sessão 9 (continuação): fase 13, sincronização de agenda com servidor

**O que foi feito:**
- `ICalendarSyncProvider` como porta única, com `CalendarProviderKind { None, CalDav,
  MicrosoftGraph, GoogleCalendar }`. A implementação entregue é a de **CalDAV**; Graph e
  Google ficam desenhados atrás da mesma porta e registrados como próximo passo (D-026).
- Domínio: `RemoteCalendar`, `CalendarConflictEvaluator` (puro, como o
  `DomainMembershipEvaluator`), e o estado de sincronização em `CalendarEvent` —
  `MarkPending` que nunca rebaixa um estado mais forte, como `Message.MarkPending`.
- `CalendarSyncService`: envio antes da leitura, falha por coleção sem derrubar as demais,
  paginação, e remoção só em passada completa declarada pelo provedor (D-028).
- Infraestrutura CalDAV: `CalDavTransport` (redirecionamento à mão, autenticação preemptiva,
  ETag cru), `DavXml`, `CalDavRequests`, `CalDavHref` e `CalDavCalendarSyncProvider`.
- Persistência: `RemoteCalendars`, colunas de espelho em `CalendarEvents`, servidor de agenda
  em `Accounts`, migração `CalendarServerSync`, repositórios e as consultas por
  `julianday()`.
- Interface: conflito visível na agenda com escolha entre a versão local e a do servidor
  (D-027), e servidor de agenda no assistente de contas, testado junto com IMAP e SMTP.
- 865 testes (Domain 225, Application 304, Infrastructure 142, Presentation 141,
  Persistence 55). D-026 a D-028 registradas.

**Dois defeitos achados pelos testes antes de haver servidor real:**
- `StringContent` recusa media type com parâmetro — `text/calendar; charset=utf-8` lançava
  `FormatException` em toda escrita.
- `StringWriter` declara `encoding="utf-16"` no XML enquanto os bytes saem em UTF-8.

**Um defeito de fase anterior corrigido de passagem:** `ManageEventsHandler.RemoveAsync`
apagava a linha local direto. Para compromisso espelhado do servidor isso faria a exclusão
morrer local — o recurso continuaria lá e a passada seguinte o traria de volta como
novidade. Agora fica `PendingDelete` e some depois de o servidor aceitar.

**Também nesta sessão:** `.config/dotnet-tools.json` passou a fixar o `dotnet-ef` 10.0.10.
Migração exige o pacote, e a versão precisa acompanhar o EF Core do projeto.

**Próxima sessão:**
- Provedores Microsoft Graph e Google Calendar, se houver interesse. O Graph exige decidir a
  precedência sem `SEQUENCE` — decisão nova, não adaptação de D-024.
- O que continua pendente é humano: validação manual em Windows 11 e teste contra servidores
  IMAP/SMTP/CalDAV reais com Client IDs de OAuth.
