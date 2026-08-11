# Análise de Código — Sintek.Mail

**Data:** 11/08/2026 · **Escopo:** todo o código em `src/` e `tests/` no branch `claude/caixa-postal-code-analysis-hknmn7`

> **Como esta análise foi feita:** não foi possível compilar localmente — o container não tem SDK .NET e o proxy nega `builds.dotnet.microsoft.com` (403 no CONNECT). A leitura do código é estática. **Mas o build real foi recuperado do CI** (run [31237494762](https://github.com/sintektec/caixa-email/actions/runs/31237494762), push em `main`, 08/08/2026), o que confirma parte dos achados com saída de compilador de verdade — ver seção 1.1.

---

## 1. Veredito

O `STATUS.md` afirma que Domain, Application, Persistence, Infrastructure e App estão "completos" e que o único trabalho restante são 54 erros de compilação. Isso está errado nos dois sentidos.

**Os 54 erros descritos já não existem.** Das quatro causas listadas no STATUS.md, três já foram corrigidas no código atual: o pacote `Microsoft.Extensions.DependencyInjection` está presente nos dois csproj citados, o `SqlCipherInterceptor` usa `ConnectionEndEventData` (o tipo correto), e a ambiguidade de `IMailTransport` está resolvida por qualificação completa em `MailKitTransport.cs:13`. O que resta são falhas de asserção nos testes, não erros de compilação — e são outras.

**As camadas não estão completas.** Três das portas centrais estão registradas no contêiner de DI e injetadas em lugar nenhum: `ICredentialStore`, `IOAuthProvider` e `IHtmlSanitizer`. Nenhuma linha de produção chama `Sanitize()`. Nenhuma linha lê uma credencial. O `GetPasswordAsync` do transporte retorna string vazia. O resultado é que **nenhuma conta consegue autenticar em nenhum servidor** e **todo HTML de e-mail chega cru no `WebView2`**.

E o diferencial do produto — a validação por Diretório de Domínio — **não funciona para nenhuma mensagem sincronizada**, por dois defeitos independentes no mapeamento IMAP (B7).

Ordem de trabalho recomendada: B0–B8 antes de qualquer coisa, depois A1–A11. Os itens M são dívida real, mas não bloqueiam.

### 1.1 O que o build real diz

O CI **existe e roda** (76 execuções). O último build de `main` é de 08/08/2026 e **falhou com 2 erros — não 54**:

```
error CS0103: The name 'InitializeComponent' does not exist in the current context
    src/Sintek.Mail.App/MainPage.xaml.cs(12,9)

error MSB3073: The command "...microsoft.windowsappsdk\1.6.240923002\...\net472\XamlCompiler.exe
    obj\x86\Release\net10.0-windows10.0.19041.0\input.json ..." exited with code 1
    src/Sintek.Mail.App/Sintek.Mail.App.csproj

Build FAILED.    21 Warning(s)    2 Error(s)
```

Três fatos importantes saem daí:

1. **Os dois erros são o mesmo problema.** `InitializeComponent` é gerado pelo compilador de XAML; quando o `XamlCompiler.exe` morre, o partial não existe e o C# reclama em seguida. Há um único ponto de falha: a compilação de XAML do projeto App (ver B0).
2. **Todos os outros 5 projetos de `src/` e os 8 de `tests/` compilam.** Domain, Application, Infrastructure, Infrastructure.Windows e Persistence geram DLL sem erro. Não existem erros de compilação nos testes — os problemas deles são de asserção (A9/A10), e nunca foram executados porque o passo `dotnet test` usa `--no-build` e o job morre antes.
3. **O CI está vermelho em `main` desde pelo menos 08/08 e ninguém agiu.** Não é falta de CI; é CI ignorado (P3).

O log também traz 21 warnings, e alguns são achados por si (ver B0 e M2).

## 2. Bloqueadores

### B0 — O build quebra: Windows App SDK 1.6 contra .NET 10

`src/Sintek.Mail.App/Sintek.Mail.App.csproj:19` — `Microsoft.WindowsAppSDK 1.6.240923002` com `TargetFramework=net10.0-windows10.0.19041.0`.

O `XamlCompiler.exe` que vem no 1.6 é distribuído em `tools\net472\` e é anterior ao suporte a .NET 10; ele sai com código 1 e derruba o build do App inteiro. D-001 registrou explicitamente **2.3.1** como consequência da escolha do .NET 10, e o `PARECER-VALIDACAO.md` §2.4 confirmou essa numeração contra a fonte oficial. O csproj ficou em 1.6.

Duas outras coisas contribuem para a falha de XAML e precisam ser corrigidas junto — qual delas o compilador reclama primeiro só um build local diz:

- `MainWindow.xaml:32,44` usam `x:DataType="local:DomainDirectoryDto"` e `local:MessageDto`, mas o elemento raiz (linhas 3-6) declara apenas `xmlns` e `xmlns:x`. **Não existe `xmlns:local`.** E os DTOs estão em `Sintek.Mail.Application.DTOs`, outro assembly.
- `MainPage.xaml` / `MainPageViewModel` são resto de template (`"Hello, WinUI!"`, contador com botão), não são usados por nada, e são exatamente o arquivo que aparece no `CS0103`. Apagar os dois resolve o erro e remove código morto (M7).

**Correção:** subir para Windows App SDK 2.3.1, declarar `xmlns:local` (ou o namespace correto dos DTOs) em `MainWindow.xaml`, e apagar `MainPage.*` e `MainPageViewModel`.

### B1 — A chave de criptografia do banco é sorteada a cada inicialização

`src/Sintek.Mail.App/App.xaml.cs:54-58`

```csharp
private static string GetOrCreateEncryptionKey()
{
    // TODO: Store in Credential Manager or DPAPI
    return Convert.ToBase64String(Guid.NewGuid().ToByteArray());
}
```

O nome diz "GetOrCreate", mas o método nunca busca e nunca guarda. Cada `App()` gera uma chave nova. Se o SQLCipher estivesse de fato ativo, o banco ficaria irrecuperável no segundo start — **perda total de dados a cada reinicialização**, silenciosa.

Além disso, `Guid.NewGuid()` não é material de chave: são 122 bits úteis com garantias de unicidade, não de imprevisibilidade criptográfica.

**Correção:** `RandomNumberGenerator.GetBytes(32)` na primeira execução, persistida via `ICredentialStore` — que já existe, já está implementado e já está registrado no DI (`AddWindowsInfrastructure`). É literalmente para isso que D-003 criou o projeto `Infrastructure.Windows`.

### B2 — Não há garantia de que o banco esteja criptografado, e a falha é silenciosa

`src/Sintek.Mail.Persistence/Sintek.Mail.Persistence.csproj:10-12`

O projeto referencia `Microsoft.EntityFrameworkCore.Sqlite` **e** `SQLitePCLRaw.bundle_e_sqlcipher`. O primeiro traz `SQLitePCLRaw.bundle_e_sqlite3` como dependência transitiva — o bundle **sem** criptografia. Com dois bundles na mesma aplicação, qual provider registra primeiro é indeterminado. Se vencer o `e_sqlite3`, o `PRAGMA key` vira um pragma desconhecido, e o SQLite **ignora pragmas desconhecidos sem erro**. Banco em texto plano, zero sinal de que algo deu errado.

**Isto não é hipótese — o log do CI mostra o bundle errado sendo restaurado:**

```
src/Sintek.Mail.Persistence/Sintek.Mail.Persistence.csproj : warning NU1903:
Package 'SQLitePCLRaw.lib.e_sqlite3' 2.1.11 has a known high severity vulnerability
```

`lib.e_sqlite3` é a biblioteca nativa **sem** criptografia. Ela está entrando em `Sintek.Mail.Persistence` (e em `App`, e em `PersistenceTests`) exatamente pela dependência transitiva descrita acima. De quebra, tem vulnerabilidade de severidade **alta** conhecida ([GHSA-2m69-gcr7-jv3q](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)).

Agravantes:
- Não existe nenhuma chamada a `SQLitePCL.Batteries_V2.Init()` no código (`grep -rn "Batteries" src/` não retorna nada), apesar de D-004 exigir explicitamente.
- Nada em lugar nenhum verifica que a criptografia funcionou.
- **O `.analysis/PARECER-VALIDACAO.md` §2.3 avisou exatamente sobre isso**, antes da implementação, com a correção já escrita: usar `Microsoft.EntityFrameworkCore.Sqlite.Core`. O aviso foi ignorado.

**Correção:** trocar para `Microsoft.EntityFrameworkCore.Sqlite.Core` (não puxa bundle), chamar `Batteries_V2.Init()` explicitamente no startup antes do primeiro uso, e adicionar um teste que abre o arquivo `.db` gerado e afirma que os nomes de tabela **não** aparecem em texto plano nos primeiros bytes. Sem esse teste, essa regressão volta sem ninguém notar.

### B3 — `PRAGMA key` montado por interpolação de string

`src/Sintek.Mail.Persistence/Interceptors/SqlCipherInterceptor.cs:26` e `:38`

```csharp
command.CommandText = $"PRAGMA key = '{_encryptionKey}';";
```

Uma aspa simples na chave quebra o comando ou injeta SQL. Hoje a chave é Base64 e não contém `'`, mas a correção de B1 não deveria depender silenciosamente desse acidente. Use o keyword `Password=` da connection string do `Microsoft.Data.Sqlite` (que faz o pragma corretamente), ou escape via `quote()`.

### B4 — Nenhuma senha é enviada ao servidor

`src/Sintek.Mail.Infrastructure/Transport/MailKitTransport.cs:175-181`

```csharp
private static async Task<string> GetPasswordAsync(Account account, CancellationToken ct)
{
    // TODO: Integrate with ICredentialStore
    await Task.CompletedTask;
    return string.Empty;
}
```

Todo `AuthenticateAsync` de IMAP e SMTP recebe senha vazia. `TestConnectionAsync` engole a exceção e retorna `false`; `SyncAccountHandler` então marca a conta como `Error` com "Connection failed". O caminho IMAP/SMTP inteiro está morto, e falha de um jeito que parece problema de rede.

`ICredentialStore` está implementado e registrado. Falta injetá-lo no `MailKitTransport` (que hoje não tem construtor) e usar `account.CredentialKey`.

### B5 — OAuth nunca é exercido, e o registro no DI está errado

`src/Sintek.Mail.Infrastructure/DependencyInjection.cs:16-24`

```csharp
if (!string.IsNullOrEmpty(msalClientId))
    services.AddSingleton<IOAuthProvider>(new MsalOAuthProvider(msalClientId));

if (!string.IsNullOrEmpty(googleClientId) && !string.IsNullOrEmpty(googleClientSecret))
    services.AddSingleton<IOAuthProvider>(new GoogleOAuthProvider(googleClientId, googleClientSecret));
```

Dois registros para o mesmo tipo de serviço: resolver `IOAuthProvider` devolve **só o último** (Google). O provider Microsoft fica inalcançável.

Isso é acadêmico, porque o transporte nunca resolve `IOAuthProvider` de qualquer forma. `ConnectAndAuthenticateAsync` sempre usa SASL usuário/senha, nunca `XOAUTH2`. D-006 ("suportar os três modos") não está implementado — está apenas escrito.

**Correção:** `IEnumerable<IOAuthProvider>` (ou keyed services do .NET 8+) selecionado por `ProviderType`, e um ramo em `ConnectAndAuthenticateAsync` que use `SaslMechanismOAuth2` quando `account.AuthenticationType != Basic`.

### B6 — XSS: o HTML nunca é sanitizado, e o sanitizador é inseguro

Dois problemas que se somam.

**O sanitizador não é chamado.** `grep -rn "Sanitize" src/` mostra a interface, a implementação e o registro no DI — nenhum consumidor. `MessageBody.SanitizedHtml` nunca é escrito. `FetchMessageBodyAsync` (`MailKitTransport.cs:78-83`) guarda `mimeMessage.HtmlBody` cru. E `MainWindow.xaml:53` renderiza o painel de leitura num `WebView2`. HTML controlado pelo remetente, direto no motor de renderização.

**E o sanitizador, quando for chamado, não protege.** `HtmlSanitizerService` é regex sobre HTML — o anti-padrão canônico. A lista de atributos perigosos tem 13 entradas e o HTML tem centenas de handlers: `onmouseenter`, `onpointerover`, `onanimationstart`, `ontoggle`, `onscroll`, `oninput`, `onplay`, `oncopy` e dezenas de outros passam intactos. Não há `svg`, `style` nem `math` na lista de tags. O corte de `javascript:` cai com `java&#9;script:`, `jav&#x0A;ascript:` ou `javascript&colon;`. E remover a substring literal `data:` de qualquer lugar do documento corrompe texto legítimo do corpo.

**A dependência certa já está no csproj e não é usada.** `HtmlSanitizer` (Ganss.Xss) 9.0.886 está em `Sintek.Mail.Infrastructure.csproj:10` e `grep -rn "Ganss" src/` não retorna nada.

Os 6 testes em `HtmlSanitizerServiceTests` passam — eles testam exatamente os três casos para os quais a implementação foi escrita. É confiança falsa, não cobertura.

**Correção:** apagar a classe de regex, envolver `Ganss.Xss.HtmlSanitizer`, chamar no momento em que o corpo é baixado, gravar em `SanitizedHtml`, e renderizar **apenas** `SanitizedHtml` no `WebView2`.

### B7 — A validação por domínio não funciona para nenhuma mensagem sincronizada

Este é o diferencial obrigatório do produto (CONTEXT.md), e ele está quebrado por dois defeitos independentes no mesmo método.

**(a) `FromAddress` guarda o endereço formatado, não o endereço.**

`src/Sintek.Mail.Infrastructure/Transport/MailKitTransport.cs:194`

```csharp
FromAddress = envelope?.From?.FirstOrDefault()?.ToString() ?? string.Empty,
```

`InternetAddress.ToString()` do MimeKit devolve a forma formatada — `"Fulano" <fulano@empresa.com>` — não o endereço nu. Quando existe display name (ou seja, na maior parte do e-mail real), `EmailAddress.Parse` corta no último `@` e produz o domínio `empresa.com>`, com o `>` colado. `Matches("empresa.com")` retorna `false`, sempre.

Correção: `((MailboxAddress)envelope.From[0]).Address`.

**(b) `Message.Addresses` nunca é populado na sincronização.**

`MapToMessage` não cria nenhum `MessageAddress`. Para toda mensagem vinda do servidor, a coleção de To/Cc/Bcc está vazia. Isso derruba `RecipientOnly`, `SenderOrRecipient`, `SenderAndRecipient` e `AnyParticipant` — quatro dos cinco `ValidationMode` não têm dado nenhum para avaliar. O quinto, `SenderOnly`, cai no defeito (a).

Resultado combinado: **os cinco modos de validação falham para mensagens reais.** `MessageSummaryItems.Envelope` já traz `To`/`Cc`; falta mapear.

### B8 — Não existe esquema de banco de dados

Não há pasta `Migrations/` em lugar nenhum, e `grep -rn "EnsureCreated\|Migrate" src/` não retorna nada. A aplicação abre um `MailDbContext` apontando para um arquivo que não tem tabela alguma. A primeira query lança.

**Correção:** gerar a migration inicial (`dotnet ef migrations add Initial -p src/Sintek.Mail.Persistence`) e chamar `MigrateAsync()` no startup.

---

## 3. Bugs de correção

### A1 — `MoveMessageHandler` grava a pasta de origem errada no outbox

`src/Sintek.Mail.Application/Handlers/MoveMessageHandler.cs:77` e `:93`

```csharp
message.FolderId = targetFolder.Id;      // linha 77
// ...
sourceFolderId = message.FolderId        // linha 93 — já é o destino
```

A mutação acontece antes da serialização do payload, então `sourceFolderId == targetFolderId`. O IMAP MOVE precisa da origem real; a operação é insincronizável. Capture `var sourceFolderId = message.FolderId;` antes da linha 77.

### A2 — A fila offline nunca é drenada, e fica envenenada

`src/Sintek.Mail.Application/Handlers/SyncAccountHandler.cs:84-90`

```csharp
foreach (var op in pendingOps)
{
    // Process operations (simplified — real implementation would use Polly retry)
    op.Status = OutboxOperationStatus.Processing;
    await _repository.UpdateOutboxOperationAsync(op, ct);
}
```

Nada é executado, completado ou falhado. E como `GetPendingOutboxOperationsAsync` filtra por `Status == Pending`, toda operação fica **permanentemente presa em `Processing`** depois do primeiro sync — nunca mais é selecionada por ninguém. Nenhuma mensagem é enviada, movida ou marcada no servidor.

O princípio "offline-first" do CONTEXT.md não está apenas por implementar: o código atual destrói ativamente a fila.

### A3 — `ChangeDomainNameHandler`: auditoria com valor errado e `ConfirmChanges` inócuo

`src/Sintek.Mail.Application/Handlers/ChangeDomainNameHandler.cs:46` e `:55`

A descrição do log é montada **depois** de `ChangeDomainName()` já ter mutado `DomainName`, então grava `"Domain name changed from 'novo.com' to 'novo.com'"`. O nome antigo — a única informação que a entrada de auditoria existe para preservar — se perde.

Pior: quando `ConfirmChanges = true` e há contas incompatíveis, o fluxo cai direto em `ChangeDomainName()`, que chama `GetIncompatibleAccounts` de novo e **lança `DomainMismatchException`**. Confirmar não faz nada além de trocar um retorno estruturado por uma exceção. É preciso decidir o que "confirmar" significa (mover as contas? desativá-las?) e implementar.

### A4 — Restrição herdada da pasta-pai não é aplicada

`FolderDomainValidator.GetEffectiveDomainRestriction` existe, está testado, e **nenhum código de produção o chama**. Tanto `MoveMessageHandler.cs:40` quanto `DomainMembershipEvaluator.ValidateMessage` olham apenas o `IsDomainRestricted` da própria pasta. Mover uma mensagem para uma subpasta de uma pasta restrita passa sem validação — o oposto da regra de herança da spec.

Mesmo se fosse chamado, não funcionaria: `MailRepository.GetFoldersByAccountAsync` não faz `Include(f => f.ParentFolder)` e não há lazy loading configurado, então a subida da hierarquia pararia no primeiro pai não carregado e devolveria `null` — silenciosamente, como "sem restrição". A recursão também não tem guarda de ciclo: uma cadeia de pais cíclica derruba o processo com `StackOverflowException`.

### A5 — `SyncAccountHandler` duplica mensagens e ignora pastas novas

`src/Sintek.Mail.Application/Handlers/SyncAccountHandler.cs:47-80`

Quatro defeitos no mesmo método:

1. `localFolders` é lido na linha 47, **antes** de as pastas remotas novas serem adicionadas, e o laço de mensagens da linha 66 itera esse snapshot velho. Pastas recém-descobertas nunca são sincronizadas na execução que as descobre.
2. Toda mensagem trazida vira `AddMessageAsync` sem nenhuma deduplicação por `Uid` ou `MessageId`. Qualquer re-sync duplica tudo. Falta upsert.
3. `Folder.UidValidity` existe na entidade e nunca é lido nem escrito. Sync incremental por UID sem checar UIDVALIDITY devolve lixo silenciosamente depois de um reset no servidor — é justamente para isso que o campo existe no protocolo.
4. `FetchFoldersRecursiveAsync` (`MailKitTransport.cs:243`) usa o `Guid` recém-gerado do pai como `ParentFolderId` dos filhos. Quando o pai já existe localmente com outro `Id`, o filho fica com FK para uma linha inexistente.

### A6 — `DeleteMessageAsync` pode apagar mensagens de terceiros

`src/Sintek.Mail.Infrastructure/Transport/MailKitTransport.cs:123-124`

```csharp
await imapFolder.AddFlagsAsync(new UniqueId((uint)uid), MessageFlags.Deleted, true, ct);
await imapFolder.ExpungeAsync(ct);
```

`ExpungeAsync()` sem argumento expurga a **pasta inteira**: qualquer outra mensagem marcada com `\Deleted` por outro cliente (webmail, celular) é apagada em definitivo junto. Use a sobrecarga com `uids` (UIDPLUS / `UidExpunge`).

### A7 — Backoff exponencial decorativo, e com estouro

`src/Sintek.Mail.Persistence/Repositories/SyncQueue.cs:58`

```csharp
op.NextAttemptAt = DateTime.UtcNow.AddMinutes(Math.Pow(2, op.AttemptCount));
```

`DequeueAsync` nunca filtra por `NextAttemptAt`, e operações `Failed` nunca voltam para `Pending` — só o `RetryFailedAsync` manual as recupera. Ou seja, não há retry automático nenhum; o campo é enfeite.

E sem teto no expoente, passando de ~40 tentativas o `AddMinutes` estoura o range de `DateTime` e lança `ArgumentOutOfRangeException` — dentro do tratamento de erro.

### A8 — `SyncQueue` nunca persiste

`ISyncQueue` não tem método de unidade de trabalho. `CompleteAsync`, `FailAsync` e `RetryFailedAsync` mutam entidades rastreadas e dependem de alguém chamar `IMailRepository.SaveChangesAsync` no mesmo escopo. Chamados isoladamente, são no-ops silenciosos. O acoplamento implícito entre as duas portas pelo `DbContext` compartilhado deveria ser explícito.

### A9 — `EmailDomain.Parse` não valida domínio nenhum

`src/Sintek.Mail.Domain/ValueObjects/EmailDomain.cs:17-31`

As únicas rejeições são vazio e presença de `@`. Passam como domínios válidos: `"invalid"`, `".com"`, `"example."`, `"my domain.com"` (com espaço no meio). Dá para criar um Diretório de Domínio chamado `invalid`.

Três testes existentes afirmam que esses casos deveriam lançar. Eles falham hoje.

### A10 — Testes esperam `ArgumentException`, o código lança `DomainException`

`DomainException` deriva de `Exception`, não de `ArgumentException`. Falham por incompatibilidade de tipo:

- `EmailAddressTests.Parse_InvalidEmail_ThrowsException` (5 casos)
- `EmailDomainTests.Parse_InvalidDomain_ThrowsException` (3 casos)
- `CreateDomainDirectoryHandlerTests.HandleAsync_InvalidDomain_Throws`

Parte deles falha duas vezes: `"user@.com"`, `".com"`, `"example."` e `"invalid"` não lançam absolutamente nada (ver A9). Decidir de que lado corrigir — derivar as exceções de domínio de `ArgumentException`, ou ajustar as asserções — e então corrigir A9.

Correlato: `EmailAddress.TryParse` (`EmailAddress.cs:41-53`) captura apenas `InvalidEmailAddressException` e deixa `InvalidEmailDomainException` escapar. Um `TryParse` que lança é uma armadilha; capture a base `DomainException`.

Nenhum desses testes jamais rodou: o job do CI morre no `dotnet build` e o passo `dotnet test --no-build` nunca chega a executar.

---

## 4. Dívida relevante

**M1 — Oito projetos de teste, quatro vazios.** `tests/Sintek.Mail.{Domain,Application,Persistence,Infrastructure}.Tests` (com ponto) são stubs de `dotnet new xunit` com um `Test1` vazio; `tests/Sintek.Mail.{...}Tests` (sem ponto) têm os testes reais. Duas convenções de nome, dois conjuntos de versão de xunit/Test.Sdk, e `AwesomeAssertions` declarado **só nos vazios** — os testes reais usam `Assert` puro, contrariando D-005. Apagar um dos conjuntos.

**M2 — As versões de pacote contradizem o plano validado, e 5 delas têm vulnerabilidade conhecida.** O `PARECER-VALIDACAO.md` §2.2 verificou versões contra a api.nuget.org; o código usa outras, mais antigas, em todos os casos:

| Pacote | Plano validado | No código | Aviso no CI |
|---|---|---|---|
| Microsoft.WindowsAppSDK | 2.3.1 | **1.6.240923002** | quebra o build (B0) |
| MailKit | 4.17.0 | 4.14.1 | NU1902 moderada |
| MimeKit | 4.17.0 | 4.14.0 (transitivo) | NU1902 moderada |
| HtmlSanitizer | 9.1.97x | 9.0.886 | NU1902 moderada |
| AngleSharp | — | 0.17.1 (transitivo) | NU1902 moderada |
| SQLitePCLRaw.lib.e_sqlite3 | não deveria existir | 2.1.11 (transitivo) | **NU1903 alta** |
| CommunityToolkit.Mvvm | 8.4.2 | 8.2.2 | — |
| Microsoft.Identity.Client | 4.87.0 | 4.78.0 | — |
| Google.Apis.Auth | 1.75.0 | 1.71.0 | — |
| EF Core Sqlite | 10.0.10 | 10.0.0 | — |

O build de `main` emite 21 warnings, a maioria deles NU1902/NU1903. O `lib.e_sqlite3` acumula os dois problemas: severidade alta **e** é a prova de que o bundle sem criptografia está entrando no projeto (B2).

Dentro do mesmo `Persistence.csproj` convivem `Microsoft.Data.Sqlite.Core 10.0.10` e `EntityFrameworkCore.Sqlite 10.0.0`. E `PersistenceTests` usa `EntityFrameworkCore.InMemory 9.0.0` contra EF Core 10 na camada testada. Não há `Directory.Packages.props` para manter nada alinhado, nem `NuGetAudit` tratado como erro para que esses avisos parem de ser ignorados.

**M3 — `Update()` em entidade já rastreada.** `MailRepository` e `SyncQueue` chamam `_context.X.Update(entity)` em entidades vindas do mesmo contexto. Isso marca **todas** as propriedades (e o grafo inteiro) como modificadas, anula o change tracking e aumenta a janela de lost update.

**M4 — Uma conexão IMAP por operação.** Todo método do transporte faz `new ImapClient()`, conecta, autentica e desconecta. Um sync de N pastas são N handshakes TLS+auth completos; cada marcação de lido/favorito abre a sua. Provedores aplicam rate limit nisso. Precisa de conexão persistente por conta.

**M5 — `FetchMessagesAsync` sem paginação.** Com `lastSeenUid == null` dispara `SearchQuery.All` e carrega os envelopes da pasta inteira num `List<Message>` em memória.

**M6 — Erros de rede em texto livre no banco.** `account.LastSyncError = ex.Message` e `OutboxOperation.LastError` guardam mensagens de exceção cruas, que podem carregar resposta de servidor e endereços — o CONTEXT.md diz que logs nunca registram conteúdo sigiloso. O `catch (Exception)` genérico também engole `OperationCanceledException` e registra cancelamento como falha de sincronização.

**M7 — Restos de template em produção, um deles quebrando o build.** `MainPage.xaml` + `MainPageViewModel` ("Hello, WinUI!", contador com botão) não são usados por nada e são exatamente o arquivo do `CS0103` (ver B0). Além deles: `Class1.cs` em Domain, Application, Infrastructure e Persistence; `Package.appxmanifest` ainda com `Publisher="CN=AppPublisher"`; e `build-output.txt`, `dotnet-path.txt` (com caminho `C:\Program Files\...`) e `sdks.txt` vazio versionados na raiz.

**M8 — Reparse de domínio por endereço.** `DomainMembershipEvaluator.IsAddressInDomain` chama `EmailDomain.Parse(_domain.DomainName)` a cada invocação, dentro de laços por endereço, e reparseia todos os aliases junto. Parse uma vez no construtor. Os métodos privados `Evaluate*` também recebem `targetDomain` e `allowSubdomains` e não usam nenhum dos dois.

**M9 — `CredentialManagerStore`: vazamento no erro, nulabilidade e desvio de D-003.** `Marshal.FreeCoTaskMem` (`CredentialManagerStore.cs:32`) está fora de `finally`: se `CredWrite` falhar e lançar, vaza o buffer não gerenciado — que contém o segredo em texto plano. O build também acusa `warning CS8619` em `CredentialManagerStore.cs(47,20)` (`Task<string>` devolvido onde se espera `Task<string?>`), sinal de que a anulabilidade do retorno de `Marshal.PtrToStringUni` não foi tratada. Além disso, D-003 decidiu CsWin32 e a implementação é P/Invoke manual, sem que DECISIONS.md fosse atualizado. O projeto ainda tem `TargetFramework=net10.0` em vez de `net10.0-windows`, então o analisador de plataforma não consegue apontar chamadas Windows-only.

**M10 — A UI é uma casca.** Todos os comandos de ViewModel são TODO. `LoadDomainsCommand` não é disparado por nada — nem `Loaded`, nem construtor — então a lista de domínios nunca é preenchida. `AddAccountHandler` está registrado no DI e nenhum caminho de UI chega nele. `MainViewModel.LoadDomainsAsync` ainda mapeia `AccountCount` como `0` fixo.

**M11 — `SendMessageHandler` não valida nem normaliza endereços.** Reimplementa extração de domínio num `ExtractDomain` local (último `@` + `ToLowerInvariant`) em vez de usar os VOs `EmailAddress`/`EmailDomain`, aceita destinatários sem validação, nunca cria o `MessageAddress` de `From`, e não confere o remetente contra o Diretório de Domínio da conta — o diferencial do produto não é aplicado no envio. Nada nunca tira a mensagem de Rascunhos nem limpa `IsDraft`.

---

## 5. Processo

**P1 — `STATUS.md` desinforma.** Afirma "Build falhou com 54 erros" e lista quatro causas, três já corrigidas no código. Declara todas as camadas "completas" enquanto `Sanitize`, `ICredentialStore` e `IOAuthProvider` não têm consumidor. Não menciona nenhum dos B1–B8. E cita `Sintek.Mail.sln` quando o repositório tem `Sintek.Mail.slnx`. Alguém lendo esse arquivo para retomar o trabalho começa pelo lugar errado.

**P2 — O hook de bootstrap de skills estava quebrado — por isso `/claude-doctor` não existia. ✅ CORRIGIDO em `main` (105819a).**

O diagnóstico original: `.claude/settings.json` registrava **o mesmo hook SessionStart duas vezes** (dois blocos `matcher: ""` idênticos chamando `bootstrap-skills.sh`). As duas instâncias rodavam concorrentes e disputavam o mesmo diretório de clone. O log mostrava as duas falhando de formas diferentes na mesma corrida:

```
.git/hooks/: No such file or directory                  (instância A)
fatal: cannot copy '.../templates/description' ... File exists   (instância B)
```

E o script não limpava clone parcial: depois da primeira falha, `$CLONE` existia sem `.git`, então toda execução seguinte voltava pelo ramo `git clone` e falhava com "already exists and is not an empty directory", para sempre. As duas instâncias também faziam `exec > >(tee "$LOG")` no mesmo arquivo, truncando o próprio diagnóstico.

**A correção em `main` fecha os quatro pontos**, e é mais completa do que o que estava recomendado aqui:

| Defeito | Correção em 105819a |
|---|---|
| Hook duplicado | `settings.json` com uma única entrada `SessionStart` |
| Instâncias concorrentes | lock por `mkdir` atômico, com quebra de lock órfão decidida por vivacidade do dono (`kill -0` no PID) e TTL só como rede de segurança |
| Clone parcial auto-perpetuante | `clone_ok()` valida o clone e, se inválido, `rm -rf` + re-clone; o clone vai para `$CLONE.tmp.$$` e só então é movido, então o caminho final nunca contém um clone pela metade |
| Log truncado pelo `tee` | `log()` append-only, com rotação em 256 KiB |

Vale destacar um acerto que esta análise não tinha previsto: `clone_ok()` não se contenta com "existe `.git`" — exige que `git rev-parse --show-toplevel` devolva exatamente o caminho do clone. Sem isso, um `.git` parcial dentro de um projeto que é ele próprio um repositório git faria a descoberta de repositório **subir na árvore**, e o `git pull` seguinte atualizaria o repositório do usuário em vez do catálogo. Era um modo de falha pior do que o que estava sendo consertado.

**P3 — O CI roda, está vermelho, e ninguém age.** São 76 execuções registradas. O build de `main` falha desde pelo menos 08/08/2026 com os 2 erros da seção 1.1, e nenhum commit desde então tentou corrigi-los. O problema não é falta de sinal — o sinal existe, é confiável, aponta o arquivo e a linha, e está sendo ignorado. Enquanto isso o `STATUS.md` seguia repetindo um número ("54 erros") de uma execução manual antiga que o CI já tinha contradito.

Três ajustes que valem no `ci.yml`:
- `workflow_dispatch` e gatilho em `claude/**`, para não depender de PR aberto para ter feedback.
- `dotnet test` com `--no-build` faz o passo de teste nunca rodar quando o build falha. Como o App é o único projeto que quebra, vale separar o job: `dotnet test` sobre os projetos de teste (que compilam hoje) daria sinal real sobre A9/A10 mesmo com o App vermelho.
- `<TreatWarningsAsErrors>` para NU1902/NU1903, ou `dotnet list package --vulnerable --include-transitive` como passo dedicado — os 5 pacotes vulneráveis de M2 já aparecem no log e passam batido no meio de 21 warnings.

---

## 6. O que está bom

Para não passar a impressão errada: a modelagem de domínio é sólida. A separação Clean Architecture está respeitada de verdade — `Domain` não referencia nada, as portas estão na Application, e `Infrastructure.Windows` isola de fato o código Windows-only. `EmailDomain.IsSubdomainOf` trata corretamente o caso `Value == parent.Value` (retorna `false`), que é o erro clássico dessa função. `DomainDirectory` protege `DomainName` com setter privado e obriga a passar por `ChangeDomainName`. As `Configurations` do EF são detalhadas e os índices (`MessageAddresses.Domain`, `Messages(FolderId, IsRead)`, `OutboxOperations(AccountId, Status, Sequence)`) são os certos para as consultas que o produto faz. `MessageDomainViolationException.SpecMessage` traz o texto exato da spec.

O problema não é o desenho. É que a fiação entre as camadas — credenciais, OAuth, sanitização, drenagem de fila — não foi feita, e o STATUS.md registra tudo isso como pronto.
