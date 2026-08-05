# STATUS.md — Sintek.Mail

> Última atualização: 2026-08-05

---

## Fase atual

**Fases 1 a 13 concluídas.** O roadmap da especificação está inteiro implementado, e as
três fases acrescentadas a pedido do usuário — contatos, agenda e sincronização de agenda
com servidor — também.

## Marco atual

**865 testes verdes no núcleo multiplataforma** (169 → 272 → 336 → 426 → 441 → 478 → 485
→ 530 → 535 → 569 → 584 → 587 → 692 → 790 → 865 ao longo das treze fases).

A fase 7 entregou a filtragem local inteira: `RuleEvaluator` puro no Domain (campos,
operadores e combinação E/OU da seção 6.5), `ApplyArrivalRulesHandler` aplicando bloqueio
de remetente e regras na chegada (só na Caixa de Entrada, ligado ao `MessageSyncService`),
listas de remetentes bloqueados/confiáveis (`SenderReputation` + migração), categorias com
gestão e aplicação pelo menu de contexto, modelos de mensagem no compositor, e os diálogos
de regras e organização encadeados a partir das configurações. Movimentação decidida por
regra passa pelo `MoveMessageHandler`; recusa pela regra de domínio vira auditoria, nunca
silêncio. Antes dela, as pendências das fases 4 e 6 foram fechadas: editor rico
(WebView2 contenteditable), rascunho automático por período de silêncio e pesquisas salvas
na barra lateral.

A fase 8 entregou a assistência por IA na ordem que o roadmap exigia: política antes dos
recursos. `AssistantGateway` é a porta única — escolhe o provedor (local primeiro,
sempre), aplica o consentimento do Diretório de Domínio e registra os envios externos em
auditoria antes de eles saírem, com provedor, tarefa e tamanho, nunca conteúdo. Os
provedores falam a API no formato OpenAI: local via runtime na máquina, nuvem com a chave
vinda do cofre. Recursos: resumo, sugestão de resposta e reescrita no compositor.

As fases 9 e 10 fecharam o roadmap: envio agendado sobre o `NextAttemptAt` que a fila já
respeitava, confirmação de leitura com pergunta antes de enviar, agrupamento por conversa,
atalhos do Outlook, limpeza de cache em duas etapas, pipeline de release com assinatura
por segredo, `.appinstaller` para atualização automática, instalador sem pacote e
`docs/implantacao.md`. O CONDSTORE, pendente desde a fase 3, também entrou.

A fase 11 trouxe o autocompletar de destinatários, que o produto não tinha e o Outlook tem:
`RecipientHistory` alimentado no envio, `Contact`/`ContactEmail` como catálogo curado,
`RecipientSuggestionRanker` combinando os dois com decaimento por recência, importação e
exportação em vCard 3.0/4.0 escritas à mão, `AutoSuggestBox` em Para/CC/CCO e o diálogo de
contatos com remoção individual do histórico, mais o "adicionar aos contatos" no painel de
leitura. Sugestão fora do domínio da conta aparece marcada, nunca escondida (D-021).

Ela também corrigiu quatro defeitos herdados das fases anteriores: o provedor do SQLite não
ordena nem compara `DateTimeOffset`, e por isso a listagem de mensagens da pasta (a tela
principal), o registro de auditoria, a limpeza de cache e a fila de saída quebravam na
primeira execução. A fila de saída é a mais grave: o modo offline inteiro depende dela.
Nenhuma tinha teste contra o banco real. Ver D-022.

A fase 12 trouxe a agenda. Teams, Meet e Outlook não precisaram de três integrações: os
três mandam o mesmo `text/calendar` da RFC 5545, e uma implementação da norma cobre os três
(D-023). Convite entra ao abrir a mensagem, resposta e remarcação saem pela fila de saída
como parte `text/calendar` — não como anexo, que o cliente do organizador não processaria
sozinho. Sequência menor nunca sobrescreve maior (D-024), e participante não move a própria
cópia de reunião alheia (D-025), que é onde o produto diverge do Outlook de propósito.

