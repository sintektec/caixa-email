# STATUS.md — Sintek.Mail

> Última atualização: 2026-08-05

---

## Fase atual

**Fases 1 a 10 concluídas.** O roadmap da especificação está inteiro implementado.

Duas fases novas foram acrescentadas a pedido do usuário e ainda não começaram:
**11 — Contatos e histórico de destinatários** e **12 — Agenda**. Ver `docs/roadmap.md`;
a fase 12 tem um risco a medir antes de começar (fusos IANA com `InvariantGlobalization`).

## Marco atual

**587 testes verdes no núcleo multiplataforma** (169 → 272 → 336 → 426 → 441 → 478 → 485
→ 530 → 535 → 569 → 584 → 587 ao longo das dez fases).

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

Distribuição: Domain 161, Application 222, Infrastructure 94, Presentation 87, Persistence 23.

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

Não há pendência de código em nenhuma das dez fases. O que resta é **pendência humana**, registrada em "Bloqueios": validação manual em Windows 11 e teste
contra servidores IMAP/SMTP reais com Client IDs de OAuth.

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
