# STATUS — Sintek.Mail

> Atualizado em: 2026-08-03

## Fase atual

**F0 — Fundação** (pré-código)

## Último marco concluído

- Especificação unificada e plano de implementação aprovados e commitados em `spec/` (commit `a83581b`)
- Harness criado (este diretório + `AGENTS.md`)

## Próximos passos (em ordem)

1. Criar o esqueleto da solution `Sintek.Mail.sln` (6 projetos em `src/` + 4 de testes em `tests/`)
2. Implementar `Sintek.Mail.Domain` completo (entidades, VOs, `DomainMembershipEvaluator`, validação de contas)
3. Implementar `Sintek.Mail.Application` (portas, DTOs, handlers)
4. Implementar `Sintek.Mail.Persistence` (EF Core + SQLCipher, migrations, FTS5)
5. Implementar `Sintek.Mail.Infrastructure` (MailKit, OAuth, sanitização, sync)
6. Implementar `Sintek.Mail.Infrastructure.Windows` (Credential Manager)
7. Implementar `Sintek.Mail.App` (WinUI 3, shell, ViewModels)
8. Testes + CI (GitHub Actions: ubuntu + windows)

Detalhamento completo das fases F2–F9 em `spec/02-plano-sintek-mail.md` (seção "Entrega desta sessão").

## Bloqueios ativos

- Nenhum.

## Ambiente / restrições conhecidas

- Container de desenvolvimento é **Linux sem .NET SDK** — WinUI 3 não compila aqui; camadas cross-platform (Domain, Application, Persistence, Infrastructure) são compiladas/testadas no container, e a solution inteira é validada por CI em `windows-latest`.
- .NET 8 e 9 têm EOL em 10/11/2026 — por isso o projeto usa **.NET 10 LTS** (ver `DECISIONS.md` D-001).
