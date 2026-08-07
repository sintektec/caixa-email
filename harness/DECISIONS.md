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

---

## D-016 — Filtragem local na chegada: bloqueio antes das regras, domínio acima de tudo (2026-08-05)

**Status:** aceita

**Decisão:** `ApplyArrivalRulesHandler` roda depois da classificação de chegada, apenas na
Caixa de Entrada, nesta ordem: (1) lista de remetentes bloqueados — que desvia pelo mesmo
caminho de "Marcar como spam" (mover **e** `$Junk`) e encerra o processamento; (2) regras
ativas em ordem de prioridade, com `StopProcessing` como corte. A avaliação das condições
(`RuleEvaluator`) é pura e vive no Domain. Toda movimentação decidida por regra passa pelo
`MoveMessageHandler`; quando a pasta de destino é restrita e a mensagem não pertence,
a ação é registrada em auditoria como ignorada — a regra de domínio prevalece sobre a
regra do usuário, e não há usuário para confirmar durante a sincronização.

**Motivo:** O bloqueio antes das regras evita trabalho sobre mensagem que o usuário pediu
para não ver — e mover só localmente deixaria o filtro do servidor classificando errado
para sempre, daí reutilizar o caminho do spam. Regras só na Caixa de Entrada: aplicá-las
em Enviados ou Arquivados refaria decisões sobre mensagens que o usuário já organizou.

**Consequências:** condições de corpo avaliam sobre a prévia na chegada (o corpo ainda não
foi baixado); ações de copiar e encaminhar ainda não são executadas e entram na auditoria
como ignoradas, com o motivo — nunca silenciosamente perdidas.

**Alternativas rejeitadas:** reavaliar regras no download do corpo (a mensagem já teria
sido triada; reclassificar depois moveria mensagens que o usuário já viu); classificador
local de spam (decisão da fase 7 no roadmap — o veredito é do servidor).

---

## D-017 — Assistência por IA: porta única, local primeiro, consentimento por diretório (2026-08-05)

**Status:** aceita

**Decisão:** Todo recurso de IA passa por `AssistantGateway`. Ele escolhe o provedor
— **local disponível primeiro, sempre** —, e só usa provedor em nuvem quando o
`DomainDirectory` da conta tem `AllowsCloudAssistant` verdadeiro. Cada envio externo é
gravado em auditoria **antes** da chamada, com provedor, tarefa e tamanho do conteúdo;
nunca com o conteúdo. O consentimento nasce falso, inclusive para diretórios já
existentes (`defaultValue: false` na migração), e é revogável.

**Motivo:** O produto inteiro é construído sobre conteúdo não sair da máquina em claro —
SQLCipher, segredos no cofre, auditoria sem conteúdo. Mandar corpo de mensagem para um
modelo em nuvem inverte isso, e num cliente organizado por domínio a confidencialidade
varia de cliente para cliente: o Diretório de Domínio já é a unidade de política e é onde
o usuário pensa sobre o assunto.

A porta única existe pelo mesmo motivo do `MoveMessageHandler`: uma segunda versão da
política divergiria da primeira, e a divergência sempre acaba com alguém mandando à nuvem
o que o diretório proíbe. E preferir a nuvem por ser melhor transformaria o consentimento
em formalidade — o usuário concordou que *pode*, não que *deve*.

**Consequências:** com modelo local instalado, o provedor de nuvem nunca é usado, mesmo
autorizado; quem quiser o contrário precisa desinstalar o local. Conta sem diretório
resolvível não é autorizada — na dúvida, o conteúdo fica na máquina.

**Alternativas rejeitadas:** consentimento global da aplicação (perderia a granularidade
que é a razão de ser do produto); consentimento por conta (o diretório já agrupa as contas
que compartilham política, e duplicar a decisão por conta convidaria à divergência);
registrar o envio depois da chamada (perderia exatamente o caso que importa — a chamada
que saiu e falhou no meio).

---

## D-018 — Envio agendado sobre a fila, não sobre um relógio novo (2026-08-05)

**Status:** aceita

**Decisão:** Agendar um envio é enfileirar a operação com `NextAttemptAt` na data
escolhida. Não há temporizador, tabela de agendamentos nem serviço de despertar.

**Motivo:** A fila já decide o que está elegível comparando `NextAttemptAt` com o relógio,
e o laço de sincronização já a consulta periodicamente. Um segundo mecanismo de espera
teria de ser mantido em sincronia com o primeiro — e a experiência com este tipo de
duplicação é que as duas versões divergem na primeira mudança de comportamento.

**Consequências:** a precisão do envio agendado é a do intervalo de sincronização, não a
do segundo. Para "enviar amanhã às 8h" isso é indiferente; para "enviar em 30 segundos"
seria ruim, mas esse caso não existe no produto.

---

## D-019 — Confirmação de leitura é sempre perguntada (2026-08-05)

**Status:** aceita

**Decisão:** Mensagem com `Disposition-Notification-To` faz aparecer uma faixa com
"Enviar confirmação" e "Não enviar". Nenhuma confirmação sai sem o clique. A decisão —
qualquer uma das duas — fica gravada em `Message.ReadReceiptHandled`.

**Motivo:** O cabeçalho é um pedido, não uma ordem. Enviar automaticamente entregaria ao
remetente a informação de que a mensagem foi aberta, que é exatamente o que um remetente
hostil quer confirmar — endereço vivo, pessoa que abre o que recebe. Gravar a recusa
importa pelo mesmo motivo que gravar o aceite: repetir a pergunta a cada abertura trataria
o "não" como um "ainda não".

**Alternativas rejeitadas:** enviar sempre (entrega o sinal ao atacante); nunca enviar
(quebra o fluxo legítimo de confirmação de recebimento em contrato e cobrança); preferência
global ligada por padrão (a decisão varia por mensagem, não por instalação).

---

## D-020 — Histórico de destinatários é alimentado no envio, não na entrega (2026-08-05)

**Status:** aceita

**Decisão:** `RecipientHistory` ganha ou incrementa uma entrada quando o
`ComposeMessageHandler` conclui um envio — isto é, quando a mensagem entra na fila —, não
quando o SMTP confirma. Rascunho gravado não alimenta o histórico.

**Motivo:** O que registra a intenção do usuário é ter escrito para aquele endereço. Se o
SMTP falhar, ele vai tentar de novo, e é justamente aí que quer a sugestão disponível.
Rascunho abandonado é o oposto: encheria o autocompletar de endereços que o usuário
desistiu de usar.

**Consequências:** um endereço digitado errado entra no histórico mesmo que a mensagem
nunca chegue. É o motivo de a remoção individual ser requisito, e não refinamento.

**Alternativas rejeitadas:** alimentar na confirmação do SMTP (perde o caso em que a
sugestão mais importa); alimentar também no rascunho (polui com o que foi descartado);
alimentar na leitura de mensagem recebida (é outra coisa — sugere quem escreve para você,
não para quem você escreve).

