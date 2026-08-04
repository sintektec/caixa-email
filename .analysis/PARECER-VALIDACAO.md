# Parecer de Validação — Plano Sintek.Mail
**Data:** 03/08/2026 · **Avaliador:** Cline (com skills: dotnet-architect, architect-review, plan-writing, architecture-decision-records, code-review-checklist)

---

## 1. Veredito geral

| Dimensão | Nota | Status |
|---|---|---|
| Cobertura funcional da spec | 9.5/10 | ✅ Validado |
| Precisão técnica (versões/claims) | 9/10 | ✅ Validado com 2 ressalvas |
| Arquitetura proposta | 9/10 | ✅ Validado |
| Exequibilidade do escopo da sessão | 6/10 | ⚠️ Risco |
| **Geral** | **8.5/10** | **✅ APROVADO com ressalvas** |

---

## 2. Validação TÉCNICA (claims verificados contra fontes oficiais)

### 2.1 Ciclo de vida .NET — ✅ CONFIRMADO (fonte: dotnet/core releases-index.json)
| Claim do plano | Verificado | Resultado |
|---|---|---|
| .NET 8 EOL 10/11/2026 | EOL real: **2026-11-10** | ✅ Exato |
| .NET 9 EOL 10/11/2026 | EOL real: **2026-11-10** (STS) | ✅ Exato |
| .NET 10 LTS, EOL 14/11/2028 | EOL real: **2028-11-14** (LTS) | ✅ Exato |
| Decisão: .NET 10 | Único LTS com suporte > 2 anos | ✅ Correta |

### 2.2 Versões NuGet — ✅ QUASE TUDO CONFIRMADO (fonte: api.nuget.org)
| Pacote | Plano | Real (estável) | Status |
|---|---|---|---|
| Microsoft.WindowsAppSDK | 2.3.1 | **2.3.1** | ✅ |
| EF Core Sqlite / Design | 10.0.10 | **10.0.10** | ✅ |
| Microsoft.Data.Sqlite.Core | 10.0.10 | **10.0.10** | ✅ |
| SQLitePCLRaw.bundle_e_sqlcipher | 2.1.11 | **2.1.11** | ✅ |
| MailKit / MimeKit | 4.17.0 | **4.17.0** | ✅ |
| CommunityToolkit.Mvvm | 8.4.2 | **8.4.2** | ✅ |
| HtmlSanitizer | 9.1.974 | **9.1.982** | ⚠️ patch acima (irrelevante) |
| Microsoft.Identity.Client(.Broker) | 4.87.0 | **4.87.0** | ✅ |
| Google.Apis.Auth | 1.75.0 | **1.75.0** | ✅ |
| Microsoft.Web.WebView2 | 1.0.4078.44 | **1.0.4129.50** | ⚠️ desatualizado (build acima) |
| Microsoft.Windows.CsWin32 | 0.3.298 | **0.3.298** | ✅ |
| Polly | 8.7.0 | **8.7.0** | ✅ |
| xunit | 2.9.3 | **2.9.3** | ✅ |
| AwesomeAssertions | 9.5.0 | **9.5.0** | ✅ |

### 2.3 Alinhamento de dependências SQLCipher — ✅ CONFIRMADO (fonte: nuspec oficial)
- `Microsoft.Data.Sqlite.Core 10.0.10` → depende de `SQLitePCLRaw.core **2.1.11**`
- `SQLitePCLRaw.bundle_e_sqlcipher 2.1.11` → traz `provider.e_sqlcipher 2.1.11` + `lib.e_sqlcipher 2.1.11`
- **Claim do plano ("alinhamento exato, sem conflito") está CORRETO.** ✅
- ⚠️ **Ressalva importante que o plano NÃO mencionou:** `Microsoft.EntityFrameworkCore.Sqlite 10.0.10` depende de `SQLitePCLRaw.bundle_e_sqlite3 2.1.11` (bundle **sem** criptografia). Se o projeto referenciar `EFCore.Sqlite` **e** `bundle_e_sqlcipher`, há **dois bundles** na mesma aplicação — o `SQLitePCL.Batteries_V2.Init()` do bundle errado pode vencer. **Recomendação:** referenciar `Microsoft.EntityFrameworkCore.Sqlite.Core` (que não puxa bundle) + `bundle_e_sqlcipher`, ou garantir `SQLitePCL.Batteries_V2.Init()` explícito antes do primeiro uso. O plano está ciente do Init, mas não do conflito de bundles.

### 2.4 Windows App SDK 2.3.1 — ✅ CONFIRMADO
- Meta-pacote 2.3.1 puxa `WinUI 2.3.0`, `Foundation 2.3.5`, `Runtime 2.3.1`. Numeração 2.x confirmada (plano corrigiu bem o "1.7" da spec).

