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

## D-007 — A implementação deste branch substitui a que estava na main (2026-08-04)

**Status:** aceita

**Decisão:** Diante de duas implementações paralelas do mesmo projeto, `src/` e `tests/`
passam a ser os deste branch. Da main foram preservados `spec/`, `AGENTS.md`, `harness/`,
`.analysis/` e `.continue/`.

**Motivo:** Três achados verificáveis na versão anterior:

1. CI vermelho — `MainPage.xaml.cs` chamava `InitializeComponent` sem `MainPage.xaml`
   existir; o XamlCompiler abortava. O `STATUS.md` registrava 54 erros de compilação em
   aberto.
2. **A D-004 estava correta na decisão e furada na implementação.** O `.csproj` de
   Persistence referenciava `Microsoft.EntityFrameworkCore.Sqlite` (o pacote agregador)
   *junto* com `bundle_e_sqlcipher`. O agregador arrasta `bundle_e_sqlite3`, e o log do CI
   confirma `SQLitePCLRaw.lib.e_sqlite3` no grafo. Com o provider sem criptografia
   registrado, o `PRAGMA key` do `SqlCipherInterceptor` vira no-op silencioso: o banco
   abre, grava e lê normalmente — em texto claro. A referência correta é
   `Microsoft.EntityFrameworkCore.Sqlite.Core`, que não traz bundle próprio.
3. Sem migrações do EF Core; projetos de teste duplicados em duas convenções de nome;
   `Class1.cs` do template ainda presentes.

**Alternativas rejeitadas:** portar as correções para a base da main (descartaria o núcleo
já testado); fundir arquivo a arquivo (misturaria duas convenções de design).

**Consequências:** 169 testes cobrem as tabelas das seções 5.2–5.4, a máquina de estados
offline, a criptografia e a sanitização. `SqlCipherDatabaseTests.ArquivoCifrado_NaoExpoeTextoEmClaro`
varre os bytes crus do arquivo — é a rede de segurança contra a reincidência do item 2.
Perdemos temporariamente `SendMessageHandler`, `SyncAccountHandler` e `ComposeViewModel`,
que a versão anterior esboçava: entram nas fases 3 e 4 do `docs/roadmap.md`.

---

## D-008 — Artefatos de build fora do controle de versão (2026-08-04)

**Status:** aceita

**Decisão:** Remover os 1521 arquivos de `bin/` e `obj/` versionados e cobri-los no
`.gitignore`.

**Motivo:** Eram 90% dos 1691 arquivos do repositório. São regeneráveis pelo build, incham
cada clone, e produzem conflito em toda alteração de código — inclusive conflitos falsos
que escondem os reais.

**Consequências:** O repositório caiu para 143 arquivos versionados.

---

## D-009 — Descoberta automática em cinco etapas, da mais confiável para a menos (2026-08-04)

**Status:** aceita

**Decisão:** `AutodiscoverService` tenta, nesta ordem: tabela de provedores conhecidos →
autoconfig publicado pelo próprio domínio (`autoconfig.dominio` e `.well-known`) → registros
SRV do DNS (RFC 6186) → banco ISPDB da Mozilla → convenção de nomeação. Cada etapa só roda
quando a anterior não respondeu, e a origem viaja no resultado (`DiscoverySource`).

**Motivo:** As fontes não têm o mesmo peso. Autoconfig e SRV são declarações de quem manda
no domínio; o ISPDB é banco de terceiros; a convenção é palpite. Sem registrar a origem, o
assistente não teria como diferenciar "o domínio declarou isto" de "chutamos isto" na hora
de pedir confirmação ao usuário.

Três decisões de segurança acompanham:

1. **DTD desligado na leitura do XML.** O documento vem de um host escolhido pelo domínio
   que o usuário digitou. Um `<!ENTITY SYSTEM "file:///...">` transformaria a descoberta de
   servidores em leitura arbitrária de disco.
2. **Só HTTPS, e com teto de leitura.** O formato do Thunderbird admite HTTP em claro no
   arquivo do próprio domínio; aceitá-lo permitiria a quem estivesse no caminho da rede
   devolver uma configuração apontando para o servidor dele.