---

## D-021 — Sugestão fora do domínio da conta é marcada, nunca escondida (2026-08-05)

**Status:** aceita

**Decisão:** O `RecipientSuggestionRanker` devolve todas as sugestões que casam com o
termo, com `BelongsToAccountDomain` dizendo se o endereço é aceito pelo Diretório de
Domínio da conta. A interface exibe as de fora com um aviso ao lado; nenhuma é omitida.

**Motivo:** Esconder quebraria o e-mail externo legítimo, que é a maior parte do trabalho
de quem atende clientes. Não marcar deixaria enviar para um domínio sósia sem perceber — o
mesmo vetor que o `SenderTrustEvaluator` cobre na leitura, agora na escrita. Marcar informa
sem atrapalhar.

**Consequências:** a marcação depende do diretório da conta estar carregado; sem ele
(`accountDirectory` nulo) nada é marcado, em vez de tudo ser marcado — falso alarme
constante ensinaria a ignorar o aviso.

**Alternativas rejeitadas:** filtrar as de fora do domínio (quebra o caso normal); pedir
confirmação ao escolher (atrito em cada destinatário externo); bloquear o envio (é decisão
do `MoveMessageHandler` para movimentação, não para escrita — a mensagem para fora é
legítima por definição).

---

## D-022 — Consultas ordenam e comparam data pelo `julianday()` do SQLite (2026-08-05)

**Status:** aceita

**Decisão:** Toda consulta em LINQ que ordena ou compara tempo passa por
`SqliteFunctions.JulianDay(...)`, mapeada para a função `julianday()` embutida do SQLite,
com desempate por `Id` na ordenação. Ordenar ou comparar diretamente uma propriedade
`DateTimeOffset` é proibido.

**Motivo:** O provedor do SQLite recusa as duas coisas — `ORDER BY` lança
`NotSupportedException`, e `<=` nem chega a traduzir. A recusa é legítima: o EF grava o tipo
como texto preservando o fuso original, e a ordem lexicográfica desse texto não é a
cronológica quando duas linhas têm fusos diferentes — o que acontece de verdade, porque o
cabeçalho `Date` da RFC 5322 traz o deslocamento de quem enviou. `julianday()` interpreta o
texto com o fuso declarado e devolve um número, que serve para ordenar e para comparar; é o
mesmo princípio que o `Fts5SearchService` já aplicava com `datetime()` no SQL manual.

**Consequências:** quatro consultas estavam quebradas desde a fase 1 e ninguém percebeu,
porque nenhuma tinha teste contra o banco real — a listagem de mensagens da pasta, o
registro de auditoria, a limpeza de cache e a fila de saída, que nunca drenaria. Todas agora
têm teste em `DateOrderingTests`. A alternativa de gravar as datas já normalizadas em UTC foi
descartada por trocar o formato de todas as colunas de data por um ganho que esta solução já
entrega.

**Alternativas rejeitadas:** converter o armazenamento para UTC (migração de todas as
colunas de data, e perde o deslocamento original que a D-001 escolheu preservar); ordenar em
memória (a listagem de uma pasta não tem teto); ordenar por `Id` (é a ordem de criação
local, não a de recebimento — mensagem antiga sincronizada depois apareceria no topo);
`datetime()` em vez de `julianday()` (devolve texto, que resolve a ordenação e deixa a
comparação de fora — e comparar texto dependeria de o provedor traduzir `string.Compare`).

---

## D-023 — Uma implementação da RFC 5545 cobre Teams, Meet e Outlook (2026-08-05)

**Status:** aceita

**Decisão:** A agenda não tem conector por produto. Teams, Google Meet, Outlook, Zoom e
Webex mandam o mesmo `text/calendar` da RFC 5545, e o `ImportInvitationHandler` trata o
formato, não o remetente. O único ajuste por produto é a lista de propriedades em que cada
um esconde o link de entrada (`X-MICROSOFT-SKYPETEAMSMEETINGURL`, `X-GOOGLE-CONFERENCE`,
`X-ZOOM-MEETING-URL`), com busca no texto como reserva.

**Motivo:** Três conectores seriam três vezes o trabalho para obter menos: cobririam três
produtos em vez de todos os que respeitam a norma, e cada um envelheceria por conta
própria.

**Consequências:** produto que se desvie da norma não é atendido. É o custo certo — quem se
desvia da RFC 5545 também quebra com o Outlook.

---

## D-024 — `SEQUENCE` menor nunca sobrescreve maior (2026-08-05)

**Status:** aceita

**Decisão:** `CalendarEvent.ApplyUpdate` e `Cancel` devolvem `false` e não alteram nada
quando o `SEQUENCE` recebido é menor que o conhecido. A recusa vira registro de auditoria
(`InvitationOutOfOrderDiscarded`).

**Motivo:** Convite antigo chega atrasado o tempo todo — reencaminhado por um participante,
retido por um servidor lento, reprocessado numa ressincronização. Aplicá-lo mudaria a
reunião de volta para o horário errado, e o usuário apareceria na sala vazia. É o mesmo
raciocínio de `Message.MarkPending`, que também recusa rebaixar um estado mais forte.

**Consequências:** um organizador que reemita o convite sem incrementar o `SEQUENCE` — o
que a norma proíbe, mas acontece — tem a atualização aceita, porque a comparação é `<`, e
não `<=`. É o lado certo do erro: recusar convite de mesma versão perderia correções
legítimas.

**Alternativas rejeitadas:** confiar no `DTSTAMP` (é o instante de emissão do documento, não
a versão do evento, e o reencaminhamento o atualiza); aceitar sempre o mais recente que
chega (é exatamente o defeito).

---

## D-025 — Participante não move a própria cópia de reunião alheia (2026-08-05)

**Status:** aceita

**Decisão:** `EventMoveEvaluator` recusa a movimentação quando quem arrasta não organiza o
evento, e oferece `COUNTER` — propor novo horário — como alternativa. Organizador move e
reenvia `REQUEST` com `SEQUENCE` incrementado. Compromisso sem organizador é próprio e move
livremente.

**Motivo:** O Outlook permite arrastar a própria cópia. O resultado é que a pessoa passa a
aparecer livre no horário em que todos combinaram, e — pior — ela mesma vê a reunião no
horário novo e confia nele. Ninguém é avisado, porque não há nada a avisar: a mudança nunca
saiu do computador dela. Propor novo horário resolve o que arrastar só disfarça.

**Consequências:** o usuário perde uma liberdade que tinha no Outlook. A troca é
deliberada, e a recusa vem com explicação e com o botão que faz o que ele queria.

