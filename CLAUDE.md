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

**Um `ContentDialog` por vez.** Abrir o segundo enquanto o primeiro está aberto não empilha:
simplesmente não aparece. As telas de configuração se chamam fechando a atual e sinalizando
a próxima (`SettingsFollowUp`, `RequestedDirectoryCreation`), e o encadeamento acontece em
laço no `MainWindow`.

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
