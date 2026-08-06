# STATUS.md — Sintek.Mail

> Última atualização: 2026-08-06

---

## Fase atual

**Fases 1 a 14 concluídas.** O roadmap da especificação está inteiro implementado, e as
quatro fases acrescentadas a pedido do usuário — contatos, agenda, sincronização de agenda com
servidor e os provedores em nuvem — também.

## Marco atual

**1008 testes verdes no núcleo multiplataforma** (169 → 272 → 336 → 426 → 441 → 478 → 485
→ 530 → 535 → 569 → 584 → 587 → 692 → 790 → 865 → 928 ao longo das catorze fases; 987 e depois
1008 com os defeitos da primeira execução real).

**A primeira execução real aconteceu**, em Windows 11, e é o marco que separa "compila e passa
nos testes" de "roda". Ela encontrou o que catorze fases de teste não encontraram, porque os
defeitos estavam onde não havia teste: na composição do contêiner, no encadeamento entre
ViewModel e `WebView2`, e no estado de conexão de um cliente com escopo. Os quatro achados
estão em D-034 a D-036 e no `CLAUDE.md`; o mais grave — um `DbContext` único para a execução
inteira — existia desde a fase 1.

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

A fase 14 fechou os três protocolos de agenda. O Microsoft Graph e a Google Calendar API
entraram atrás do mesmo `ICalendarSyncProvider`, e a porta mudou junto: ela passou a trocar
`CalendarEventData`, não texto iCalendar (D-030). Só um dos três fala iCalendar — obrigar os
outros a sintetizar um documento para o motor reinterpretar seria inventar um formato
intermediário e uma segunda chance de errar em cada caminho.

**Duas descobertas mudaram o desenho antes de a primeira linha ser escrita.** O único `delta`
de calendário em `v1.0` do Graph é `calendarView/delta`, que exige janela de datas e **expande
a recorrência em ocorrências** — uma reunião semanal de um ano vira 52 objetos sem `RRULE`, e o
mestre com a regra é justamente o que este produto guarda. A leitura passou a ser `events` com
`$filter` em `lastModifiedDateTime`, com passada completa periódica porque essa consulta não
reporta exclusão (D-031). Na Google, ao contrário, o `syncToken` cobre tudo: alterações e
exclusões na mesma listagem, a exclusão como `status: cancelled`, e 410 quando o token vence.

A precedência sem `SEQUENCE` — a decisão que D-026 deixara em aberto — ficou em `RemoteVersion`
e `AllowsVersion`: só se compara o que existir dos dois lados, `SEQUENCE` com `SEQUENCE` e
instante com instante. Comparar os dois entre si produziria recusa arbitrária (D-029).

Distribuição: Domain 231, Application 304, Infrastructure 189, Presentation 149, Persistence 55.

**Um defeito de fase anterior veio à tona ao documentar o registro OAuth** (D-032): o
`GoogleOAuthProvider` montava `ClientSecrets` sem `ClientSecret`. Cliente "Desktop app" da
Google recebe os dois valores e exige o segredo na troca do código e na renovação; a
`Google.Apis.Auth` omite o campo nulo sem lançar nada, então o defeito era invisível até a
implantação — o navegador abria, o usuário consentia, e a falha aparecia só na resposta do
servidor de token. Corrigido, com teste que trava a regra.

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
A **tradução de `RRULE` para o objeto de recorrência do Graph**, que era o item 3 desta
lista, foi feita: `GraphRecurrence.ToRecurrence` escreve o conjunto que `ToRRule` relê —
diária, semanal com dias, mensal por dia do mês, anual, com contagem ou data-limite — e
recusa o resto. Compromisso recorrente criado aqui não sobe mais como encontro único
(D-033). Os padrões `relative*` seguem fora dos dois lados, juntos.

Não há pendência de código em nenhuma das catorze fases. O que resta é **pendência humana**, registrada em "Bloqueios": validação manual em Windows 11 e teste
contra servidores IMAP/SMTP/CalDAV reais, e contra Microsoft 365 e Google, com Client IDs de
OAuth.

**A validação manual começou e ainda não terminou.** Já foi verificado, em máquina real: o
assistente de conta com OAuth da Google e da Microsoft, a criação de Diretório de Domínio, a
listagem de mensagens e a sincronização inicial. Falta reverificar o painel de leitura, o
"Exibir imagens" e o download sob demanda **depois** das correções desta rodada — as três
mudaram, e nenhuma delas é observável no job Linux.

Vale notar: o MSIX compila mas **ainda não foi empacotado nem instalado** — o que rodou na
máquina de validação foi o build local, direto do Visual Studio. Arrastar e soltar contra a
regra de domínio e a fila de sincronização offline seguem sem verificação em máquina real.

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

**Os Client IDs deixaram de ser bloqueio.** Ambos os aplicativos foram registrados:

- **Entra ID** — registro `SINTEK-Mail`, cliente público, `TenantId = common`. As cinco
  permissões delegadas estão concedidas **no Microsoft Graph**: `IMAP.AccessAsUser.All`,
  `SMTP.Send`, `Calendars.ReadWrite`, `offline_access`, `User.Read`. Anotado porque custou
  uma volta: a API *Office 365 Exchange Online* oferece IMAP e SMTP só sob permissões de
  **aplicativo** (`IMAP.AccessAsApp`, `SMTP.SendAsApp`), que servem ao fluxo de daemon e não
  ao nosso — a lista delegada dela não traz nenhuma das duas.
- **Google Cloud** — Client ID no `appsettings.json`; o Client secret fica no
  `appsettings.Local.json`, fora do controle de versão.

**O certificado de assinatura ganhou um segundo motivo.** Ele não serve mais só para
distribuir o MSIX: o **Smart App Control** do Windows 11 recusa executar binário sem
assinatura de CA reconhecida, e isso inclui todo build local. Na máquina de validação a
interface compilou sem erro e não pôde ser executada — "Uma política de Controle de
Aplicativo bloqueou este arquivo". A saída local é desligar o Smart App Control, que é
irreversível sem reinstalar o Windows; certificado autoassinado não resolve, porque ele não
honra raiz confiada localmente. Registrado no README.

Continua pendente, e agora é o único item externo além do certificado: **a verificação de
editor no Entra ID**. Cosmética para uso no domínio próprio — só um aviso de "aplicativo não
verificado" no consentimento. Passa a ser bloqueio real se o MSIX for distribuído a outras
organizações, porque locatário que bloqueia aplicativo não verificado por política recusa a
entrada dos próprios usuários.

## Notas

- O núcleo é desenvolvido e testado em Linux de propósito: é o que verifica mecanicamente
  que `Domain`, `Application`, `Persistence`, `Infrastructure` e `Presentation` não
  adquiriram dependência do Windows. `CA1416` está configurado como erro pelo mesmo motivo.
- Armadilhas descobertas na prática e documentadas em `CLAUDE.md`: o pacote agregador do
  SQLite anulando o SQLCipher em silêncio, `Cache=Shared` derrubando o processo com falha
  nativa quando combinado com WAL, e o conjunto de restrições do `x:Bind` (nulo em
  `TextBox`, `double` no `NumberBox`, um `ContentDialog` por vez).