3. **Ao ISPDB vai apenas o domínio, nunca o endereço completo.** Quem responde é um
   terceiro sem relação com o usuário, e o domínio basta para localizar o registro.

Configuração que aponta para fora do domínio do endereço chega com
`RequiresUserConfirmation`. Hospedagem terceirizada é legítima e comum — e tem exatamente o
mesmo formato de um desvio malicioso, então quem decide é o usuário.

**Alternativas rejeitadas:** consultar tudo em paralelo e escolher o "melhor" (tornaria a
precedência implícita e imprevisível); confiar no ISPDB antes do próprio domínio (inverteria
a ordem de autoridade).

**Consequências:** dependência nova de `DnsClient` — o .NET não consulta registros SRV — e
de `Microsoft.Extensions.Http`. `IDnsResolver` isola a consulta para que a ordem das
estratégias seja verificável sem rede.

---

## D-010 — ViewModels em projeto multiplataforma próprio (2026-08-04)

**Status:** aceita

**Decisão:** Criar `Sintek.Mail.Presentation` (`net10.0`, sem WinUI) e mover para lá os
ViewModels que estavam em `Sintek.Mail.App`. O projeto entra no filtro
`Sintek.Mail.CrossPlatform.slnf` e é compilado e testado pelo job Linux do CI.

**Motivo:** Na fase 1, a camada de interface levou seis correções em quatro rodadas de CI
porque cada erro só aparecia no compilador de destino. Boa parte daquela lógica — validação
de formulário, etapas do assistente, decisão de quando pedir confirmação — não depende de
WinUI coisa nenhuma; ficava sem teste apenas por morar no projeto errado.

**Consequências:** 34 testes cobrem o assistente de contas e as telas de configuração,
executados em segundos no Linux. `Sintek.Mail.App` fica com o que de fato precisa do WinUI:
janelas, XAML, WebView2 e o vaivém entre `ContentDialog`s.

O limite é honesto: XAML, `x:Bind` e o gerador do MVVM Toolkit continuam só verificáveis no
job Windows. O que este projeto elimina é a categoria de erro que *não* precisava estar lá.

---

## D-011 — A fila de saída drena antes da leitura do servidor (2026-08-05)

**Status:** aceita

**Decisão:** O ciclo de `SyncAccountHandler` é: conectar → drenar a fila de saída → espelhar
pastas → ler mensagens. Nunca o inverso.

**Motivo:** Enquanto a fila não drena, o servidor não sabe do que o usuário fez offline. Ler
primeiro traz o estado antigo e sobrescreve localmente a intenção dele: a mensagem que ele
marcou como lida volta a não lida, e só depois a fila a marca de novo. Ele vê o marcador
piscar e conclui, com razão, que o programa está confuso.

Pelo mesmo motivo, `MessageSyncService` não aplica marcadores do servidor sobre mensagem cujo
`SyncState` não seja `Synced` — alteração local pendente tem precedência.

**Consequências:** a fila passa a ser pré-requisito da leitura. Uma operação travada atrasa a
sincronização daquela conta, o que é o comportamento correto: aplicar leitura sobre estado
divergente pioraria a divergência.

---

## D-012 — Pasta que some do servidor não é apagada (2026-08-05)

**Status:** aceita

**Decisão:** `FolderMirrorService` desliga a sincronização da pasta ausente e preserva o
conteúdo local. Exclusão de fato é decisão do usuário, pela interface de pastas.

**Motivo:** Uma resposta de `LIST` incompleta — servidor sob carga, conexão cortada no meio,
permissão temporariamente revogada — é indistinguível de uma exclusão real. A diferença entre
as duas hipóteses é a caixa postal inteira do usuário, e o custo dos dois erros não é
simétrico: manter uma pasta a mais incomoda, apagar uma pasta a menos é irreversível.

**Consequências:** pastas realmente excluídas no servidor ficam visíveis e sem sincronização
até o usuário removê-las. A alternativa — apagar automaticamente — foi rejeitada.

---

## D-013 — Classificação na chegada tem tabela de decisão própria (2026-08-05)

**Status:** aceita