**Alternativas rejeitadas:** permitir com aviso (aviso que se pode ignorar não protege de
nada, e o custo do erro é faltar a uma reunião); permitir e mandar `COUNTER` junto
(mostraria ao usuário um horário que o organizador ainda não aceitou).

---

## D-026 — Três provedores de agenda atrás de uma porta, e o CalDAV primeiro (2026-08-05)

**Status:** aceita

**Decisão:** `ICalendarSyncProvider` é a porta única para servidor de agenda, com
`CalendarProviderKind { None, CalDav, MicrosoftGraph, GoogleCalendar }`. Esta fase entrega a
implementação **CalDAV**; Graph e Google Calendar ficam registrados como próximo passo,
desenhados atrás da mesma porta.

**Motivo:** O mercado se dividiu, e não por gosto. O Exchange Online **nunca** implementou
CalDAV — nem hoje, nem no on-premises, nem no Outlook.com — e o EWS está sendo desligado
(bloqueio global em 01/10/2026, remoção até 04/2027). Para Microsoft 365 o único caminho
suportado é o Microsoft Graph. A Google mantém CalDAV como compatibilidade declaradamente
parcial e recomenda a Calendar API. CalDAV é o padrão aberto que cobre todo o resto:
Nextcloud, ownCloud, Baikal, Fastmail, iCloud, SOGo, Radicale, DAViCal. Um cliente que só
fale CalDAV fica fora do mercado corporativo Microsoft; um que só fale Graph fica fora de
todo o resto.

**Consequências, e a mais séria delas:** a regra do `SEQUENCE` (D-024) **não se aplica ao
caminho do Graph**. O CalDAV carrega o iCalendar íntegro, então a versão está lá e o
`CalendarConflictEvaluator.AllowsSequence` a compara. O Graph não expõe `SEQUENCE`: quando
aquela implementação existir, a precedência terá de ser expressa por outro critério
(`lastModifiedDateTime` mais `changeKey`), e isso é uma decisão nova, não uma adaptação
desta. Registrar aqui evita que a fase seguinte a tome por descuido.

Outra consequência: a coleção sem `sync-collection` cai no `CTag`, que lista a coleção
inteira a cada alteração. É mais tráfego, e é o preço de atender servidor antigo.

**Alternativas rejeitadas:** só CalDAV (deixaria Microsoft 365 de fora, que é justamente o
cliente corporativo); EWS para Microsoft (tem data de morte marcada); um gateway de
terceiros traduzindo CalDAV para Graph (não é CalDAV nativo, e põe a credencial do usuário
num terceiro).

---

## D-027 — Conflito de agenda não é resolvido em silêncio (2026-08-05)

**Status:** aceita

**Decisão:** Quando local e servidor mudam o mesmo compromisso entre duas sincronizações, o
`CalendarConflictEvaluator` devolve `Conflict`, o compromisso é marcado com
`CalendarSyncState.Conflict` e fica visível na agenda até o usuário escolher qual versão
fica. Nada é escrito de nenhum lado enquanto isso.

**Motivo:** Qualquer regra automática — última escrita vence, servidor vence, local vence —
descarta o trabalho de alguém, e a pessoa só descobre quando procura o que escreveu e não
acha. É a mesma postura de `InvalidEmailAction.WarnAndConfirm` na regra de domínio: onde a
decisão custa caro, quem decide é o usuário.

**Consequências:** um compromisso pode ficar parado esperando decisão, sem subir e sem
descer. É o custo certo — a alternativa é perder a alteração sem aviso. Aceitar a versão do
servidor descarta o `ETag` conhecido de propósito: mantê-lo faria a passada seguinte
concluir que os dois lados estão iguais e deixaria a versão local, que o usuário acabou de
descartar, como a final.

---

## D-028 — Ausência só significa exclusão em passada completa (2026-08-05)

**Status:** aceita

**Decisão:** `RemoteCalendarChanges.IsFullEnumeration` é declarado pelo provedor, e só ele
autoriza o motor a apagar o compromisso local que não apareceu na listagem. O motor **não**
deduz isso da ausência de token.

**Motivo:** Três situações devolvem zero alterações e significam coisas opostas. Uma passada
incremental sem novidade: nada mudou, e apagar esvaziaria a agenda. Um servidor sem
`sync-collection` respondendo "o `CTag` não mudou": também não enumerou nada. E uma passada
completa de uma coleção esvaziada no servidor: aí sim tudo foi removido. Deduzir pelo token
nulo trata as duas primeiras como a terceira.

**Consequências:** o provedor carrega a responsabilidade de declarar o que fez, e um
provedor que declare errado apaga a agenda do usuário. É por isso que os dois caminhos do
cliente CalDAV — `sync-collection` e `CTag` — têm teste dedicado para esse campo.

---

## D-029 — Precedência sem `SEQUENCE`: instante de alteração, e só entre iguais (2026-08-05)

**Status:** aceita

**Decisão:** `RemoteVersion` carrega os dois critérios possíveis — `Sequence` e
`LastModifiedAt` — e `CalendarConflictEvaluator.AllowsVersion` **só compara o que existir dos
dois lados**. Com `SEQUENCE` dos dois lados vale D-024. Sem ele, vale o instante de alteração,
com igual sendo aceito. Sem critério comum, aplica-se.

**Motivo:** D-026 já registrava que o Graph não expõe `SEQUENCE` e que isso exigiria decisão
nova. É esta. Três pontos que não são óbvios:

Comparar um `SEQUENCE` com um instante produziria recusa arbitrária — são grandezas
diferentes, e um servidor que reescreve o objeto ao gravar move o segundo sem tocar no
primeiro. Daí a comparação só acontecer entre critérios do mesmo tipo.

Igual é aceito nos dois. No `SEQUENCE` é a mesma escolha de D-024, pelo mesmo motivo. No
instante é porque a granularidade de `lastModifiedDateTime` faz duas alterações próximas
caírem no mesmo valor, e recusar a segunda perderia a correção.

Sem critério comum, aplica-se. Chegar até a comparação já significa que o `ETag` mudou — o
servidor está afirmando que o recurso é outro. Recusar por falta de versão comparável deixaria
a cópia local parada para sempre num servidor que não declara versão nenhuma. É o lado certo
do erro: o caro é perder alteração, não reaplicar uma igual.

**Consequências:** o caminho do Graph e o da Google ficam mais frouxos que o do CalDAV. Um
convite antigo reentregue pelo Graph pode sobrescrever, se o servidor tiver reescrito o
`lastModifiedDateTime`. Não há como fazer melhor com o que a API expõe — e `changeKey`, que
seria o análogo, é opaco e não ordenável.

**Alternativas rejeitadas:** recusar tudo o que não trouxer `SEQUENCE` (travaria o Graph
inteiro); tratar ausência de versão como versão zero (faria toda atualização do Graph parecer
regressão); usar o `changeKey` (é opaco, não ordena, e serve só para igualdade).

