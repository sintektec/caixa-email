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

O EF Core não modela tabelas virtuais. A tabela `MessagesFts` usa modo *contentless*
(`content=''`): guarda apenas o índice invertido, sem duplicar o texto já presente em
`Messages` e `MessageBodies` — o que evitaria praticamente dobrar o tamanho do banco.

O tokenizador usa `remove_diacritics 2`, sem o qual buscar "orcamento" não encontraria
"Orçamento" — o caso mais comum de busca em português.

A sincronização é feita por gatilhos, e não em código, para que o índice permaneça correto
mesmo quando as tabelas forem alteradas fora dos casos de uso. No modo contentless, o FTS5
exige apagar a entrada antiga com o comando `'delete'` antes de reinserir; um `UPDATE`
direto corromperia o índice.

## 12. AwesomeAssertions em vez de FluentAssertions

A versão 8 do FluentAssertions passou a exigir licença paga para uso comercial.
AwesomeAssertions é o fork livre (Apache-2.0) com a mesma API.
