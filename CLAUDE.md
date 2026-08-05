# Sintek.Mail

Cliente de e-mail desktop para Windows 11 cuja característica definidora é a organização
rigorosa por **Diretório de Domínio** (`Domínio → Conta → Pastas`), com operação
offline-first e base local criptografada.

O layout de diretórios, a stack e os comandos de build são deriváveis do `.sln` e dos
`.csproj` — este arquivo cobre apenas o que o código não conta sozinho.

## Regras invioláveis

**Toda movimentação de mensagem passa por `MoveMessageHandler`.** Arrastar e soltar, menu
de contexto, regras automáticas e classificação durante a sincronização — todos. O
handler é o único lugar que consulta o `DomainMembershipEvaluator`. Reimplementar a
verificação na interface ou no motor de sincronização faz as versões divergirem, e a
divergência sempre termina com a interface permitindo o que o domínio proíbe.

**Nenhum segredo entra no banco de dados.** Senhas, tokens OAuth e a chave do SQLCipher
vivem exclusivamente no Windows Credential Manager, acessados por `ICredentialStore`. As
entidades guardam apenas o identificador da credencial (`Account.CredentialKey`). Isso
vale também para logs e para o `PayloadJson` da fila de saída.

**A auditoria não registra conteúdo de mensagem.** `AuditLogEntry` aceita identificadores,
tipo de evento e o motivo da decisão. Assunto, corpo, nome de anexo e endereços de
participantes ficam de fora, inclusive no `DetailsJson`.

**`Sintek.Mail.Domain` não referencia nenhum outro projeto nem pacote NuGet.** É o que o
mantém testável sem banco, sem rede e em qualquer sistema operacional. Se uma regra
precisa de infraestrutura para ser expressa, ela está no lugar errado.

**Código Windows-only vive só em `Infrastructure.Windows` e `App`.** As demais camadas
compilam e são testadas em Linux, e o job `core` do CI existe para quebrar quando isso
deixar de ser verdade. O analisador `CA1416` está configurado como erro pelo mesmo motivo.

**ViewModel novo nasce em `Sintek.Mail.Presentation`, não em `App`.** O projeto é
multiplataforma de propósito: é o que permite testar validação de formulário, etapas de
assistente e decisão de confirmação no job Linux, em segundos. Em `App` fica só o que
depende do WinUI — janela, XAML, WebView2 e o encadeamento de `ContentDialog`. Um ViewModel
colocado em `App` só é verificável no job Windows, e foi assim que a fase 1 gastou quatro
rodadas de CI com erros que um teste local teria pego.

**Documento de autoconfiguração vindo da rede é lido com DTD desligado.**
`ClientConfigParser` usa `XmlReaderSettings { DtdProcessing = Prohibit, XmlResolver = null }`
porque o host que responde é escolhido pelo domínio que o usuário digitou. Um
`<!ENTITY SYSTEM "file:///...">` transformaria a descoberta de servidores em leitura
arbitrária de disco. A busca é só por HTTPS e com teto de leitura, pelo mesmo motivo.

**HTML de mensagem nunca é renderizado sem passar por `IHtmlSanitizer`.** O `WebView2`
recebe apenas `MessageBody.SanitizedHtml`. `HtmlBody` guarda o original só para
reprocessamento futuro.

**Convite com `SEQUENCE` menor nunca sobrescreve o maior.** `CalendarEvent.ApplyUpdate` e
`Cancel` recusam e devolvem `false`. Convite antigo chega atrasado o tempo todo —
reencaminhado, retido por servidor lento, reprocessado numa ressincronização — e aplicá-lo
mudaria a reunião de volta para o horário errado, com o usuário indo para a sala vazia. A
recusa vira auditoria, nunca silêncio (D-024).

**Movimentação de compromisso passa por `EventMoveEvaluator`.** Como toda movimentação de
mensagem passa por `MoveMessageHandler`: a regra vive num avaliador puro, e arrastar na
grade só executa o que ele permitir. Participante não move a própria cópia de reunião
alheia — a alternativa oferecida é propor novo horário (D-025).