### 2.5 FluentAssertions vs AwesomeAssertions — ✅ DECISÃO CORRETA
- FluentAssertions 8.x tem licença comercial (Xceed). AwesomeAssertions 9.5.0 é o fork livre mantido. Decisão acertada e bem fundamentada.

---

## 3. Validação FUNCIONAL (cobertura da especificação)

| Seção da spec | Coberto pelo plano? | Observação |
|---|---|---|
| 1. Papel/objetivo (Outlook-like, Fluent, offline-first) | ✅ | |
| 2. Stack obrigatória | ✅ | C# 12 implícito no net10; WinUI 3, MVVM, SQLite, EF Core, SQLCipher, MailKit, Credential Manager, MSIX — todos presentes |
| 3.1 Offline-first | ✅ | Outbox pattern + drain com Polly + CONDSTORE/QRESYNC/IDLE |
| 3.2 Segurança | ✅ | SQLCipher, OAuth2, Credential Manager, bloqueio de imagens remotas, HtmlSanitizer+WebView2, MOTW, logs sem conteúdo sigiloso, confirmações |
| 4. TreeView obrigatório | ✅ | Favoritos / Contas e Diretórios / Domínio→Conta→Pastas, ícones Fluent, contadores |
| 5.1 Conceito Diretório de Domínio | ✅ | |
| 5.2 Validação de contas (tabelas de exemplo) | ✅ | Testes cobrem exatamente as tabelas da spec |
| 5.3 Regras de organização (5 modos de validação) | ✅ | `DomainMembershipEvaluator` + `DomainAliases` + mensagem de bloqueio literal |
| 5.4 Herança de regras | ✅ | Herança, invariante de 1 domínio por pasta, fluxo de alteração de domínio em 2 etapas |
| 6.1 Gestão de contas | ✅ | Fase F2 |
| 6.2 Gestão de mensagens | ✅ | Fases F3/F4/F8 (responder, encaminhar como anexo, agendar, categorias, assinaturas, modelos, confirmação de leitura) |
| 6.3 Pastas e organização | ✅ | F5 (drag & drop com regras de domínio, pasta de pendências) |
| 6.4 Pesquisa e filtros | ✅ | FTS5 + pesquisas salvas (F6) |
| 6.5 Regras automáticas | ✅ | Rules/RuleConditions/RuleActions (F7) |
| 7. Interface/UX | ✅ | Shell, temas, atalhos, acessibilidade |
| 8. Modelo de dados | ✅ | Spec truncada suprida com modelo completo coerente |

**Cobertura funcional: completa.** Nenhum requisito da spec ficou órfão.

---

## 4. Ressalvas e riscos (o que o plano NÃO cobre ou arrisca)

1. **🔴 Conflito de bundles SQLitePCLRaw** (técnico, real) — ver §2.3. Pode fazer a criptografia SQLCipher falhar silenciosamente se o bundle errado inicializar. **Ação:** usar `EFCore.Sqlite.Core` + `bundle_e_sqlcipher` e teste que prove `PRAGMA cipher_version` retorna valor.
2. **🟡 Escopo da sessão superdimensionado** — 10 entregas (solution + 6 projetos + testes + CI) em uma sessão é irreal. Risco de camadas rasas. **Ação:** priorizar Domain + testes (coração) e deixar App WinUI como esqueleto mínimo.
3. **🟡 FTS5 via SQL bruto** — triggers de sincronização são fonte clássica de bugs. **Ação:** testes dedicados de insert/update/delete refletindo no FTS.
4. **🟡 WinUI 3 não validável em Linux** — ~20% da solution só no CI Windows (feedback lento). Mitigação do plano (isolar camadas) é correta.
5. **🟢 Versões patch desatualizadas** (HtmlSanitizer, WebView2) — sem impacto; CPM facilita bump.
6. **🟢 README ainda é do template** — atualizar na primeira entrega.

---

## 5. Conclusão

O plano é **tecnicamente preciso** (claims de versão e ciclo de vida verificados como corretos em fontes oficiais) e **funcionalmente completo** (100% da spec mapeada, incluindo o trecho truncado que foi projetado de forma coerente). As decisões de arquitetura (Clean Architecture, separação `Infrastructure.Windows`, Outbox, MessageAddresses indexado por domínio, CsWin32 dual-packaging, AwesomeAssertions) são **corretas e bem fundamentadas**.

**Recomendação: APROVAR o plano**, condicionando a execução a:
- (a) corrigir a estratégia de bundle SQLCipher (ressalva 1);
- (b) fatiar a entrega da sessão para não sacrificar profundidade do Domain (ressalva 2).
