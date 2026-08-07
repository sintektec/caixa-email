# Decisões arquiteturais

Registro das escolhas cuja motivação não é óbvia no código, e do que aconteceria se cada
uma fosse revertida.

## 1. .NET 10 LTS em vez do .NET 8 pedido na especificação

A especificação pede ".NET 8 ou superior". Verificado no índice oficial `dotnet/core` em
03/08/2026:

| Versão | Tipo | Fase | Fim do suporte |
|---|---|---|---|
| .NET 8 | LTS | manutenção | **10/11/2026** |
| .NET 9 | STS | manutenção | **10/11/2026** |
| .NET 10 | LTS | ativo | 14/11/2028 |

.NET 8 e .NET 9 saem de suporte na mesma data, a cerca de três meses do início do
projeto. Escolher qualquer um dos dois significaria migrar durante a própria construção.
.NET 10 atende ao "ou superior" e dá dois anos de folga.

O Windows App SDK também mudou de numeração: está em **2.3.x**, não na série 1.x. O
`Microsoft.WindowsAppSDK.WinUI` tem TFM `net6.0-windows10.0.17763.0`, compatível com
`net10.0-windows`.

## 2. `Microsoft.Data.Sqlite.Core` em vez do pacote agregador

O pacote `Microsoft.Data.Sqlite` traz `SQLitePCLRaw.bundle_e_sqlite3` como dependência.
Esse bundle registra um provider **sem** suporte a criptografia e vence a corrida de
inicialização contra o `bundle_e_sqlcipher`.

A falha é silenciosa: a aplicação abre, grava, lê — e o arquivo fica em texto claro.
Nenhuma exceção, nenhum aviso.

Usamos `Microsoft.Data.Sqlite.Core` (sem bundle) mais `SQLitePCLRaw.bundle_e_sqlcipher`
explicitamente. As versões precisam permanecer alinhadas: `Microsoft.Data.Sqlite.Core`
10.0.10 depende de `SQLitePCLRaw.core` 2.1.11, que é exatamente o que
`bundle_e_sqlcipher` 2.1.11 traz.

`SqlCipherConnectionFactory.VerifyEncryptionAsync` e o teste
`ArquivoCifrado_NaoExpoeTextoEmClaro` existem para transformar essa falha silenciosa em
erro visível — o segundo varre os bytes crus do arquivo gravado procurando o assunto de
uma mensagem de teste.

## 3. Credential Manager em vez de PasswordVault

`Windows.Security.Credentials.PasswordVault` é a API mais conveniente, mas exige
**identidade de pacote**: só funciona em aplicativos MSIX.

A decisão de empacotamento é dual — MSIX para distribuição e unpackaged para depuração e
cenários corporativos legados. O PasswordVault falharia em metade dos casos.

`CredRead`/`CredWrite`, acessadas por P/Invoke gerado pelo CsWin32, funcionam nos dois
modos. O custo é código não gerenciado em `WindowsCredentialStore`, contido em uma única
classe.

## 4. Camada `Infrastructure.Windows` separada

Sem essa separação, `Infrastructure` inteira precisaria do TFM `net10.0-windows` e
deixaria de compilar em Linux — o que eliminaria a possibilidade de testar o núcleo fora
do Windows.

Com ela, quatro das seis camadas compilam e são testadas em qualquer sistema operacional.
O job `core` do CI roda em Linux justamente para que essa fronteira seja verificada
mecanicamente: uma chamada Windows-only que vaze para as camadas de núcleo quebra a build.
`CA1416` está configurado como erro pelo mesmo motivo.

## 5. `MessageAddresses` como tabela própria e indexada

A alternativa seria guardar destinatários como texto único na tabela de mensagens.

A regra de Diretório de Domínio pergunta, a cada movimentação, "esta mensagem tem algum
participante em `sintek.com.br`?". Com os endereços em texto, responder exigiria ler e
interpretar cada mensagem da pasta. Com a tabela normalizada e o domínio já extraído e
indexado, vira uma busca por índice.

Isso importa em dois momentos: no arrastar e soltar, que acontece a cada gesto do usuário,
e na revalidação disparada por troca de domínio de um diretório, que percorre a caixa
inteira.

## 6. `DateTimeOffset` em vez de `DateTime`