---

## D-030 — A porta de agenda troca `CalendarEventData`, não texto (2026-08-05)

**Status:** aceita

**Decisão:** `ICalendarSyncProvider` recebe e devolve `CalendarEventData`. O documento
iCalendar íntegro viaja junto **quando existe**, em `RemoteCalendarChange.ICalendar`, para ser
preservado — não para ser lido de novo. O motor não serializa nem interpreta formato algum; o
`ICalendarSerializer` passou a ser dependência do adaptador CalDAV, que é quem fala iCalendar.

**Motivo:** Só um dos três protocolos fala iCalendar. Obrigar Graph e Google a sintetizar um
documento para o motor reinterpretar seria inventar um formato intermediário e uma segunda
chance de errar em cada caminho — exatamente o que D-023 evita ao não ter um conector por
produto.

**Consequências:** `RawICalendar` fica nulo no Graph e na Google. Lá não há documento a
preservar, e a preocupação com propriedades desconhecidas — o motivo de o campo existir — não
se aplica: a API devolve campos nomeados, não um documento de terceiros.

Consequência secundária, e visível: **compromisso recorrente criado aqui sobe ao Graph como
encontro único.** Traduzir `RRULE` para o objeto de recorrência do Graph exige mapear exceções,
contagem e limite por data, e um mapeamento parcial gravaria no servidor uma série diferente da
que o usuário vê. Um encontro único é visivelmente errado e corrigível; uma série
silenciosamente errada faz a pessoa faltar a reuniões. A leitura no sentido inverso existe e
recusa o que não sabe traduzir, pelo mesmo critério.

---

## D-031 — Graph lê por `events` com `$filter`, não por `calendarView/delta` (2026-08-05)

**Status:** aceita

**Decisão:** o provedor do Graph lê `GET /me/calendars/{id}/events` com `$filter` em
`lastModifiedDateTime`, e faz uma passada completa quando não há marca-d'água ou quando a
última passou de 24 horas. O `delta` não é usado.

**Motivo:** o único `delta` de calendário em `v1.0` é `/me/calendarView/delta`, e ele **exige
janela de datas e expande a recorrência em ocorrências**. Uma reunião semanal de um ano vira 52
objetos sem `RRULE`. Este produto guarda o mestre com a regra e expande ao desenhar a grade —
é o que permite editar "a série", e é o que a agenda local já faz com o convite que chega por
e-mail. Usar `calendarView` destruiria isso e ainda esconderia tudo o que caísse fora da janela.

**Consequências:** a consulta por `$filter` **não reporta exclusão** — o recurso simplesmente
some da listagem. Daí a passada completa periódica, que é o que `IsFullEnumeration` autoriza
(D-028). O intervalo de 24 horas é o atraso máximo com que uma exclusão feita no servidor é
notada aqui; encurtá-lo custa listar a agenda inteira com mais frequência.

