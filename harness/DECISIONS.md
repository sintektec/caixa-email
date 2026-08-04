# DECISIONS — Sintek.Mail

> Log append-only de decisões técnicas (ADR-lite). Não reabrir sem evidência nova.

---

## D-001 — .NET 10 LTS em vez de .NET 8 (2026-08-03)

**Status:** aceita

**Decisão:** Usar .NET 10 LTS (`net10.0`; UI `net10.0-windows10.0.19041.0`), apesar de a especificação pedir ".NET 8 ou superior".

**Motivo:** Verificado em 03/08/2026 no índice oficial dotnet/core: .NET 8 e .NET 9 têm ambos EOL em 10/11/2026 (~3 meses depois da decisão). .NET 10 é o único LTS ativo (EOL 14/11/2028). A spec diz "ou superior", então .NET 10 a satisfaz.

**Alternativas rejeitadas:** .NET 8 (EOL iminente); .NET 9 (STS, mesmo EOL do 8).

**Consequências:** EF Core 10, Microsoft.Data.Sqlite 10.0.10; Windows App SDK 2.3.1 (numeração nova, não 1.7).

---

## D-002 — Empacotamento dual: MSIX + unpackaged (2026-08-03)

**Status:** aceita

**Decisão:** O app WinUI 3 será distribuído tanto como MSIX (packaged) quanto como executável unpackaged.

**Motivo:** MSIX dá instalação limpa e identidade de pacote; unpackaged facilita distribuição corporativa e depuração. A escolha do mecanismo de credenciais precisa funcionar nos dois modos.

**Alternativas rejeitadas:** MSIX-only (limita cenários corporativos); unpackaged-only (perde benefícios de identidade/instalação).

**Consequências:** Credenciais via CsWin32 (`CredWrite`/`CredRead`), não `PasswordVault` (que exige identidade de pacote). Ver D-003.

---

## D-003 — Credenciais via CsWin32, não PasswordVault (2026-08-03)

**Status:** aceita

**Decisão:** Windows Credential Manager acessado por P/Invoke via `Microsoft.Windows.CsWin32` (`CredWrite`/`CredRead`).

**Motivo:** Funciona nos modos packaged **e** unpackaged (coerente com D-002). `PasswordVault` (WinRT) exige identidade de pacote.

**Alternativas rejeitadas:** `PasswordVault` (quebra no modo unpackaged).

**Consequências:** Projeto `Sintek.Mail.Infrastructure.Windows` separado; banco guarda apenas `CredentialKey`, nunca segredos.

---

## D-004 — SQLCipher via SQLitePCLRaw.bundle_e_sqlcipher 2.1.11 (2026-08-03)

**Status:** aceita

**Decisão:** Criptografia do banco local com `SQLitePCLRaw.bundle_e_sqlcipher` 2.1.11 + `Microsoft.Data.Sqlite.Core` 10.0.10, com `SQLitePCL.Batteries_V2.Init()`.

**Motivo:** Spec exige SQLCipher. As versões 10.0.10 (Microsoft.Data.Sqlite.Core → SQLitePCLRaw.core 2.1.11) e bundle 2.1.11 estão alinhadas — sem conflito de versão do provider nativo.

**Alternativas rejeitadas:** SEE (pago); SQLite sem criptografia (viola a spec).

**Consequências:** Chave do banco gerada aleatoriamente e guardada no Credential Manager (D-003), nunca em arquivo.

---

## D-005 — AwesomeAssertions em vez de FluentAssertions (2026-08-03)

**Status:** aceita

**Decisão:** Testes usam `AwesomeAssertions` 9.5.0.

**Motivo:** FluentAssertions 8.x mudou para licença paga para uso comercial. AwesomeAssertions é o fork livre que mantém a API.

**Alternativas rejeitadas:** FluentAssertions 8.x (custo/licença); Shouldly (API diferente, sem necessidade).

**Consequências:** Nenhuma restrição prática — API compatível.

---

## D-006 — Autenticação: os três modos (2026-08-03)

**Status:** aceita

**Decisão:** Suportar IMAP/SMTP básico (senha) + OAuth 2.0 Microsoft 365 (MSAL) + OAuth 2.0 Google (Google.Apis.Auth), com ponto de extensão para outros provedores.

**Motivo:** Escopo aprovado pelo usuário — aplicação completa, não MVP.

**Alternativas rejeitadas:** Só senha básica (exclui M365/Gmail modernos); só OAuth (exclui servidores corporativos legados).

**Consequências:** `AuthenticationType` enum em `Accounts`; `OAuthProvider?` opcional; dois pacotes de OAuth.