**Conflito de agenda não é resolvido em silêncio.** Quando local e servidor mudam o mesmo
compromisso, `CalendarConflictEvaluator` devolve `Conflict`, o compromisso é marcado e fica
visível até o usuário escolher. Qualquer regra automática — última escrita vence, servidor
vence — descarta o trabalho de alguém, e a pessoa só descobre quando procura o que escreveu
e não acha. É a postura de `InvalidEmailAction.WarnAndConfirm`: onde a decisão custa caro,
quem decide é o usuário (D-027).

**A porta de agenda troca `CalendarEventData`, não texto.** Só o CalDAV fala iCalendar; o
Graph e a Google falam JSON. Obrigá-los a sintetizar um documento para o motor reinterpretar
seria inventar um formato intermediário e uma segunda chance de errar em cada caminho. O
documento cru viaja junto **quando existe** — para ser preservado, não para ser lido de novo —
e quem serializa é o adaptador CalDAV (D-030).

**Precedência só compara critérios do mesmo tipo.** `CalendarConflictEvaluator.AllowsVersion`
usa `SEQUENCE` contra `SEQUENCE` e instante contra instante. Um `SEQUENCE` é contador de
revisão e um instante de alteração é outra grandeza — um servidor que reescreve o objeto ao
gravar move o segundo sem tocar no primeiro, e comparar os dois produziria recusa arbitrária.
Sem critério comum, aplica-se: chegar até a comparação já significa que o `ETag` mudou (D-029).

**Ausência de um recurso só significa exclusão em passada completa.** Quem declara isso é o
provedor, em `RemoteCalendarChanges.IsFullEnumeration` — o motor **não** deduz do token
nulo. Três situações devolvem zero alterações e significam coisas opostas: passada
incremental sem novidade, servidor sem `sync-collection` respondendo "o `CTag` não mudou", e
passada completa de coleção esvaziada. Só a terceira autoriza apagar (D-028).

## Armadilhas conhecidas

**O pacote de SQLite é `Microsoft.Data.Sqlite.Core`, não `Microsoft.Data.Sqlite`.** O
pacote agregador traz `bundle_e_sqlite3`, que registra um provider sem criptografia e
vence a corrida de inicialização com o `bundle_e_sqlcipher`. O sintoma é cruel: tudo
funciona, o banco abre normalmente, e o arquivo fica em claro. `SqlCipherDatabaseTests`
verifica isso lendo os bytes crus do arquivo.

**Não ligar `Cache=Shared` na connection string.** Combinado com WAL, derruba o processo
com falha nativa — sem exceção gerenciada, sem mensagem.

**`Message.MarkPending` nunca rebaixa um estado mais forte.** Uma mensagem pendente de
exclusão que tem o marcador de leitura alterado continua pendente de exclusão. Reverter
isso faz a fila propagar o marcador e esquecer a exclusão, e a mensagem reaparece na
sincronização seguinte. `Restore` é a exceção deliberada e atribui o estado diretamente.

**A fila de saída drena antes da leitura do servidor.** `SyncAccountHandler` conecta, drena
e só então lê. Ler primeiro traria o estado antigo e desfaria localmente o que o usuário fez
offline — o marcador voltaria atrás e a fila o refaria em seguida, um pisca-pisca que parece
defeito e é. Pelo mesmo motivo, marcador vindo do servidor não sobrescreve mensagem cujo
`SyncState` não seja `Synced`.

**Pasta que some da listagem do servidor não é apagada.** `FolderMirrorService` desliga a
sincronização e preserva o conteúdo. Uma resposta de `LIST` incompleta é indistinguível de
uma exclusão real, e o custo dos dois erros não é simétrico.

**A fila de saída é estritamente sequencial por conta.** `OutboxProcessor` interrompe o
lote na primeira falha em vez de pular para a próxima operação, porque as seguintes
dependem do estado que a anterior deixaria. Paralelizar parece uma otimização óbvia e
quebra a semântica.

**Migrações do EF Core exigem build entre uma e outra.** Criar duas migrações seguidas com
`--no-build` faz a segunda usar o assembly antigo e regenerar o schema inteiro.

**FTS5 é criado em SQL puro.** O EF Core não modela tabelas virtuais. Alterações no
índice de busca vão em migração dedicada com `migrationBuilder.Sql()`. O índice usa
*external content* sobre a tabela `MessagesSearch`, não o modo contentless: apagar uma
entrada contentless exige reapresentar os valores antigos, e corpo, participantes e anexos
vivem em outras tabelas — um gatilho delas não tem como sabê-los (D-015).

