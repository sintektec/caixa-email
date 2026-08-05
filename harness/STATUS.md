# STATUS.md — Sintek.Mail

> Última atualização: 2026-08-05

---

## Fase atual

**Fase 6 — Pesquisa: concluída.** Ver `docs/roadmap.md` para as fases 7 a 10.

## Marco atual

**478 testes verdes no núcleo multiplataforma** (169 → 272 → 336 → 426 → 441 → 478 ao
longo das fases).

A fase 6 entregou a pesquisa completa da seção 6.4. O índice FTS5 foi reconstruído com
*external content* (migração `RebuildSearchIndex`): o modo contentless original não tinha
como indexar corpo, participantes e nomes de anexo, porque apagar uma entrada exige
reapresentar os valores antigos — que vivem em outras tabelas. Agora a tabela física
`MessagesSearch` espelha o texto pesquisável, os gatilhos das tabelas de origem a mantêm
(inclusive no download sob demanda), e `Fts5SearchService` combina MATCH com os filtros
estruturais: conta, pasta, Diretório de Domínio, categoria, intervalo de datas com
normalização de fuso, lida, sinalizador, anexos, importância e status de sincronização.
Pesquisas salvas atualizam pela identidade do nome, e a interface ganhou o flyout de
pesquisa avançada e o modo de resultados no painel central.

Distribuição: Domain 144, Application 165, Infrastructure 85, Presentation 61, Persistence 23.

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
      (herança pela árvore de pastas). **130 testes**
- [x] **Application** — portas, `MoveMessageHandler` (as quatro ações configuráveis), ciclo
      de vida completo de conta (`AddAccount`, `TestAccountConnection`, `UpdateAccount`,
      `RemoveAccount`) e de Diretório de Domínio (`Create`, `Update`, `Remove`,
      `ChangeDomainName`), `SetFolderRestrictionHandler` e o motor de sincronização
      (`SyncAccountHandler`, `FolderMirrorService`, `MessageSyncService`, `SyncSchedule`).
      **95 testes**
- [x] **Persistence** — EF Core 10, SQLCipher, mapeamentos de todas as entidades, migrações
      (inicial, FTS5 e a reconstrução com external content) e `Fts5SearchService`.
      **23 testes**, incluindo a leitura dos bytes crus do arquivo para provar a
      criptografia e a pesquisa completa contra o banco migrado de verdade
- [x] **Infrastructure** — MailKit IMAP/SMTP, sanitizador de HTML, provedores OAuth
      Microsoft e Google, descoberta automática em cinco etapas, serialização MIME,
      `OutboxProcessor` completo e o laço `AccountSyncWorker`. **61 testes**
- [x] **Presentation** — ViewModels multiplataforma: assistente de contas, editor de
      Diretórios de Domínio, lista de contas, fila de sincronização, árvore de navegação,
      lista de mensagens e painel de leitura. **41 testes**
- [x] **Infrastructure.Windows** — Credential Manager via CsWin32; chave do banco
- [x] **App WinUI 3** — janela principal com árvore hierárquica e painel de leitura travado;
      diálogos de assistente de conta, editor de diretório, configurações e fila de
      sincronização; MSIX e unpackaged
- [x] **CI** — job Linux (núcleo) e job Windows (solution completa + MSIX)
- [x] **Docs** — `docs/decisoes-arquiteturais.md`, `docs/modelo-de-dados.md`,
      `docs/roadmap.md`, `CLAUDE.md`

## Próximos passos

1. **Revisar e integrar o PR #1.**
2. **Fase 7 — Automação e filtragem local:** editor e motor de regras, categorias com
   gestão na interface (o filtro de pesquisa por categoria já existe no serviço, à espera
   do seletor), modelos de mensagem, listas de remetentes bloqueados/confiáveis.

As pendências pontuais das fases 4 e 6 foram fechadas: editor rico (WebView2
contenteditable), rascunho automático por período de silêncio e pesquisas salvas na barra
lateral.

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