**Decisão:** `MoveMessageHandler.ClassifyArrivalAsync` avalia mensagens trazidas pela
sincronização. Ela vive no mesmo handler — que continua sendo o único lugar a consultar o
`DomainMembershipEvaluator` —, mas com decisão diferente da movimentação iniciada pelo
usuário: `Block` e `MoveToPending` desviam para pendências, e `WarnAndConfirm` e `LogOnly`
apenas registram.

**Motivo:** Uma chegada não pode ser "bloqueada". A mensagem já existe no servidor, dentro
daquela pasta; recusá-la localmente apenas a esconderia do usuário sem mudar nada do outro
lado. E não há a quem pedir confirmação: a sincronização roda sozinha.

O desvio é puramente local — a mensagem continua onde está no servidor. Movê-la lá alteraria
a caixa postal de quem talvez use outro cliente na mesma conta.

**Alternativas rejeitadas:** reimplementar a avaliação dentro do motor de sincronização
(criaria uma segunda versão da regra, que divergiria da primeira); apagar a mensagem
incompatível (perda de dados por configuração).

---

## D-014 — Enviar é entregar à fila, nunca falar SMTP da interface (2026-08-05)

**Status:** aceita

**Decisão:** O botão Enviar grava a mensagem na Caixa de Saída local e enfileira
`SendMessage`, na mesma transação. O SMTP acontece quando a fila drenar. O rascunho segue o
mesmo desenho com `AppendDraft`.

**Motivo:** É a promessa offline-first aplicada ao envio: o botão funciona num avião, a
mensagem sai quando a rede voltar, e a fila visível mostra o que ainda não saiu. Falar SMTP
diretamente do compositor criaria dois caminhos de envio — um com as garantias da fila
(ordem, retentativa, recuo exponencial) e outro sem nenhuma.

A cópia em Itens Enviados usa o mesmo `MimeMessageWriter` do envio: serializar duas vezes
faria a cópia guardada divergir do que o destinatário recebeu.

**Consequências:** o envio nunca é instantâneo do ponto de vista do processo — há sempre um
ciclo de fila entre o clique e o SMTP. Em troca, falha de rede no meio do envio deixa de
existir como categoria de erro do compositor.

---

## D-015 — Índice de pesquisa com external content, não contentless (2026-08-05)

**Status:** aceita

**Decisão:** O FTS5 é reconstruído (migração `RebuildSearchIndex`) sobre uma tabela física
`MessagesSearch` — uma linha por mensagem com o texto pesquisável: assunto, prévia, corpo
(truncado em 64 mil caracteres), remetente com nome exibido, participantes e nomes de
anexo. Gatilhos das tabelas de origem mantêm o espelho; gatilhos do espelho mantêm o
índice. A pesquisa (`Fts5SearchService`) combina o MATCH com filtros estruturais em SQL
manual, com todos os valores em parâmetros.

**Motivo:** O índice contentless da fase 1 tinha um defeito estrutural: para apagar uma
entrada, o FTS5 exige reapresentar os valores que estavam indexados. Um gatilho de
`Messages` os tem em `OLD`; um gatilho de `MessageBodies` ou `Attachments` não tem como
saber o que o índice guardava — o agregado vive espalhado por outras tabelas. Resultado:
corpo, participantes e anexos existiam como colunas do índice e ficavam permanentemente
vazios. Com external content, o espelho é a fonte dos valores antigos, e `OLD`/`NEW` nos
gatilhos do próprio espelho resolvem o problema por construção.

**Consequências:** o texto pesquisável é duplicado no banco (limitado pelo truncamento do
corpo); em troca, o índice acompanha o download sob demanda e a exclusão sem código de
manutenção na aplicação.

**Alternativas rejeitadas:** manter o índice sincronizado por código nos casos de uso
(quebraria na primeira escrita fora deles — o motivo de os gatilhos existirem, já registrado
na migração da fase 1); FTS5 sem external content com `delete-all` + reinserção periódica
(janelas de índice incompleto e custo proporcional à caixa inteira).

**Armadilha registrada no caminho:** parâmetro `Guid` em SQL manual precisa ir como `Guid`,
nunca como `ToString()` — o provider grava TEXT maiúsculo e a comparação com minúsculo
falha em silêncio. Documentada em `CLAUDE.md`.