**Parâmetro `Guid` em SQL manual vai como `Guid`, nunca como `ToString()`.** O provider
grava Guid como TEXT **maiúsculo**; `ToString()` produz minúsculo, e a comparação devolve
zero linhas sem nenhum erro. `Fts5SearchService` passa o `Guid` cru e deixa a conversão com
o `Microsoft.Data.Sqlite`.

**Propriedade ligada a `TextBox.Text` ou `PasswordBox.Password` não pode ser nula.** O WinUI
lança em tempo de execução ao receber `null` nesses controles, e nada disso aparece na
compilação. Os ViewModels usam `string` com `string.Empty`; os casos de uso já tratam vazio
como ausente. O mesmo vale para `TextBlock.Text` — daí `DomainNameError` devolver vazio em
vez de nulo.

**`NumberBox.Value` é `double`.** Ligar uma propriedade `int` de duas vias a ele faz o
compilador de XAML recusar a conversão. A saída é expor a propriedade em `double` no
ViewModel (`ImapPortValue`), com a conversão explícita ali.

**`InvariantGlobalization` está ligado, e isso quebra duas coisas em silêncio.**
`string.Normalize` não decompõe nada — o acento sobrevive como caractere único —, então
comparação sem acento usa mapeamento explícito (`SenderTrustEvaluator.Normalize`). E
`CultureInfo.GetCultureInfo("pt-BR")` **lança em tempo de execução**: datas e números usam
formato explícito com `CultureInfo.InvariantCulture`, que é onde o padrão brasileiro já está
escrito. As duas pegadinhas custaram uma rodada de testes cada: a primeira fez "JOAO SILVA"
deixar de casar com "João Silva" na detecção de remetente disfarçado, que é exatamente o caso
que a função existe para pegar.

**`InfoBar.ActionButton` aceita um `ButtonBase`, não um painel.** Dois botões lado a lado
vão em `InfoBar.Content`. O erro (`WMC0015`) só aparece na compilação do XAML, que não roda
fora do Windows — o job `windows` do CI é o primeiro lugar onde ele existe.

**Um `ContentDialog` por vez.** Abrir o segundo enquanto o primeiro está aberto não empilha:
simplesmente não aparece. As telas de configuração se chamam fechando a atual e sinalizando
a próxima (`SettingsFollowUp`, `RequestedDirectoryCreation`), e o encadeamento acontece em
laço no `MainWindow`.

**O `AutoSuggestBox` escreve o item escolhido na caixa antes de avisar quem o escolheu.** Com
ligação de duas vias, a propriedade do ViewModel já chega destruída em `QuerySubmitted` — os
outros destinatários do campo se perderam. Por isso o `ComposerViewModel` guarda o texto do
momento em que montou a lista (`_suggestionBaseText`) e aplica a troca sobre ele, nunca sobre
a propriedade atual.

**O provedor do SQLite não ordena nem compara `DateTimeOffset`.** `ORDER BY` lança
`NotSupportedException`; `<=` e `<` simplesmente não traduzem e viram
`InvalidOperationException`. Nada disso aparece na compilação — quebra na primeira execução
da consulta. O motivo é legítimo: o EF grava o tipo como texto preservando o fuso original, e
a ordem lexicográfica desse texto não é a cronológica quando duas linhas têm fusos
diferentes, o que acontece de verdade porque o cabeçalho `Date` da RFC 5322 traz o
deslocamento de quem enviou.

Toda consulta em LINQ que ordena ou compara data passa por `SqliteFunctions.JulianDay(...)`,
mapeada para o `julianday()` embutido do SQLite: ele interpreta o texto com o fuso declarado
e devolve um número, que o provedor aceita nos dois casos. Ordenação leva junto um desempate
estável por `Id`. Isso já custou quatro consultas: a listagem de mensagens da pasta (a tela
principal), o registro de auditoria, a limpeza de cache e — a pior — a fila de saída, que
nunca drenaria.