**O risco de fuso da fase 12 não se materializou.** O `Ical.Net` traz o `NodaTime`, que
carrega a própria base IANA; o `VTIMEZONE` embutido cobre os nomes do Windows que o Outlook
emite. Nenhum caminho depende da tabela do ICU, e o `InvariantGlobalization` continua ligado.

A fase 13 ligou a agenda ao servidor, nos dois sentidos. Três protocolos atrás de uma porta
(`ICalendarSyncProvider`), e não um: o Exchange Online nunca implementou CalDAV e o EWS tem
data de desligamento, então Microsoft 365 só é atendido pelo Graph — que fica desenhado e
registrado como próximo passo, com a ressalva de que ele não expõe `SEQUENCE` e por isso a
regra de D-024 não atravessa para lá (D-026). O que entrou pronto é o **CalDAV**: descoberta
por `current-user-principal` e `calendar-home-set`, sincronização incremental da RFC 6578
com paginação pelo 507 dentro do 207 e recuperação de token recusado, caminho alternativo
por `CTag` para servidor que não fala `sync-collection`, e escrita condicionada por `ETag`
com releitura obrigatória quando o servidor não devolve ETag forte.

O envio vem antes da leitura, pelo mesmo motivo que a fila de saída drena antes do IMAP.
Conflito não é resolvido em silêncio: fica marcado e espera decisão do usuário (D-027).
Ausência de um recurso só significa exclusão quando o provedor declara ter enumerado a
coleção inteira — deduzir isso do token nulo apagaria a agenda de quem usa servidor sem
`sync-collection` (D-028).

Dois defeitos foram achados pelos testes antes de qualquer servidor real: o `StringContent`
recusa media type com parâmetro (`text/calendar; charset=utf-8` lançava `FormatException` em
toda escrita), e o `StringWriter` comum declara `encoding="utf-16"` no XML enquanto os bytes
saem em UTF-8.

Distribuição: Domain 225, Application 304, Infrastructure 142, Presentation 141, Persistence 55.

> **Atenção:** houve troca de implementação em 04/08/2026 (ver `DECISIONS.md`, D-007). O que
> este arquivo descrevia antes daquela data pertencia à versão anterior, que foi substituída.
> As decisões D-001 a D-006 seguem válidas: a versão atual chegou às mesmas conclusões de
> forma independente.

## O que existe

- [x] Especificação e plano (`spec/`)
- [x] Harness de memória (`AGENTS.md` + `harness/`)
- [x] `Sintek.Mail.sln` com 7 projetos de src e 5 de teste, mais o filtro
      `Sintek.Mail.CrossPlatform.slnf` para o que compila fora do Windows
- [x] **Domain** — sem dependência de projeto ou pacote. VOs `EmailAddress`/`EmailDomain`,
      entidades, `DomainMembershipEvaluator` (os cinco modos) e `FolderRestrictionResolver`
      (herança pela árvore de pastas), `SenderTrustEvaluator` e `RuleEvaluator`. **161 testes**
- [x] **Application** — portas, `MoveMessageHandler` (as quatro ações configuráveis), ciclo
      de vida completo de conta (`AddAccount`, `TestAccountConnection`, `UpdateAccount`,
      `RemoveAccount`) e de Diretório de Domínio (`Create`, `Update`, `Remove`,
      `ChangeDomainName`), `SetFolderRestrictionHandler` e o motor de sincronização
      (`SyncAccountHandler`, `FolderMirrorService`, `MessageSyncService`, `SyncSchedule`),
      pesquisa, regras, organização e o `AssistantGateway` da fase 8. **207 testes**
