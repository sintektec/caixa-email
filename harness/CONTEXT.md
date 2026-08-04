# CONTEXT — Sintek.Mail

## O que é

Cliente de e-mail desktop corporativo, instalável e **nativo para Windows 11**, inspirado no Microsoft Outlook e baseado no Fluent Design System. Repositório: `sintektec/caixa-email`.

## Diferencial obrigatório

Organização rigorosa por **Diretório de Domínio**: `Domínio → Conta de e-mail → Pastas`. Uma conta só pode ser vinculada a um Diretório se o domínio do e-mail for **exatamente igual** ao do Diretório (subdomínios só com opt-in explícito). Pastas podem ser restritas por domínio, com modos de validação configuráveis e herança para subpastas. Ver `spec/01-especificacao-unificada.md` seção 5 e `GLOSSARY.md`.

## Princípios fundamentais

- **Offline-first**: toda ação grava primeiro no SQLite local; sincronização acontece quando há conectividade, via fila persistente (`OutboxOperations`).
- **Segurança**: banco criptografado (SQLCipher), credenciais no Windows Credential Manager, OAuth 2.0 quando suportado, HTML sanitizado, imagens remotas bloqueadas por padrão.

## Stack (decidida — ver DECISIONS.md)

| Camada | Tecnologia |
|---|---|
| Linguagem / Framework | C# 12+ / **.NET 10 LTS** |
| UI | WinUI 3 / Windows App SDK 2.3.1, MVVM (CommunityToolkit.Mvvm) |
| Persistência | SQLite + EF Core 10 + SQLCipher (SQLitePCLRaw.bundle_e_sqlcipher) |
| E-mail | MailKit/MimeKit (IMAP + SMTP) |
| Credenciais | Windows Credential Manager via CsWin32 |
| OAuth | MSAL (Microsoft 365) + Google.Apis.Auth (Gmail) |
| Empacotamento | MSIX + unpackaged (dual) |
| Testes | xunit + AwesomeAssertions (não FluentAssertions 8.x — licença) |

## Arquitetura

Clean Architecture: `Domain ← Application ← {Persistence, Infrastructure} ← App`. `Domain` não referencia nada. `Infrastructure.Windows` separado para isolar APIs Windows-only e permitir build/test no container Linux.

## Documentos-fonte

- **O que construir**: `spec/01-especificacao-unificada.md` (atenção: termina truncado na entidade `Accounts`; o restante do modelo de dados foi projetado no plano)
- **Como construir**: `spec/02-plano-sintek-mail.md` (modelo de dados completo, fases F0–F9, versões de pacotes verificadas)

## Restrições

- Não usar Electron nem web empacotada — comportamento nativo Windows 11 é obrigatório.
- Nenhuma senha ou token no banco de dados — apenas identificadores (`CredentialKey`).
- Logs nunca registram conteúdo sigiloso de mensagens.