**Documento iCalendar da rede é lido sem nunca lançar.** `IcalNetCalendarSerializer.Read`
devolve `null` no lugar de propagar exceção. O documento vem de um anexo escolhido por quem
enviou a mensagem, e uma exceção derrubaria a sincronização da conta inteira por causa de
uma mensagem malformada — que é rotina, não exceção. Mesmo raciocínio do `VCardSerializer`.

**`Ical.Net` inventa um `UID` quando o documento não traz — e diferente a cada leitura.**
Por isso o `UID` sozinho não serve de identidade: o `ImportInvitationHandler` tem uma
segunda via, pela mensagem em que o convite chegou. Sem ela, rebaixar o corpo criaria um
compromisso novo a cada vez.

**Resposta a convite sai como parte `text/calendar`, nunca como anexo.** `CalendarPartBuilder`
a coloca em um `multipart/alternative` com o parâmetro `method` no `Content-Type`, como a
RFC 6047 exige. É isso que faz o cliente do organizador atualizar o `PARTSTAT` sozinho; como
anexo, ele mostraria um `.ics` para a pessoa abrir à mão.

**`Calendar.GetOccurrences` do Ical.Net devolve uma sequência infinita.** Uma `RRULE` sem
`COUNT` nem `UNTIL` não termina. Toda expansão leva um `TakeWhile` com o fim da janela —
sem ele o laço roda para sempre.

**`AllowAutoRedirect` ligado quebra CalDAV de dois jeitos.** O `HttpClient` transforma um
`PROPFIND` em `GET` ao seguir 301/302/303, e **descarta o header `Authorization` quando o
destino é outro host** — que é exatamente o caso do iCloud, cujo `calendar-home-set` aponta
para a partição da conta em outro nome de servidor. O sintoma é um 401 inexplicável logo
depois de uma autenticação que funcionou. `CalDavTransport` segue o `Location` à mão, com
teto de saltos e recusando destino que não seja HTTPS.

**`response.Headers.ETag` lança em servidor fora da norma.** Radicale antigo e alguns
gateways devolvem o ETag sem aspas, e a propriedade tipada lança `FormatException` ao
analisá-lo. Leia com `TryGetValues("ETag", ...)` e guarde a string crua **com as aspas**:
`"2134-314"` e `2134-314` são valores diferentes para o `If-Match`. ETag fraco (`W/"..."`)
não serve para pré-condição e é descartado, o que força a releitura por `GET`.

**No WebDAV, o discriminador entre "alterado" e "removido" é onde o `status` está.** Filho
direto da `<D:response>` fala do recurso; dentro de um `<D:propstat>` fala de uma
propriedade que não existe naquele recurso. O código é `404` nos dois. Ler só o primeiro
`propstat` faz "esta propriedade não veio" virar "este recurso foi apagado".

**`DAV:` é literal, e os prefixos XML são arbitrários.** É esse texto mesmo, com
dois-pontos e sem `http://`. Um servidor escreve `<D:response>`, outro `<d:response>`, outro
`<dav:response>`, e todos estão certos. Casar por prefixo, ou usar `Element("response")` sem
namespace, devolve zero elementos **sem erro nenhum**. Sempre `XNamespace` + nome local.

**`StringContent` recusa media type com parâmetro.** `new StringContent(body, Encoding.UTF8,
"text/calendar; charset=utf-8")` lança `FormatException`: o `charset` vem da codificação, e
o terceiro argumento aceita só o tipo. O erro não aparece na compilação — quebra na primeira
escrita.

**`StringWriter` declara UTF-16 no XML que sai em UTF-8.** `XDocument.Save(TextWriter)`
escreve na declaração o `Encoding` que o escritor informa, e o `StringWriter` comum informa
`Encoding.Unicode`. O documento sai com `encoding="utf-16"` enquanto os bytes vão em UTF-8 —
servidor estrito recusa, tolerante decodifica errado o primeiro acento. `CalDavRequests` usa
um escritor que sobrescreve `Encoding`.

**No CalDAV, o nome do recurso não tem relação com o `UID`.** Que muitos servidores usem
`{UID}.ics` é coincidência, não contrato: a Google usa identificadores internos e o iCloud
renomeia. `href` e `UID` são guardados separados — o primeiro é a identidade de rede, o
segundo é a de calendário. E o `href` volta ora absoluto, ora relativo, ora
percent-encoded de formas diferentes: `CalDavHref` resolve contra a URI da requisição e
guarda a forma canônica.