A especificação esboça `DateTime`. O cabeçalho `Date` da RFC 5322 carrega deslocamento de
fuso, e o MimeKit o entrega como `DateTimeOffset`.

Converter para `DateTime` descartaria o deslocamento e faria uma mensagem enviada às 23h
em UTC-03:00 aparecer no dia seguinte para quem está em UTC. Em um cliente corporativo com
contas em fusos diferentes, isso é erro visível.

## 7. GUID v7 em vez de v4

GUIDs v7 embutem o instante de criação e são monotonicamente crescentes. Como chave
primária no SQLite, mantêm a localidade das páginas do índice.

Com v4 aleatório, cada inserção cai em uma página distinta da árvore. Em uma caixa postal
com centenas de milhares de mensagens, a diferença aparece como lentidão progressiva da
sincronização.

## 8. Fila de saída sequencial, não paralela

`OutboxProcessor` processa uma operação por vez, por conta, em ordem de `Sequence`, e
interrompe o lote na primeira falha.

Paralelizar parece a otimização óbvia e quebra a semântica: "mover para Arquivados"
seguido de "marcar como lida", aplicados fora de ordem, fazem a segunda operação procurar
a mensagem na pasta errada. Pular a operação que falhou tem o mesmo efeito, porque as
seguintes assumem o estado que ela deixaria.

O índice único `(AccountId, Sequence)` é a garantia final: se duas escritas concorrentes
calcularem o mesmo número, a segunda falha em vez de embaralhar a ordem.

## 9. Sanitização em três camadas

1. `MessageHtmlSanitizer` remove conteúdo ativo e neutraliza referências remotas.
2. `WebView2` roda sem scripts, sem DevTools e com navegação cancelada.
3. Uma CSP restritiva no documento envolvente recusa script e recurso externo.

Nenhuma sozinha basta. Corpo de e-mail é conteúdo hostil por definição — qualquer pessoa
pode enviá-lo.

Imagens remotas ficam bloqueadas por padrão não por banda: carregar uma imagem hospedada
pelo remetente confirma a ele que a mensagem foi aberta, quando, e de qual endereço IP.

## 10. `KeepChildNodes = false` no sanitizador

Com a opção ligada, o conteúdo de uma tag removida sobrevive como texto. O corpo de um
`<script>` passaria a ser exibido ao usuário como se fosse parte da mensagem — inerte, mas
confuso, e revelando o payload de um ataque. Um teste cobre esse caso.

## 11. FTS5 em migração de SQL puro

O EF Core não modela tabelas virtuais. O índice usa *external content* sobre a tabela
`MessagesSearch`, e não o modo contentless (D-015): apagar uma entrada contentless exige
reapresentar os valores antigos, e corpo, participantes e anexos vivem em outras tabelas —
um gatilho delas não tem como sabê-los.

O tokenizador usa `remove_diacritics 2`, sem o qual buscar "orcamento" não encontraria
"Orçamento" — o caso mais comum de busca em português.

A sincronização é feita por gatilhos, e não em código, para que o índice permaneça correto
mesmo quando as tabelas forem alteradas fora dos casos de uso.

O parâmetro `Guid` em SQL manual vai como `Guid`, nunca como `ToString()`: o provider grava
Guid como TEXT **maiúsculo**, `ToString()` produz minúsculo, e a comparação devolve zero
linhas sem erro algum.

## 12. AwesomeAssertions em vez de FluentAssertions

A versão 8 do FluentAssertions passou a exigir licença paga para uso comercial.
AwesomeAssertions é o fork livre (Apache-2.0) com a mesma API.

## 13. Uma porta para agenda, três protocolos, e o CalDAV primeiro

`ICalendarSyncProvider` é a porta única para servidor de agenda. Três implementações
previstas, e a divisão não foi escolha de gosto: o Exchange Online **nunca** implementou
CalDAV, e o EWS está sendo desligado (bloqueio global em 01/10/2026, remoção até 04/2027).
Para Microsoft 365 o único caminho suportado é o Microsoft Graph. CalDAV é o padrão aberto
que cobre todo o resto. Ver D-026.

Consequência que não é adaptação, mas decisão nova: **o Graph não expõe `SEQUENCE`**. A
regra de D-024 — versão menor nunca sobrescreve maior — vale no caminho CalDAV, que carrega
o iCalendar íntegro, e não atravessa para o Graph.

