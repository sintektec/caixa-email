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

**A fila de saída é estritamente sequencial por conta.** `OutboxProcessor` interrompe o
lote na primeira falha em vez de pular para a próxima operação, porque as seguintes
dependem do estado que a anterior deixaria. Paralelizar parece uma otimização óbvia e
quebra a semântica.

**Migrações do EF Core exigem build entre uma e outra.** Criar duas migrações seguidas com
`--no-build` faz a segunda usar o assembly antigo e regenerar o schema inteiro.

**FTS5 é criado em SQL puro.** O EF Core não modela tabelas virtuais. Alterações no
índice de busca vão em migração dedicada com `migrationBuilder.Sql()`.

## Convenções

Testes nomeados `Metodo_Cenario_ResultadoEsperado`, em português, refletindo o vocabulário
da especificação. Os que cobrem as tabelas das seções 5.2 a 5.4 são a rede de segurança da
regra de domínio: se um deles for alterado, a mudança precisa ser intencional.

Assertions com **AwesomeAssertions**, não FluentAssertions — a v8 do FluentAssertions
exige licença paga para uso comercial.

Entidades recebem o instante como parâmetro (`DateTimeOffset now`) em vez de ler o
relógio. A camada de Aplicação o obtém de `TimeProvider`.

Identificadores são `Guid.CreateVersion7()`: são ordenados no tempo e preservam a
localidade dos índices do SQLite, o que importa em caixas com centenas de milhares de
mensagens.

Commits e comentários em português; nomes de código em inglês.

## Referências

- Especificação funcional e decisões: `docs/`
- Fases seguintes do roadmap: `docs/roadmap.md`