**Sem ETag forte na resposta do `PUT`, é obrigatório reler.** A RFC 4791 §5.3.4 **proíbe**
ETag forte quando o servidor modifica o objeto ao gravar — e eles modificam: normalizam
fuso, injetam `SEQUENCE`, reescrevem `DTSTAMP`. Guardar um ETag adivinhado faz o `If-Match`
seguinte falhar com 412 para sempre, ou pior: sobrescrever em silêncio o que o servidor
gravou.

**No Entra ID o token é emitido por recurso, e no Google não.** Pedir
`outlook.office.com/IMAP.AccessAsUser.All` e `graph.microsoft.com/Calendars.ReadWrite` na mesma
chamada do MSAL é recusado — são públicos diferentes, e o token do IMAP não abre o Graph. Daí
`IOAuthProvider.GetAccessTokenAsync` ter a sobrecarga que recebe escopos, e o consentimento
interativo da Microsoft pedir os dois em sequência. A Google faz o oposto: um token só, com
todos os escopos consentidos, e pedir um subconjunto não produziria outro token — produziria
outra ida ao consentimento, sem ganho nenhum.

**O `delta` de calendário do Graph expande a recorrência.** O único disponível em `v1.0` é
`/me/calendarView/delta`, que exige janela de datas e devolve **ocorrências**, não o mestre com
`RRULE`. Uma reunião semanal de um ano vira 52 objetos. A leitura usa `events` com `$filter` em
`lastModifiedDateTime`, que preserva o mestre — ao custo de não reportar exclusão, que fica com
a passada completa periódica (D-031).

**`JsonNode.ToJsonString()` escapa não-ASCII.** "Reunião" sai como `Reuni\u00E3o`. É JSON
válido e as duas APIs aceitam; o que quebra é a asserção de teste escrita com o acento literal.

**`dotnet ef migrations add --no-build` usa o assembly de `Debug`.** Compilar só em `Release`
antes de criar a migração faz o EF ler o modelo antigo e gerar a migração anterior de novo —
o sintoma é uma migração nova com o conteúdo da última. Compilar em `Debug` antes resolve.

## Convenções

Testes nomeados `Metodo_Cenario_ResultadoEsperado`, em português, refletindo o vocabulário
da especificação. Os que cobrem as tabelas das seções 5.2 a 5.4 são a rede de segurança da
regra de domínio: se um deles for alterado, a mudança precisa ser intencional.

Assertions com **AwesomeAssertions**, não FluentAssertions — a v8 do FluentAssertions
exige licença paga para uso comercial.

Senha em teste vem de `FakeSecret.For("rotulo")`, nunca de um literal ao lado de um campo
chamado `Password`. O detector de segredos do CI não distingue credencial real de valor de
teste e reprova o PR; pior que o atraso é o hábito de ignorar o alerta, que um dia estará
certo.

Entidades recebem o instante como parâmetro (`DateTimeOffset now`) em vez de ler o
relógio. A camada de Aplicação o obtém de `TimeProvider`.

Identificadores são `Guid.CreateVersion7()`: são ordenados no tempo e preservam a
localidade dos índices do SQLite, o que importa em caixas com centenas de milhares de
mensagens.

Commits e comentários em português; nomes de código em inglês.

## Referências

Este arquivo cobre as regras do **código**. O comportamento esperado de uma sessão de IA
neste repositório está em `AGENTS.md`, que aponta para o harness de memória:

- `AGENTS.md` — diretrizes de conduta e ordem de leitura no início da sessão
- `harness/STATUS.md` — onde o projeto está agora
- `harness/DECISIONS.md` — log append-only de decisões técnicas; não reabrir sem evidência nova
- `harness/CONTEXT.md`, `harness/GLOSSARY.md`, `harness/SESSIONS.md`

Documentação do projeto:

- `docs/decisoes-arquiteturais.md` — o porquê das escolhas não óbvias e o que quebra se
  forem revertidas
- `docs/modelo-de-dados.md` — entidades, índices e o que nunca é persistido
- `docs/roadmap.md` — fases seguintes
- `spec/` — especificação funcional original