- [x] **Persistence** — EF Core 10, SQLCipher, mapeamentos de todas as entidades, migrações
      (inicial, FTS5, reconstrução com external content, listas de remetentes e consentimento
      de IA) e `Fts5SearchService`.
      **23 testes**, incluindo a leitura dos bytes crus do arquivo para provar a
      criptografia e a pesquisa completa contra o banco migrado de verdade
- [x] **Infrastructure** — MailKit IMAP/SMTP, sanitizador de HTML, provedores OAuth
      Microsoft e Google, descoberta automática em cinco etapas, serialização MIME,
      `OutboxProcessor` completo, o laço `AccountSyncWorker` e os provedores de IA local e
      em nuvem. **94 testes**
- [x] **Presentation** — ViewModels multiplataforma: assistente de contas, editor de
      Diretórios de Domínio, lista de contas, fila de sincronização, árvore de navegação,
      lista de mensagens, painel de leitura, compositor, pesquisa, regras, organização e o
      painel de IA. **84 testes**
- [x] **Infrastructure.Windows** — Credential Manager via CsWin32; chave do banco
- [x] **App WinUI 3** — janela principal com árvore hierárquica e painel de leitura travado;
      diálogos de assistente de conta, editor de diretório, configurações e fila de
      sincronização; MSIX e unpackaged
- [x] **CI** — job Linux (núcleo) e job Windows (solution completa + MSIX)
- [x] **Docs** — `docs/decisoes-arquiteturais.md`, `docs/modelo-de-dados.md`,
      `docs/roadmap.md`, `CLAUDE.md`

## Próximos passos

1. **Revisar e integrar o PR #1.**
2. **Validação manual em Windows 11** — o único item que resta, e que nenhuma sessão
   automatizada faz. Ver "Bloqueios".
3. **Provedores Microsoft Graph e Google Calendar** — desenhados atrás do
   `ICalendarSyncProvider`, ainda sem implementação. O Graph exige decidir a precedência sem
   `SEQUENCE`, que é decisão nova e não adaptação (D-026).

Não há pendência de código em nenhuma das treze fases. O que resta é **pendência humana**, registrada em "Bloqueios": validação manual em Windows 11 e teste
contra servidores IMAP/SMTP/CalDAV reais com Client IDs de OAuth.

Vale notar: o MSIX compila mas **ainda não foi executado**. Nenhuma sessão automatizada
consegue fazer isso — exige uma máquina Windows 11 real. A validação funcional da interface
(assistente de conta, árvore de navegação, arrastar e soltar contra a regra de domínio,
painel de leitura, fila de sincronização) segue pendente e é o primeiro item de qualquer
revisão manual. Nenhuma sincronização foi executada contra um servidor IMAP real.

O que mudou desde a fase 1: a lógica de interface que não depende do WinUI e o motor de
sincronização inteiro passaram a ser verificados mecanicamente. O que resta para a validação
manual é XAML, `x:Bind`, comportamento visual e o diálogo com servidores reais — não mais
regra de negócio.

## Bloqueios

Nenhum bloqueio de código.

Duas dependências externas, ambas de implantação e não de desenvolvimento: os Client IDs de
OAuth (Entra ID e Google Cloud) e o certificado de assinatura do MSIX. Sem os Client IDs, os
provedores ficam implementados e desativados, e o assistente os apresenta com a explicação
de que falta configurar — não com erro de autenticação.

## Notas

- O núcleo é desenvolvido e testado em Linux de propósito: é o que verifica mecanicamente
  que `Domain`, `Application`, `Persistence`, `Infrastructure` e `Presentation` não
  adquiriram dependência do Windows. `CA1416` está configurado como erro pelo mesmo motivo.
- Armadilhas descobertas na prática e documentadas em `CLAUDE.md`: o pacote agregador do
  SQLite anulando o SQLCipher em silêncio, `Cache=Shared` derrubando o processo com falha
  nativa quando combinado com WAL, e o conjunto de restrições do `x:Bind` (nulo em
  `TextBox`, `double` no `NumberBox`, um `ContentDialog` por vez).
