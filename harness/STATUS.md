# STATUS.md — Sintek.Mail

> Última atualização: 2026-08-04

---

## Fase atual

**Fase 1 — Fundação: concluída.** Ver `docs/roadmap.md` para as fases 2 a 9.

## Marco atual

Solution compila e **169 testes passam** nas quatro camadas multiplataforma, verificados em
Linux com .NET 10.0.110. PR #1 aberto.

> **Atenção:** houve troca de implementação em 04/08/2026 (ver `DECISIONS.md`, D-007). O que
> este arquivo descrevia antes — 54 erros de compilação em aberto — pertencia à versão
> anterior, que foi substituída. As decisões D-001 a D-006 seguem válidas: a versão atual
> chegou às mesmas conclusões de forma independente.

## O que existe

- [x] Especificação e plano (`spec/`)
- [x] Harness de memória (`AGENTS.md` + `harness/`)
- [x] `Sintek.Mail.sln` com 6 projetos de src e 4 de teste, mais o filtro
      `Sintek.Mail.CrossPlatform.slnf` para o que compila fora do Windows
- [x] **Domain** — sem dependência de projeto ou pacote. VOs `EmailAddress`/`EmailDomain`,
      entidades, `DomainMembershipEvaluator` (os cinco modos) e `FolderRestrictionResolver`
      (herança pela árvore de pastas). **130 testes**
- [x] **Application** — portas, `MoveMessageHandler` (as quatro ações configuráveis),
      `AddAccountHandler`, `ChangeDomainNameHandler` (análise separada da aplicação),
      `SetFolderRestrictionHandler`. **11 testes**
- [x] **Persistence** — EF Core 10, SQLCipher, mapeamentos de todas as entidades, migração
      inicial e migração do FTS5 com gatilhos. **9 testes**, incluindo a leitura dos bytes
      crus do arquivo para provar a criptografia
- [x] **Infrastructure** — MailKit IMAP/SMTP, sanitizador de HTML, provedores OAuth
      Microsoft e Google, autodiscovery, `OutboxProcessor`. **19 testes**
- [x] **Infrastructure.Windows** — Credential Manager via CsWin32; chave do banco
- [x] **App WinUI 3** — árvore de navegação hierárquica, lista de mensagens, painel de
      leitura com WebView2 travado; modos MSIX e unpackaged
- [x] **CI** — job Linux (núcleo) e job Windows (solution completa + MSIX)
- [x] **Docs** — `docs/decisoes-arquiteturais.md`, `docs/modelo-de-dados.md`,
      `docs/roadmap.md`, `CLAUDE.md`

## Próximos passos

1. **Aguardar o CI Windows do PR #1.** Será o primeiro build real da camada WinUI 3 e de
   `Infrastructure.Windows` — nada disso compila no container Linux onde o núcleo foi
   desenvolvido. Erros de markup XAML e de P/Invoke do CsWin32 aparecem aí.
2. **Fase 2 — Contas:** assistente de configuração, fluxo interativo de OAuth, autodiscovery
   por SRV/ISPDB.
3. **Fase 3 — Sincronização:** espelhamento de pastas, sincronização incremental com
   CONDSTORE/QRESYNC, IDLE, e os tipos de operação da fila ainda não tratados
   (`SendMessage`, `AppendDraft`, operações de pasta).

## Bloqueios

Nenhum bloqueio de código.

Duas dependências externas, ambas de implantação e não de desenvolvimento: os Client IDs de
OAuth (Entra ID e Google Cloud) e o certificado de assinatura do MSIX. Sem os Client IDs, os
provedores ficam implementados e desativados, e a interface os apresenta como "não
configurados" em vez de falhar na autenticação.

## Notas

- O núcleo é desenvolvido e testado em Linux de propósito: é o que verifica mecanicamente
  que `Domain`, `Application`, `Persistence` e `Infrastructure` não adquiriram dependência
  do Windows. `CA1416` está configurado como erro pelo mesmo motivo.
- Duas armadilhas descobertas na prática e documentadas em `CLAUDE.md`: o pacote agregador
  do SQLite anulando o SQLCipher em silêncio, e `Cache=Shared` derrubando o processo com
  falha nativa quando combinado com WAL.
