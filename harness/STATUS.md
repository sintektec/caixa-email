# STATUS.md — Sintek.Mail

> Última atualização: 2026-08-05

---

## Fase atual

**Fase 4 — Leitura e composição: em andamento.** Ver `docs/roadmap.md` para as fases 5 a 10.

## Marco atual

**408 testes verdes no núcleo multiplataforma** (169 na fase 1, 272 na 2, 336 na 3).

Da fase 4 já entraram a leitura do veredito do servidor sobre cada mensagem — SPF, DKIM,
DMARC e classificação de spam —, a detecção de remetente disfarçado e toda a montagem de
respostas, encaminhamentos e aviso de anexo esquecido. Falta o download sob demanda de corpo
e anexos e a tela do compositor.

O resto do parágrafo vale para a fase 3: O motor de sincronização inteiro é testável sem servidor: espelhamento de pastas,
sincronização incremental, troca de UIDVALIDITY, reconciliação de exclusões e a política de
agendamento.

Distribuição: Domain 130, Application 95, Infrastructure 61, Presentation 41, Persistence 9.

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
- [x] **Persistence** — EF Core 10, SQLCipher, mapeamentos de todas as entidades, migração
      inicial e migração do FTS5 com gatilhos. **9 testes**, incluindo a leitura dos bytes
      crus do arquivo para provar a criptografia
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
2. **Concluir a fase 4:** download sob demanda de corpo e anexos, tela do compositor e a
   faixa de confiança no painel de leitura.
3. **Fase 5 — Pastas e regras de domínio na interface**, incluindo "Marcar como spam".

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