**O envio vem antes da leitura**, pelo mesmo motivo que a fila de saída drena antes do IMAP:
enquanto o local não subiu, o servidor não sabe do que o usuário fez offline, e ler primeiro
traria o estado antigo e desfaria a edição dele. O compromisso voltaria para o horário
anterior e o envio seguinte o moveria de novo — um pisca-pisca que parece defeito e é.

**Coleção que some da listagem não é apagada**, como a pasta que some do `LIST`: desliga a
sincronização e preserva o conteúdo. Uma resposta incompleta do servidor é indistinguível de
uma exclusão real, e perder a agenda de um cliente por causa de um 500 momentâneo é o erro
caro.

**Falha de um calendário não derruba os outros.** Uma coleção com permissão revogada
registra o motivo e a passada segue; abortar o ciclo faria uma coleção quebrada esconder a
atualização de todas as demais.

O `SyncToken` e o `CTag` são tratados como blob de texto. São uma URI no CalDAV, um
`deltaLink` no Graph e um `syncToken` na Google — nos três, o servidor emite e o cliente
devolve sem interpretar. Extrair número, comparar ordem ou gerar um valor quebra nos três, e
quebra em silêncio: o servidor aceita o token inventado e devolve o conjunto errado de
mudanças.

## 14. Um formato de domínio na porta de agenda, três protocolos atrás dela

`ICalendarSyncProvider` troca `CalendarEventData`. Só o CalDAV fala iCalendar; o Microsoft
Graph e a Google Calendar API falam JSON com campos nomeados. Obrigá-los a sintetizar um
documento iCalendar para o motor reinterpretar seria inventar um formato intermediário — e uma
segunda chance de errar em cada caminho. O documento cru viaja junto quando existe, para ser
preservado, não para ser lido de novo. Ver D-030.

**A precedência precisou de regra própria, e é a parte que mais custa se estiver errada.** O
`SEQUENCE` da RFC 5545 protege contra convite antigo que chega atrasado (D-024). O Graph não o
expõe, e a Google não o expõe na API. `RemoteVersion` carrega os dois critérios possíveis, e
`AllowsVersion` só compara o que existir dos dois lados: `SEQUENCE` com `SEQUENCE`, instante com
instante. Comparar um contador de revisão com um instante produziria recusa arbitrária, porque
um servidor que reescreve o objeto ao gravar move o segundo sem tocar no primeiro. Ver D-029.

**O Graph obrigou a abrir mão do `delta`.** O único disponível em `v1.0` é
`/me/calendarView/delta`, que exige janela de datas e expande a recorrência em ocorrências. Este
produto guarda o mestre com a `RRULE` e expande ao desenhar a grade — é o que permite editar "a
série". A leitura passou a ser `events` com `$filter` em `lastModifiedDateTime`, que preserva o
mestre e não reporta exclusão; a exclusão fica com a passada completa periódica, que é o que
`IsFullEnumeration` autoriza. Ver D-031.

**A escrita de recorrência no Graph só produz o que a leitura relê.** `ToRecurrence` aceita
exatamente o conjunto que `ToRRule` devolve — diária, semanal com dias, mensal por dia do mês,
anual, com contagem ou data-limite — e nada além. O critério não é o que o Graph suporta: um
padrão que a leitura não entendesse faria o compromisso subir como série e voltar como encontro
único na sincronização seguinte, uma divergência que aparece sozinha, sem ninguém ter tocado
nele. Por isso os padrões `relative*` ("a segunda terça do mês") ficam fora dos dois lados —
entram juntos ou não entram.

Duas consequências que parecem detalhe e não são. Primeira: parte `BY*` que não valha para a
frequência recusa a regra inteira. `FREQ=MONTHLY;BYDAY=2TU` descartado em silêncio não vira
tradução incompleta, vira "dia 10 de todo mês" — outra série, com aparência de correta.
Segunda: a recorrência tem três estados no corpo enviado, não dois. Regra traduzível manda o
objeto; ausência de regra manda `null` explícito, porque num `PATCH` campo ausente significa
"não mexa" e a remoção nunca chegaria ao servidor; regra sem tradução fiel **omite o campo**,
porque mandar nulo ali apagaria a série que não soubemos ler. Ver D-033.