O token de sincronização do Graph, por isso, não é um token de servidor: é o estado que este
provedor guarda (marca-d'água e data da última passada completa), serializado em JSON. Ele
continua opaco para o motor, que é o que a porta promete.

**Alternativas rejeitadas:** `calendarView/delta` (destrói o mestre de série); expandir
localmente o que o `calendarView` devolve (seria reconstruir a `RRULE` a partir das ocorrências
— adivinhação); assinar notificações de mudança do Graph (exige endpoint público para receber
o webhook, que um cliente desktop não tem).

---

## D-032 — O `client_secret` da Google fica em configuração, não no cofre (2026-08-05)

**Status:** aceita

**Decisão:** `OAuthClientOptions` ganhou `ClientSecret`, lido em `OAuth:Google:ClientSecret`, e
`GoogleOAuthProvider.IsConfigured` passou a exigir Client ID **e** Client secret. O valor vive
em configuração — `appsettings.Local.json` ou variável de ambiente `SINTEK_MAIL_` —, e não no
`ICredentialStore`.

**Motivo:** o levantamento da fase 14 encontrou um defeito real: o provedor construía
`new ClientSecrets { ClientId = ... }` com `ClientSecret` nulo. Cliente do tipo "Desktop app" da
Google recebe os dois valores, e o `client_secret` é parâmetro obrigatório na troca do código e
na renovação por `refresh_token` — só iOS e Android são emitidos sem segredo. A
`Google.Apis.Auth` omite o campo nulo do corpo sem lançar exceção, então o defeito era invisível
até a implantação: o navegador abria, o usuário consentia, e a falha aparecia só na resposta do
servidor de token.

Sobre **onde** guardá-lo: é credencial de aplicativo, não de usuário. A própria Google documenta
que o valor fica embutido no aplicativo e que um app instalado não guarda segredo de verdade —
ele identifica o aplicativo, não o autentica. Guardá-lo no Windows Credential Manager daria
falsa sensação de proteção a um valor que qualquer usuário extrai do binário, e ainda criaria o
problema de como colocá-lo lá antes do primeiro uso. O que continua no cofre é o **token de
atualização**, esse sim equivalente à senha de quem entrou.

**Consequências:** a regra "nenhum segredo entra no banco de dados" continua valendo por
inteiro — ela é sobre credencial de usuário, e este valor não é uma. O `appsettings.Local.json`
já está fora do controle de versão pelo mesmo motivo que os Client IDs estão.

O defeito nunca foi pego por teste porque nenhum teste tinha Client ID real. Os testes novos
verificam a **regra** — que sem segredo o provedor se declara não configurado, e que a mensagem
cita as duas chaves —, que é o que dá para verificar sem falar com a Google.

**Alternativas rejeitadas:** guardar no `ICredentialStore` (falsa proteção, e problema de
inicialização); embutir no binário (impede cada organização de registrar o próprio aplicativo,
que é o desenho desde a fase 1).

## D-033

**A tradução `RRULE` → Graph só escreve o que ela própria consegue reler.**

`GraphRecurrence.ToRecurrence` fecha a lacuna deixada por D-030: compromisso recorrente criado
aqui passa a subir ao Graph como série, e não mais como encontro único. O critério que define o
que ela aceita não é "o que o Graph suporta", e sim **o conjunto que `ToRRule` produz** —
diária, semanal com dias, mensal por dia do mês, anual, com contagem ou data-limite.

O motivo é a ida e a volta. Escrever um padrão que a leitura não entende faria o compromisso
subir como série e voltar como encontro único na sincronização seguinte: a divergência
apareceria sozinha, sem ninguém ter tocado no compromisso, e o usuário veria o próprio trabalho
se desfazer. Por isso os padrões `relativeMonthly` e `relativeYearly` ("a segunda terça do mês")
seguem fora dos **dois** lados — entram juntos ou não entram.

Completar do `DTSTART` o que a regra omite **não é adivinhação**: a RFC 5545 §3.3.10 manda
derivar dele as partes `BY*` ausentes, e o Graph apenas exige escrito o que a norma deixa
implícito. Uma `FREQ=MONTHLY` sem `BYMONTHDAY` repete no dia do mês em que a série começou, e é
isso que `dayOfMonth` recebe.

Parte `BY*` que não valha para a frequência **recusa a regra inteira**, mesmo quando a tradução
saberia lê-la em outro contexto. Isto não é rigor decorativo: foi o defeito que o teste pegou.
`FREQ=MONTHLY;BYDAY=2TU` caía no ramo mensal, que só olhava `BYMONTHDAY`, e o `BYDAY` era
descartado em silêncio — a regra virava "dia 10 de todo mês". Não é tradução incompleta, é outra
série, com aparência de correta.

No corpo enviado a recorrência tem **três estados, não dois**, e a diferença entre o segundo e o
terceiro é o que protege a série no servidor:

| Situação | O que vai no corpo |
|---|---|
| Regra traduzível | o objeto de recorrência |
| Nenhuma regra | `"recurrence": null` |
| Regra sem tradução fiel | **o campo não vai** |

Num `PATCH` do Graph, campo ausente significa "não mexa". Sem o nulo explícito, a remoção da
repetição não se propagaria: o usuário apaga, salva, e ela volta na sincronização seguinte. E
pelo mesmo motivo a regra intraduzível exige o oposto — mandar nulo ali apagaria do servidor a
série que não soubemos ler, e o custo dos dois erros não é simétrico.

**Alternativas rejeitadas:** traduzir tudo que o Graph aceita (quebra a ida e volta, e é o que
D-030 já recusava); ignorar a parte `BY*` desconhecida em vez de recusar a regra (produz série
diferente com aparência correta, que é o pior resultado possível); omitir sempre o campo de
recorrência (mantém o defeito de a remoção nunca chegar ao servidor).


## D-034

**A interface abre um escopo por operação; ViewModel residente recebe a fábrica, nunca o repositório.**

`MainWindow` e `ShellViewModel` eram `AddSingleton` e dependiam de repositórios `AddScoped`. O
`BuildServiceProvider()` sem `ValidateScopes` aceita isso em silêncio, e o resultado é que a
interface inteira capturava o `MailDbContext` do escopo raiz e o compartilhava por toda a
execução — enquanto o `AccountSyncWorker`, que abre escopo novo a cada rodada, escrevia nas
mesmas linhas.

Três sintomas, uma causa: `DbUpdateConcurrencyException` ao clicar numa mensagem, porque o
contexto da interface guardava valores que o laço já havia mudado; travamento, porque
`DbContext` não é seguro para uso concorrente; e painel de leitura vazio, porque a exceção
subia de `LoadMessageAsync` — chamado de um `async void` — e abortava a carga antes de
renderizar.

**Existia desde a fase 1 e nenhum dos 987 testes o alcançava**, porque todos montam
repositórios direto, com o contexto que o próprio teste cria. O defeito só existe na
composição, e não havia teste de composição.

A escolha entre quatro desenhos foi feita comparando-os, não por instinto: fábrica de contexto,
escopo por operação, mexer só no rastreamento, e prevenção. Os quatro convergiram para escopo
por operação — inclusive o que investigava não mexer no tempo de vida, que concluiu que
`NoTracking`, recarga explícita e token de concorrência atacam só valor velho, e dois dos três
sintomas não nascem de valor velho.

O que a proposta vencedora acrescentou foi a **prevenção**, e ela vale mais que a correção:

- `ValidateScopes` e `ValidateOnBuild` — o contêiner errado deixa de abrir, dizendo qual
  serviço e qual consumidor. Sem isso, a próxima dependência cativa entra igual.
- `App.Services` deixou de existir. Não há mais como resolver do provedor raiz, e quem tentar
  encontra o **compilador**, não um analisador nem uma revisão.
- `Sintek.Mail.Composition` existe para o teste montar o **mesmo** contêiner que o aplicativo.
  Uma lista paralela de chamadas `AddSintekMail*` divergiria da real com o tempo, e o teste
  passaria a provar um contêiner que ninguém executa.
- `ContainerCompositionTests` prende a invariante nos **dois** sentidos: ViewModel residente
  precisa resolver do raiz, ViewModel de diálogo precisa **falhar** no raiz. Só a primeira
  metade deixaria alguém "consertar" uma falha promovendo um repositório a singleton.

**Duas armadilhas que a correção arma, e que entraram junto.** `GetWithParticipantsAsync` não
fazia `Include` de `Message.Body`; isso não aparecia porque o contexto único devolvia, por
resolução de identidade, a mesma instância que o download acabara de preencher. Com contexto
por operação o corpo passaria a chegar nulo. E `LoadFolderAsync`, disparado sem `await` ao
alternar o agrupamento, antes batia em "a second operation was started on this context
instance" — falha alta; agora as duas cargas correm de verdade sobre a mesma
`ObservableCollection`, e o que era exceção barulhenta viraria lista errada em silêncio. Daí a
guarda por número de geração.

**Alternativas rejeitadas:** `IDbContextFactory` com `IWorkSession` (nota 7,0 — mexe em
repositórios, `UnitOfWork` e `Fts5SearchService`, e o custo não se paga contra o escopo por
operação, que o `AccountSyncWorker` já usa); resolver só com `NoTracking` e recarga (nota 7,33
— não trata uso concorrente, que é causa de travamento e não de valor velho); manter o provedor
raiz acessível e confiar em revisão (o defeito atravessou catorze fases assim).

---

## D-035 — A CSP do painel de leitura acompanha a autorização de imagens

**Data:** 2026-08-06
**Contexto:** primeira execução real; "Exibir imagens" não fazia nada.

O botão estava ligado direto em `AllowRemoteContentCommand`. O comando reescrevia
`SanitizedHtml` no ViewModel, mas quem entrega o HTML ao `WebView2` é `RenderBodyAsync`, na
janela, e nada o chamava depois — o documento antigo, sem imagem nenhuma, continuava na tela.

E reapresentar sozinho não resolveria: `WrapInDocument` fixava `img-src cid: data:` na CSP.
Com as imagens autorizadas, o `MessageHtmlSanitizer` preserva os `src` remotos e o navegador
recusaria **todos** — a CSP era mais restritiva que o HTML que ela deveria conter. O bloqueio
só apareceria no console das DevTools, que estão desligadas ali de propósito.

**Decisão:** o `img-src` é montado a partir de `RemoteContentAllowed`, com os mesmos esquemas
que `MessageHtmlSanitizer.CreateSanitizer` passa a aceitar; sem autorização volta a ser o mais
estreito possível. O botão vira `Click`, que autoriza **e** reapresenta.

**Por quê:** as duas listas descrevem a mesma decisão do usuário e precisam concordar. Uma
barreira que contraria a decisão em vez de a proteger não é defesa em profundidade — é defeito,
e do tipo que não deixa rastro.

**Alternativa rejeitada:** manter a CSP fixa e aceitar que a autorização vale só para o
sanitizador. Seria uma opção de menu que não faz nada, que é pior do que não ter a opção.

---

## D-036 — Quem baixa conteúdo sob demanda também conecta

**Data:** 2026-08-06
**Contexto:** consequência direta de D-034, encontrada ao revisar as minas que a mudança de
escopo armava.

`IImapClient` tem escopo. O laço de sincronização conecta o cliente **do escopo dele**; o
clique numa mensagem abre outro escopo, com outra instância, que nasce desconectada. Nada em
`DownloadMessageContentHandler` chamava `ConnectAsync`, então `FetchBodyAsync` caía no
`EnsureConnected` da implementação e lançava `InvalidOperationException` — que sobe pelo
manipulador `async void` da seleção de mensagem e **derruba a aplicação**. Clicar numa mensagem
ainda não baixada fechava o programa.

Não era regressão de D-034: com o provedor raiz a instância também nunca era conectada. A
mudança de escopo só tornou o defeito determinístico, o que é uma melhora.

**Decisão:** o caso de uso conecta o próprio cliente antes de buscar, nos dois caminhos (corpo
e anexo), e a falha vira `DownloadBodyResult`, nunca exceção.

**Por quê:** o caminho é disparado por um clique. Sem rede, o esperado é a faixa de aviso do
painel de leitura — uma exceção fecharia a aplicação pelo mesmo `async void`. Conectar no caso
de uso, e não no ViewModel, é o que `OutboxProcessor` e `SyncAccountHandler` já fazem: quem
sabe que precisa da rede e conhece a conta é quem conecta.

**Detalhe que importa:** `ApplyArrivalRulesHandler` também baixa corpo, para avaliar condição
sobre o texto completo — mas roda a partir do `SyncAccountHandler`, no escopo em que o cliente
já está conectado. `IsConnected` curto-circuita, e nenhuma ida e volta é acrescentada ao laço.

---

## D-037 — UID é identidade por pasta, e a reconciliação precisa respeitar isso

**Data:** 2026-08-06
**Contexto:** primeira execução real. Nenhuma mensagem de nenhuma conta Gmail exibia o corpo;
o servidor respondia que não conhecia aquele UID na pasta.

`MessageSyncService.UpsertAsync` reconciliava assim:

```
GetByUidAsync(folder.Id, header.Uid) ?? GetByMessageIdAsync(folder.AccountId, header.MessageId)
```

A segunda busca varre a **conta inteira**. E `ApplyRemoteFlags`, ao receber a linha
encontrada, grava nela o UID do cabeçalho — o da pasta que está sendo sincronizada.

No Gmail cada rótulo é uma pasta, e a mensagem da Caixa de Entrada aparece em todas elas,
cada cópia com o **seu** UID. Sincronizar o rótulo achava a linha da Caixa de Entrada e
carimbava nela o UID do rótulo. A linha continuava com `FolderId` da Caixa de Entrada e um
UID que só existe noutra pasta.

Nada disso aparecia na lista, que já tinha os cabeçalhos gravados na criação. Aparecia no
clique: `FetchBodyAsync("INBOX", uidDoRotulo)` e `MessageNotFoundException`. Como praticamente
toda mensagem do Gmail carrega ao menos um rótulo, falhava em todas.

**Decisão:** a reconciliação por `Message-ID` passa a ser recortada pela pasta
(`GetByMessageIdInFolderAsync`). A busca por conta continua existindo, para deduplicação e
para relacionar cópias, mas **não** decide identidade de rede.

**Por quê:** UID é identidade por pasta — está na RFC 3501, e o Gmail apenas torna isso
visível todo dia. O caso que a busca ampla existia para atender, o MOVE em servidor sem
UIDPLUS, continua atendido: a mensagem reaparece **na pasta de destino**, e a busca recortada
por essa pasta a encontra.

**Autocorreção:** bancos já corrompidos se curam na sincronização seguinte. `GetByUidAsync`
falha (a linha tem UID errado), a busca por pasta encontra pelo `Message-ID`, e
`ApplyRemoteFlags` grava o UID certo — o da pasta onde a linha mora.

**O que o defeito ensina sobre a suíte:** 928 testes não o pegaram porque todos os dublês
respondiam por uma pasta só. O cenário que faltava não era exótico — era o provedor de e-mail
mais usado do mundo, no comportamento mais característico dele.

---

## D-038 — Falha de sincronização aparece na tela, não só no log

**Data:** 2026-08-06
**Contexto:** contas de um domínio foram criadas e nenhuma carregou conteúdo, sem que a
aplicação dissesse por quê.

`Account.SyncStatus` e `Account.LastSyncError` existem desde a fase 3 e `SyncAccountHandler`
os grava corretamente. **Ninguém os lia.** Uma conta parada por senha expirada ficava idêntica
a uma conta sem mensagem nova, e o motivo vivia no log de depuração — que o usuário não tem
como abrir. Ele descobria dias depois, procurando um e-mail que nunca chegou.

O laço também não falava com a interface: gravava e seguia, para não morrer.

**Decisão:** três ligações, e nenhuma delas inventa dado novo.

1. `NavigationNode` carrega `SyncStatus` e `SyncError`; o nó da conta ganha alerta com o
   motivo na dica.
2. `ShellViewModel.ReportSyncProblems` leva o primeiro motivo à barra de status, **nomeando a
   conta** — com várias cadastradas, "falha de sincronização" não diz onde mexer.
3. `ISyncActivityNotifier` liga o laço à janela. O laço avisa ao fim de uma volta que mudou
   algo; a janela relê. Sem isso, a falha registrada às 3 da manhã só apareceria quando o
   usuário clicasse em sincronizar.

**`Offline` não acende alerta**, de propósito: é o modo offline funcionando como projetado.
Alerta que aparece a cada oscilação de rede deixa de ser lido, e aí o que importa passa junto.

**O aviso não sobrescreve mensagem já posta.** Uma recusa da regra de domínio acabou de ser
explicada; trocá-la apagaria a resposta à ação que o usuário acabou de fazer.

**O evento chega em thread de segundo plano**, e quem assina leva ao despachante — a `MainWindow`
usa o `DispatcherQueue`. Pôr isso na porta obrigaria a Aplicação a conhecer o WinUI.

---

## D-039 — O laço de sincronização descarta o escopo com `await using`

**Data:** 2026-08-06
**Contexto:** achado ao investigar D-038.

`AccountSyncWorker.RunOnceAsync` abria o escopo do ciclo com `using` comum. Esse escopo resolve
`SyncAccountHandler`, que traz um `MailKitImapClient` junto — e ele implementa **só**
`IAsyncDisposable`. O descarte síncrono lança `"type only implements IAsyncDisposable"`.

O sintoma é o pior tipo: **tudo funciona**. A exceção acontece no fim do bloco, depois de todo
o trabalho feito, e é capturada pelo `catch` que existe para o laço não morrer. As mensagens
aparecem, a sincronização parece boa, e cada volta termina em erro logado — com a conexão IMAP
**nunca encerrada**. Conexão vazada por ciclo esbarra no limite de sessões simultâneas do
servidor; o Gmail corta em quinze.

A armadilha estava escrita no `CLAUDE.md` desde a correção de escopo, e mesmo assim passou:
está documentada para os escopos da interface, e este é o do laço.

---

## D-040 — A volta ao ciclo de sincronização precisa existir, não só ser prometida

**Data:** 2026-08-06
**Contexto:** contas de um domínio criadas e nunca carregadas. Investigação do que D-038
passou a tornar visível.

`SyncSchedule.Decide` pula **indefinidamente** a conta com `AuthenticationFailed`, e o motivo
é bom: credencial recusada não melhora com insistência, e tentar a cada minuto é a forma mais
rápida de ganhar bloqueio temporário no provedor. O comentário dizia:

> A conta volta ao ciclo quando o usuário reautenticar, o que muda o estado dela.

**Nada executava essa volta.** `UpdateAccountHandler` trocava servidor, porta, usuário e
senha — e não tocava em `SyncStatus`. Nenhum outro caso de uso tocava. Fora do
`SyncAccountHandler`, o estado nunca saía de `AuthenticationFailed`.

O sintoma é de uma conta que não carrega nada e não explica: o usuário corrige a senha, o
teste de conexão passa, a tela confirma que salvou — e a caixa continua vazia para sempre. A
única saída era o botão de sincronizar agora, que não passa pelo agendador; quem não o
descobrisse ficava sem conta.

**Decisão:** `Account.ResumeSync` devolve a conta ao ciclo, e `UpdateAccountHandler` o chama
ao gravar.

**Volta como `NeverSynced`, não `Online`.** Quem reconfigurou não provou que o servidor
aceita — apenas pediu nova tentativa. Declarar a conta em dia mentiria na barra de status até
a primeira sincronização, e é o resultado dela que define o estado.

**A ordem contra `SetActive` importa.** Desativar define `Disabled`, e é essa a palavra final;
retomar depois disso devolveria à fila uma conta que o usuário acabou de desligar. O teste
`AlterarConta_DesativandoConta_DispensaOTesteDeConexao`, que já existia, pegou a inversão na
primeira execução.

**O que o defeito ensina sobre a suíte:** havia teste para a saída do ciclo
(`Agendar_CredencialRecusada_SaiDoCicloAteReautenticar`) e nenhum para a volta. Meia regra
verificada passa por regra inteira — o nome do teste até citava a reautenticação que não
acontecia.

---

## D-041 — Conflito de concorrência é traduzido na fronteira e tratado no clique

**Data:** 2026-08-06
**Contexto:** `DbUpdateConcurrencyException` sem tratamento derrubando a aplicação ao abrir
uma mensagem.

Não há token de concorrência em nenhuma entidade, então "esperava 1 linha, afetou 0" quer
dizer **a linha não existe mais**. E a janela é larga: o painel de leitura carrega a mensagem,
gasta segundos na rede conectando e baixando o corpo, e só então grava. Nesse intervalo o laço
de sincronização — escopo próprio, outro contexto — escreve e remove nas mesmas linhas.

**Decisão:** `UnitOfWork.SaveChangesAsync` traduz para `ConcurrentModificationException`, da
camada de Aplicação, preservando a original como `InnerException`. O download de corpo a
captura e devolve `DownloadBodyResult`.

**Por que traduzir, e não capturar o tipo do EF:** a Aplicação não referencia o EF Core, e não
deve. Capturar lá exigiria a referência; capturar só na App perderia o tratamento nos casos de
uso.

**Por que não repetir a gravação automaticamente:** a linha foi removida, não alterada. Repetir
recriaria a mensagem que a sincronização acabou de apagar, ressuscitando no cliente o que já
não existe no servidor. O usuário reabre; a lista já estará correta.

---

## D-042 — UID que o servidor desconhece se corrige, não se apaga

**Data:** 2026-08-06

Dois enganos vinham do mesmo lugar: tratar "este UID não está no servidor" como prova de que a
mensagem sumiu.

**O primeiro é perda de dados.** `ReconcileDeletionsAsync` apagava a linha. Mas as linhas
corrompidas por D-037 — que receberam UID carimbado de outra pasta — são exatamente as que
essa pergunta condena, e a mensagem delas está no servidor, inteira. A reconciliação já baixa
todos os cabeçalhos: passou a perguntar também pelo `Message-ID` e, achando, **corrige o UID**.

**O segundo é a cura que eu havia prometido e não existia.** Afirmei que os bancos corrompidos
se curariam na sincronização seguinte. **Falso.** A leitura incremental parte de
`Folder.LastSeenUid` e busca só o que está acima dele; linha antiga nunca é revisitada, e
`UpsertAsync` — onde a correção por `Message-ID` mora — nunca roda para ela. O corpo falharia
para sempre.

**Decisão:** quando o servidor responde que não conhece o UID, `DownloadMessageContentHandler`
chama `Folder.RequestFullReread` — zera o marcador incremental. A próxima passada lê a pasta
inteira e `UpsertAsync` reconhece cada mensagem pelo `Message-ID` dentro da pasta, corrigindo
o UID sem duplicar nada.

**Por que ali:** é o único ponto do sistema com prova de que o marcador não corresponde ao
servidor. Varrer tudo periodicamente custaria uma enumeração completa por pasta por ciclo,
que é justamente o que a leitura incremental existe para evitar.

**O texto ao usuário deixou de anunciar exclusão.** Dizer "foi apagada ou movida" quando o
caso comum é UID errado é pior do que não explicar: a pessoa para de procurar.

---

## D-043 — Recarga da árvore não recusa uma sincronização

**Data:** 2026-08-06

`SyncNowAsync` começava com `if (IsBusy) return;`, e `IsBusy` é o mesmo sinalizador que
`LoadNavigationAsync` liga. Depois de D-038, o laço passou a mandar a árvore recarregar a cada
volta — e essas recargas ficaram frequentes.

Resultado: **clicar em sincronizar durante uma recarga não fazia nada e não avisava nada.** O
pior desfecho possível para um botão: o usuário conclui, com razão, que o programa está
quebrado.

**Decisão:** guarda própria (`IsSyncing`), e o comando expõe `CanExecute`. Botão desabilitado
comunica "já estou fazendo"; botão habilitado que ignora o clique comunica outra coisa.

**Por que separar:** recarregar a árvore lê o banco; sincronizar conversa com o servidor. Uma
não é motivo para recusar a outra, e tratá-las como a mesma "ocupação" foi o erro.

---

## D-044 — Estado da interface é copiado antes do `await`, não relido depois

**Data:** 2026-08-06

`OnMessageSelectionChanged` verificava `SelectedMessage` no início e lia
`SelectedMessage.MessageId` **depois de dois `await`**. `ConfigureAwait(true)` devolve a
continuação à thread da interface, mas não impede que a interface tenha andado enquanto se
esperava — e o que anda ali é a própria seleção: basta a lista recarregar, e ela recarrega,
porque o laço manda recarregar a cada volta desde D-038.

O sintoma foi `NullReferenceException` em manipulador `async void`: **a aplicação fechava**,
ao clicar numa mensagem enquanto a caixa sincronizava — que é o momento mais comum de se
clicar numa mensagem.

**Decisão:** copiar o identificador antes do primeiro `await` e usar a cópia até o fim.

**Varredura feita:** os demais pontos que releem estado volátil após `await`
(`MessageList.FolderId`, `Shell.SelectedNode`, `Reading.MessageId`) usam padrão seguro para
nulo e no máximo leem valor velho. Só este tinha acesso direto depois de uma verificação
envelhecida, e só ele derrubava.

---

## D-045 — Comando que reescreve a entidade inteira precisa trazer tudo

**Data:** 2026-08-06

`UpdateAccountCommand` tem valores padrão em todas as propriedades, e o caso de uso aplica o
comando por completo. `ToggleSelectedAccountAsync` montava o comando com quatro campos, e o
`SyncIntervalMinutes` ausente virava o padrão do registro — **cinco minutos**.

Consequência: ativar e desativar uma conta apagava o intervalo configurado, em silêncio.
Ninguém notaria: o valor não estava em tela nenhuma até esta rodada.

**Decisão:** o comando de alternância passa o intervalo da conta. E o intervalo ganhou tela,
por conta, na lista de contas das configurações — é por conta que ele vale, e uma caixa
corporativa movimentada não pede a mesma frequência de um endereço pessoal.

**O piso é um minuto e o teto um dia.** Zero faria `ConfigureSync` lançar; valores muito
curtos rendem bloqueio temporário no provedor.

**Lição mais geral:** comando com padrão em toda propriedade parece conveniente e transforma
omissão em sobrescrita. Onde ele existir, quem monta precisa trazer o estado inteiro — ou o
padrão vira a decisão.

---

## D-046 — A fila mostra por que a operação falhou, e qual delas trava as demais

**Data:** 2026-08-06
**Contexto:** dezoito "Mover mensagem" presas na fila, duas com "Falhou 2 vez(es)" e nenhuma
pista do motivo.

`OutboxOperation.LastError` é gravado desde a fase 5 e **não chegava à tela**. Terceira vez
neste projeto que o dado existe e ninguém o lê — depois de `Account.LastSyncError` (D-038) e
`Account.LastSyncAt` (D-043).

Aqui o custo é maior que o normal, por causa de uma decisão anterior: **a fila é estritamente
sequencial por conta e interrompe o lote na primeira falha** (`OutboxProcessor.DrainAsync`),
porque as operações seguintes dependem do estado que a anterior deixaria. A consequência é
bloqueio de cabeça de fila: uma operação que falha sempre segura todas as outras
indefinidamente, e o motivo dela é o **único** dado que explica por que nada mais sai.

**Decisão:** o motivo entra na descrição da situação, e a operação que falhou passa a exibir o
alerta que antes era só das mortas.

**A regra de "não pedir atenção a cada falha temporária" foi revista, com evidência nova.** O
raciocínio original — alerta a cada oscilação de rede vira ruído — supunha que uma falha
atrasa apenas a si mesma. Com interrupção do lote, ela atrasa tudo. Não é a mesma situação, e
por isso não é reabrir decisão sem motivo.

**O que não mudou:** a fila continua sequencial. Paralelizar resolveria o bloqueio e quebraria
a semântica que a ordem garante — o remédio seria pior.

---

## D-047 — Filho novo em agregado rastreado precisa de inserção explícita

**Data:** 2026-08-06
**Contexto:** investigação a fundo pedida pelo usuário depois de a execução real mostrar corpo
que não aparece, fila travada e erro de concorrência a cada clique. Cinco frentes, 34 agentes,
11 achados confirmados e 17 refutados.

**Reproduzido contra o banco real antes de qualquer correção** (`TrackedGraphTests`): três
falhas, `MessageBody`, `Attachment` e `MessageAddress`. Não é corrida — é determinístico, com
uma conexão, sem sincronização, sem remoção de linha nenhuma.

`Entity` sempre atribui a chave no construtor. É convenção deliberada do projeto:
`Guid.CreateVersion7()` é ordenado no tempo e preserva a localidade dos índices do SQLite em
caixas com centenas de milhares de mensagens. O efeito colateral não é óbvio — o EF Core
decide `Added` × `Modified` por **`IsKeySet`**. Chave preenchida ⇒ ele assume linha existente
⇒ `UPDATE ... WHERE Id = @p` ⇒ zero linhas ⇒ `DbUpdateConcurrencyException`.

Isso só acontece quando o filho é descoberto pela **navegação de um pai já rastreado**. Quando
o grafo inteiro é novo, o pai é `Added` e o filho vai junto — que é exatamente como todos os
testes montavam o cenário, e por isso nenhum dos 1029 o alcançava.

**Decisão:** `AddBody`, `AddAttachment` e `AddAddress` no `IMessageRepository`, chamados
**além** da navegação. A navegação é o que a tela lê logo em seguida; a inserção é o que grava.

**O que eu diagnostiquei errado, e fica registrado:** eu havia concluído que a linha era
*removida* pela sincronização (D-041), e escrevi na tela "alterada pela sincronização enquanto
era aberta". A linha nunca existiu. A mensagem descrevia uma corrida que não houve e mandava
o usuário repetir uma ação que nunca funcionaria. D-041 continua correto como **tratamento** —
um clique não pode derrubar a aplicação —, mas o texto precisa deixar de acusar a
sincronização.

**Duas correções que parecem resolver e não resolvem**, verificadas em matriz 2×2 durante a
investigação: `ValueGeneratedNever()` no mapeamento, e `Entry(filho).State = Added` antes do
`DetectChanges` — o estado é repintado depois. O discriminador é `IsKeySet`, e nada mais.

**O alcance é maior que o corpo da mensagem.** Sem persistir, `MessageBody.DownloadedAt` fica
nulo para sempre: o curto-circuito de idempotência nunca passa a valer, todo clique refaz o
`FETCH` pela rede, e ao reabrir o aplicativo não há corpo nenhum. O índice FTS também nunca é
alimentado — a busca era a terceira vítima, silenciosa.
