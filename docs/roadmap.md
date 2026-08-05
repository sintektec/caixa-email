# Roadmap

O escopo acordado é a **aplicação completa** da especificação, não um MVP. Esta é a ordem
de construção; cada fase termina com o que a anterior tornou possível.

## Fase 1 — Fundação ✅

Solution, camada de Domínio com a regra de Diretório de Domínio completa, camada de
Aplicação com os casos de uso críticos, persistência criptografada com migrações e busca
FTS5, infraestrutura de e-mail e OAuth, camada Windows, esqueleto da interface WinUI 3 e
CI nos dois sistemas operacionais.

169 testes cobrem as tabelas de validação das seções 5.2 a 5.4, a máquina de estados
offline, a criptografia do banco e a sanitização de HTML.

## Fase 2 — Contas ✅

Assistente de configuração em quatro etapas, descoberta automática completa, fluxo
interativo de OAuth para Microsoft e Google, teste de credenciais isolado do cadastro,
edição e remoção de contas e de Diretórios de Domínio com relatório de impacto e
confirmação.

A descoberta tenta, nesta ordem: tabela de provedores conhecidos, autoconfig publicado pelo
próprio domínio, registros SRV do DNS (RFC 6186), banco ISPDB da Mozilla e, por último, as
convenções usuais. A origem viaja no resultado e aparece na tela — o usuário precisa saber
a diferença entre "o domínio declarou isto" e "chutamos isto".

Os ViewModels passaram para `Sintek.Mail.Presentation`, multiplataforma, e são testados no
job Linux junto com o núcleo (ver `DECISIONS.md`, D-010).

## Fase 3 — Sincronização ✅

Espelhamento da árvore de pastas, sincronização incremental por UID, ressincronização
completa quando o `UIDVALIDITY` muda, reconciliação de mensagens apagadas fora do cliente e
`IDLE` para recebimento imediato, com retorno a sondagem periódica onde o servidor não o
oferece.

A fila de saída passou a cobrir envio, rascunhos, cópia e as operações de pasta, e está
visível na interface — no modo offline-first, "já enviei" e "ainda não saiu daqui" são
estados diferentes que o usuário precisa distinguir.

Três decisões governam o ciclo e estão em `DECISIONS.md`: a fila drena antes da leitura
(D-011), pasta ausente na listagem não é apagada (D-012) e a classificação na chegada tem
tabela de decisão própria (D-013).

`CONDSTORE` (RFC 7162) entrou depois: quando a pasta já tem um `MODSEQ` conhecido, o
servidor devolve apenas os marcadores alterados desde então, em vez de a pasta inteira ser
relida a cada ciclo. Alteração local pendente continua tendo precedência — deixar o
servidor vencer desfaria diante dos olhos do usuário o que ele fez offline. Servidor sem
CONDSTORE cai na reconciliação por UID, correta e mais cara.

## Fase 4 — Leitura e composição ✅

Painel de leitura completo com anexos, download sob demanda de corpo e anexos, compositor
com Para/CC/CCO, rascunho automático, resposta, resposta a todos, encaminhamento e
encaminhamento como anexo, assinaturas por conta e aviso de anexo esquecido.

Inclui a **leitura do veredito do servidor sobre a mensagem**: os cabeçalhos de autenticação
(`Authentication-Results`, com SPF, DKIM e DMARC) e os de classificação (`X-Spam-Flag`,
`X-Spam-Score`). O painel de leitura exibe a faixa de confiança e o aviso de remetente
disfarçado — nome exibido igual ao de um contato conhecido, domínio diferente —, que é o
vetor de phishing que mais funciona na prática.

Enviar significa entregar à fila: a mensagem vai para a Caixa de Saída local e o SMTP
acontece quando a fila drenar (D-014). O botão funciona num avião. As duas pendências da
fase foram fechadas depois: o compositor ganhou editor rico (WebView2 com `contenteditable`
e barra de formatação; o HTML volta ao ViewModel antes de qualquer gravação) e o rascunho
automático grava quando a digitação para pelo período de silêncio
(`ComposerViewModel.AutoSaveQuietPeriod`), sem deixar rascunho em branco para trás.

## Fase 5 — Pastas e regras de domínio na interface ✅

Criação, renomeação e exclusão de pastas, favoritos, arrastar e soltar ligado ao
`MoveMessageHandler`, interface de vínculo de pasta a Diretório de Domínio com a
propagação de herança, pastas de pendências e o fluxo de troca de domínio com relatório de
impacto e confirmação.

Inclui as ações **"Marcar como spam"** e **"Não é spam"**, com propagação correta: mover para
a pasta de lixo eletrônico *e* aplicar os marcadores `$Junk`/`$NotJunk`, que é como
servidores modernos recebem o treinamento. A palavra-chave é enfileirada **antes** da
movimentação — a fila é sequencial e o marcador precisa ser aplicado enquanto o servidor
ainda encontra a mensagem na pasta de origem.

"Não é spam" passa pelo `MoveMessageHandler` como qualquer movimentação: se a Caixa de
Entrada for restrita e a mensagem não pertencer ao domínio, ela vai para pendências — que é
o que a regra manda fazer com ela.

## Fase 6 — Pesquisa ✅

Pesquisa local sobre o índice FTS5 com todos os filtros da seção 6.4, pesquisa avançada
com combinação de critérios e pesquisas salvas.

O índice contentless original não tinha como indexar corpo, participantes e nomes de
anexo — apagar uma entrada exige reapresentar os valores antigos, que vivem em outras
tabelas. A migração `RebuildSearchIndex` o reconstruiu com *external content*: a tabela
física `MessagesSearch` espelha o texto pesquisável e os gatilhos das tabelas de origem a
mantêm, inclusive quando corpo e anexos chegam depois, no download sob demanda.

Entregue: `Fts5SearchService` com os filtros da 6.4 (texto livre e por campo com prefixo e
sem acento; conta, pasta, Diretório de Domínio, categoria, datas com normalização de fuso,
lida, sinalizador, anexos, importância e status de sincronização), pesquisas salvas com
atualização por nome (`SavedSearchesHandler`), flyout de pesquisa avançada na barra
superior e modo de resultados no painel central.

As pesquisas salvas aparecem na barra lateral (seção "Pesquisas salvas", fixadas
primeiro); selecionar uma aplica os critérios aos filtros e executa no painel central. O
filtro por categoria já funciona no serviço, mas só ganhará seletor na interface quando a
fase 7 criar a gestão de categorias.

## Fase 7 — Automação e filtragem local ✅

Editor de regras automáticas, motor de avaliação na chegada (`RuleEvaluator` puro no
Domain; `ApplyArrivalRulesHandler` na Aplicação, ligado ao `MessageSyncService` e ativo só
na Caixa de Entrada), categorias coloridas com atalhos e aplicação pelo menu de contexto,
modelos de mensagem aplicáveis no compositor.

As listas de **remetentes e domínios bloqueados e confiáveis** entraram
(`SenderReputation`, migração `SenderReputationLists`): bloqueado vai direto para o lixo
eletrônico pelo mesmo caminho de "Marcar como spam" — mover *e* aplicar `$Junk`, antes de
qualquer regra rodar; confiável libera as imagens remotas no painel de leitura sem
perguntar. Entrada por domínio cobre os subdomínios.

Toda movimentação decidida por regra passa pelo `MoveMessageHandler`; quando a pasta de
destino é restrita e a mensagem não pertence ao domínio, a ação é registrada como ignorada
em auditoria — não há usuário para confirmar durante a sincronização, e a regra de domínio
prevalece sobre a regra do usuário. A concorrência entre regras (inclusive de Diretórios
de Domínio diferentes) resolve-se pela prioridade configurável de cada regra, com
"interromper as seguintes" como corte explícito.

**Copiar para pasta** e **encaminhamento automático** também executam. A cópia passa por
`MoveMessageHandler.HandleCopyAsync` — a regra de domínio da pasta de destino vale para a
cópia tanto quanto para a movimentação, e cópia incompatível é recusada em qualquer modo:
não existe "desviar a cópia para pendências", que criaria no servidor uma cópia que
ninguém pediu. O encaminhamento baixa corpo e anexos antes e entrega o envio à fila
(D-014); se algum conteúdo não puder ser baixado, o encaminhamento inteiro é recusado e
auditado — encaminhar pela metade entregaria ao destinatário algo diferente do que o
remetente mandou.

Condição de corpo dispara o download do corpo antes da avaliação, já que a sincronização
está conectada naquele momento; se o download falhar, a avaliação recai sobre a prévia.

**O que esta fase deliberadamente não faz: um classificador de spam próprio.** Servidores
corporativos — Exchange, Google Workspace — já classificam com dados que nenhum cliente
desktop tem: volume global, reputação de IP, telemetria de milhões de caixas. Um
classificador local competindo com isso perde, e o modo de perder é o pior possível: falso
positivo esconde mensagem legítima numa pasta que o usuário não olha. O papel do cliente é
respeitar o veredito do servidor, tornar a correção fácil e avisar sobre disfarce.

## Fase 8 — Assistência por IA ✅

Recursos de IA sobre a caixa postal: resumo de mensagem longa e de conversa, sugestão de
resposta, redação assistida no compositor, classificação sugerida para as regras automáticas
e pesquisa em linguagem natural sobre o índice da fase 6.

**A parte difícil não são os recursos, é a política de privacidade — e ela vem primeiro.**
Todo o desenho deste produto é sobre conteúdo de mensagem não sair da máquina em claro:
banco cifrado com SQLCipher, segredos fora do banco, auditoria sem conteúdo. Mandar corpo de
mensagem para um modelo em nuvem inverte isso por completo, e num cliente organizado por
domínio — que existe justamente porque a confidencialidade varia de cliente para cliente —
não é detalhe menor.

Por isso a fase começa pela infraestrutura, não pelos recursos:

1. **Abstração `IAssistantProvider`** na camada de Aplicação, com implementações separadas.
2. **Modelo local por padrão** (ONNX Runtime ou equivalente), em que nada trafega. Custa
   tamanho de download e qualidade menor; ganha o direito de estar ligado sem perguntar.
3. **Provedor em nuvem opcional, com consentimento por Diretório de Domínio.** É o encaixe
   natural: o Diretório de Domínio já é a unidade de política do produto. Um diretório pode
   permitir processamento em nuvem e outro não, e a decisão fica onde o usuário já pensa
   sobre confidencialidade. Nunca ligado por padrão.
4. **Registro em auditoria de cada envio a provedor externo** — identificadores e destino,
   nunca conteúdo, como manda a regra que já vale para o resto.

Só depois disso os recursos. Ordená-los antes produziria um atalho difícil de desfazer: uma
chamada em nuvem enfiada no meio do compositor, sem consentimento, que ninguém lembra de
remover.

**Entregue exatamente nessa ordem.** `AssistantGateway` é a porta única: nenhum recurso
fala com provedor diretamente, pelo mesmo motivo que toda movimentação passa pelo
`MoveMessageHandler`. A escolha é local primeiro, sempre — autorizar a nuvem diz que
*pode*, não que *deve*, e preferi-la por ser melhor transformaria o consentimento em
formalidade. O consentimento mora em `DomainDirectory.AllowsCloudAssistant`, nasce falso
(migração `CloudAssistantConsent` com `defaultValue: false`, o que vale também para os
diretórios já existentes) e é revogável; conta sem diretório resolvível não é autorizada.
Cada envio externo entra na auditoria **antes** de sair — registrar depois perderia a
chamada que falhou no meio do caminho — com provedor, tarefa e tamanho, nunca o conteúdo.

Os provedores falam HTTP no formato OpenAI, o mesmo que Ollama, LM Studio, llama.cpp e os
serviços em nuvem expõem: compatibilidade com o que o usuário já tenha instalado, sem
embutir um runtime nativo de centenas de megabytes no instalador. A chave do provedor de
nuvem sai do cofre do sistema a cada chamada, como as senhas de conta.

Recursos entregues: resumo de mensagem, sugestão de resposta e reescrita no compositor. O
corpo é cortado em 12 mil caracteres antes de sair e vai como texto puro — marcação não
ajuda o modelo e infla o que deixa a máquina; endereços de participantes ficam de fora.

## Fase 9 — Acabamento ✅

Envio agendado, confirmação de leitura, agrupamento por conversa, atalhos completos no
padrão Outlook, limpeza segura de cache e confirmações antes de operações destrutivas.

O **envio agendado** não ganhou mecanismo próprio de espera: a fila já respeita
`NextAttemptAt`, então agendar é enfileirar com a data certa. Um segundo relógio teria de
ser mantido em sincronia com o primeiro, e não estaria.

A **confirmação de leitura** nunca sai sozinha. O cabeçalho `Disposition-Notification-To`
é um pedido, não uma ordem: enviar sem perguntar entregaria ao remetente a informação de
que a mensagem foi aberta — que é exatamente o que um remetente hostil quer confirmar.
Recusar também é decisão registrada (`Message.ReadReceiptHandled`), para que a pergunta
não reapareça a cada abertura.

O **agrupamento por conversa** colapsa a lista mantendo a mensagem mais recente de cada
conversa. Mensagem sem `ThreadId` é a sua própria conversa: juntá-las todas sob uma linha
esconderia mensagens que nada têm a ver umas com as outras.

Os **atalhos** seguem o Outlook, inclusive onde ele contraria o hábito geral — Ctrl+F é
encaminhar, não localizar. Ctrl+Shift+A lista todos, porque descobri-los precisa ser
possível sem consultar documentação.

A **limpeza de cache** é segura por construção: só descarta o que o servidor ainda tem
(mensagem com UID e sincronizada), preserva os metadados do anexo e a autorização de
conteúdo remoto, e mede o impacto antes de apagar — o mesmo desenho de duas etapas da
remoção de conta, de diretório e de pasta.

## Fase 10 — Distribuição ✅

Assinatura do pacote MSIX com certificado corporativo, atualização por App Installer,
instalador para o modo unpackaged e documentação de implantação, incluindo o registro dos
aplicativos OAuth no Entra ID e no Google Cloud Console.

O pipeline de release (`.github/workflows/release.yml`) dispara por tag `v*.*.*` e é
separado do CI de propósito: o CI roda a cada push e não pode depender de segredos de
assinatura. O certificado chega como PFX em base64 pelos segredos do repositório, vive no
runner só durante o job e é apagado ao final. Sem ele o pipeline **não falha** — gera o
pacote sem assinatura e avisa, o que permite validar o empacotamento antes de haver
certificado.

A atualização automática vem do `.appinstaller` gerado a partir de
`build/Sintek.Mail.appinstaller.template`: o Windows consulta a URI a cada 8 horas de uso
(não a cada abertura — em frota grande isso transforma o servidor de distribuição em
gargalo) e instala em segundo plano, sem bloquear o uso.

O modo sem pacote existe para ambientes em que a política de grupo bloqueia sideload.
`build/install-unpackaged.ps1` instala sob `%LOCALAPPDATA%` — sem privilégio de
administrador —, cria o atalho, registra a desinstalação no Painel de Controle e preserva
`appsettings.Local.json` entre atualizações. A desinstalação **não apaga** banco nem
credenciais: dados de usuário não somem por remoção de programa.

`docs/implantacao.md` cobre o resto: registro dos aplicativos OAuth nos dois provedores,
onde os Client IDs entram (arquivo ou variável de ambiente, para frota grande), a
configuração do assistente de IA e o que fica na máquina do usuário — incluindo o aviso de
que perder o perfil do Windows significa perder a chave do banco, e que isso é deliberado.

## Fase 11 — Contatos e histórico de destinatários ✅

**Entregue.** `RecipientHistory`, `Contact`/`ContactEmail`, `RecipientSuggestionRanker`,
`RecipientHistoryHandler`, `ManageContactsHandler`, `VCardSerializer`, `ContactsViewModel`,
o `AutoSuggestBox` de Para/CC/CCO e o diálogo de contatos. O texto abaixo descreve o
desenho como ele foi implementado.

**Estado antes desta fase: não existia.** O que se parece com isso é `KnownCorrespondent`, e ele não
serve: carrega apenas nome exibido e domínio — não o endereço —, e é alimentado somente por
mensagens **lidas e não marcadas como spam**, porque existe para detectar remetente
disfarçado. Usá-lo para autocompletar sugeriria endereços incompletos e deixaria de fora
justamente quem o usuário ainda não leu.

O Outlook tem duas coisas distintas, e o pedido ("históricos para facilitar preencher") é a
primeira:

1. **Cache de autocompletar** — endereços para quem você já escreveu, ordenados por
   frequência e recência, sugeridos ao digitar e removíveis um a um.
2. **Contatos** — o catálogo de endereços propriamente dito, com nome, empresa, telefone.

### 11.1 Cache de autocompletar ✅

Entidade `RecipientHistory` (endereço, nome exibido, conta, contador de uso, último uso),
alimentada **no envio** — é a intenção do usuário que conta, não a entrega. Sugestão no
`AutoSuggestBox` de Para/CC/CCO, ordenada por uso e recência, com teto de oito itens.

**Remoção individual é requisito, não refinamento.** Um endereço digitado errado que entrou
no cache é o incômodo clássico do Outlook, e o usuário espera o "x" para apagá-lo.

**Onde este produto diverge do Outlook, de propósito:** a sugestão cujo domínio não é aceito
pelo Diretório de Domínio da conta aparece **marcada**, não escondida. Esconder quebraria o
e-mail externo legítimo; não marcar deixaria enviar para um domínio sósia sem perceber — o
mesmo vetor que o `SenderTrustEvaluator` já cobre na leitura.

### 11.2 Catálogo de contatos ✅

Entidade `Contact` com os campos que o Outlook expõe, importação e exportação em **vCard
(RFC 6350)** — que é o formato que Outlook e Google leem e escrevem, e portanto o que torna
a migração possível nos dois sentidos.

Contato pertence a uma conta e, por consequência, a um Diretório de Domínio: a lista fica
naturalmente segmentada por cliente, que é o que o produto inteiro faz.

### O que a implementação acrescentou ao plano

**O leitor de vCard nunca lança por conteúdo malformado.** Arquivo exportado de outro
cliente traz propriedades desconhecidas, versões antigas e endereços inválidos; abortar a
importação inteira por causa de um cartão ruim faria perder os outros duzentos. O que não dá
para entender é ignorado e contado. Pelo mesmo motivo o leitor aceita as duas sintaxes de
preferencial — `TYPE=PREF` do 3.0 e `PREF=1` do 4.0 —, e desdobra linha continuada antes de
interpretar, sem o que um endereço longo chegaria partido ao meio.

**A importação soma endereços; a edição substitui.** O arquivo é uma contribuição parcial e
não pode apagar o que o usuário acrescentou à mão; a tela de edição mostra a lista completa,
então o que sumiu de lá ele apagou de propósito.

**Gravar o histórico nunca derruba o envio.** A falha é registrada e a mensagem segue: o
autocompletar é conveniência, a mensagem é o trabalho.

**"Adicionar aos contatos" no painel de leitura**, que o plano não previa e o Outlook tem —
é o caminho pelo qual o catálogo se enche na prática. Remetente já cadastrado não é
duplicado nem sobrescrito: o contato pode ter sido editado à mão, e trocar o nome curado
pelo que veio no cabeçalho de uma mensagem seria perder a edição sem avisar.

**Defeito corrigido de passagem, herdado das fases anteriores:** o provedor do SQLite não
ordena nem compara `DateTimeOffset`, e a quebra só aparece em tempo de execução. Quatro
consultas caíam nisso desde a fase 1 — a listagem de mensagens da pasta (a tela principal),
o registro de auditoria, a limpeza de cache e a fila de saída, que nunca drenaria. Nenhuma
tinha teste contra o banco real; todas têm agora. Ver `SqliteFunctions`, D-022 e a armadilha
registrada no `CLAUDE.md`.

---

## Fase 12 — Agenda ✅

**Entregue.** `CalendarEvent`/`EventAttendee` e `EventMoveEvaluator` no domínio;
`ICalendarSerializer` como porta e `IcalNetCalendarSerializer` como adaptador;
`ImportInvitationHandler`, `RespondToInvitationHandler`, `MoveEventHandler` e
`ManageEventsHandler`; migração `CalendarEvents`; `CalendarViewModel` e o `CalendarDialog`.
O texto abaixo descreve o desenho como ele foi implementado.

**A decisão que define o tamanho desta fase:** Teams, Google Meet e Outlook **não precisam
de três integrações**. Os três enviam convite no mesmo formato — uma parte MIME
`text/calendar` com `METHOD=REQUEST`, conforme o **iCalendar (RFC 5545)**. Implementar o
padrão corretamente e extrair a URL de entrada do corpo cobre os três, e cobre também
Zoom, Webex e qualquer outro que respeite a norma. Escrever um conector por produto seria
triplicar o trabalho para obter menos.

### 12.1 Importar convites da caixa

A sincronização já detecta partes MIME; passa a reconhecer `text/calendar` e entregar a um
`ImportInvitationHandler`:

- `METHOD=REQUEST` cria ou atualiza o evento, casando por `UID`
- `METHOD=CANCEL` cancela
- `METHOD=REPLY` atualiza o `PARTSTAT` do participante que respondeu

**Regra inviolável desta fase: `SEQUENCE` menor nunca sobrescreve maior.** Convite antigo
que chega atrasado — reencaminhado, ou retido por um servidor lento — desfaria a atualização
mais recente e mudaria a reunião de volta para o horário errado. É o mesmo raciocínio de
`Message.MarkPending`, que também recusa rebaixar um estado mais forte.

### 12.2 Responder

Aceitar, Recusar e Provisório geram `METHOD=REPLY` de volta ao organizador — **pela fila de
saída**, como todo envio (D-014). Responder direto pelo SMTP criaria um segundo caminho de
envio sem ordem, retentativa nem visibilidade.

### 12.3 Mover entre datas

Arrastar e soltar na grade. O comportamento depende de quem você é no evento, e a distinção
é deliberada:

- **Organizador**: mover incrementa o `SEQUENCE` e reenvia `METHOD=REQUEST` a todos. É o que
  mantém os participantes em dia.
- **Participante em reunião com outros**: mover apenas a própria cópia dessincroniza você do
  organizador em silêncio — você aparece livre no horário em que todos combinaram. O Outlook
  permite; aqui a operação é **recusada com explicação**, e a alternativa oferecida é propor
  novo horário.
- **Compromisso próprio, sem participantes**: move livremente.

### 12.4 Regra de Diretório de Domínio na agenda

O calendário pertence a uma conta, e a conta a um diretório. Evento cujos participantes não
satisfazem a regra do diretório é tratado pelo mesmo `DomainMembershipEvaluator` e pela
mesma `InvalidEmailAction` já configurada — bloquear, avisar, desviar ou registrar. Sem
isso a agenda seria um produto genérico grudado ao lado do cliente de e-mail, em vez de
parte dele.

### 12.5 Escopo e riscos

**Dentro:** eventos únicos e recorrentes (`RRULE`), participantes, lembretes locais, visões
de dia/semana/mês, e a agenda alimentada pelos convites que chegam por e-mail — que é
exatamente o pedido.

**Fora, para a fase 13:** sincronização bidirecional com servidor. É outra pilha de
protocolo inteira, e a agenda local alimentada por e-mail já entrega o caso de uso sem ela.

**Risco de fuso horário: medido, e resolvido por desenho.** A suspeita era que
`InvariantGlobalization` inviabilizasse o tratamento de fuso, já que o iCalendar identifica
fuso por nome IANA (`America/Sao_Paulo`). A medição (05/08/2026, .NET 10 em modo
invariante) separou o que funciona do que não funciona:

| API | Resultado |
|---|---|
| `TimeZoneInfo.GetSystemTimeZones()` | ✅ 419 fusos — a base de fusos está inteira |
| `TimeZoneInfo.Local` e `GetAdjustmentRules()` | ✅ funcionam |
| Cálculo de offset e conversão de instante | ✅ funcionam |
| `TryConvertIanaIdToWindowsId` | ❌ devolve falso |
| `TryConvertWindowsIdToIanaId` | ❌ devolve falso |

Ou seja: **os dados de fuso estão presentes; o que sumiu é apenas a tabela de tradução
entre nomes IANA e nomes Windows**, que vem do ICU. `FindSystemTimeZoneById` com nome IANA
funciona no Linux (que usa IANA nativamente) e deve falhar no Windows, que precisa
justamente da tradução ausente.

A saída não é desligar `InvariantGlobalization` — seria reabrir as duas armadilhas já
documentadas no `CLAUDE.md` por um problema que dá para contornar. **É não depender de nome
de fuso em momento algum:**

- **Ao ler um convite**, usar o `VTIMEZONE` que o próprio arquivo `.ics` carrega. A norma
  manda o convite embutir as regras de offset justamente para não depender da base do
  destinatário, e o `Ical.Net` sabe usá-las.
- **Ao escrever um convite**, gerar o `VTIMEZONE` a partir de `TimeZoneInfo.Local` e suas
  `AdjustmentRules` — ambos disponíveis, como a medição mostra.
- **No seletor de fuso da interface**, listar `GetSystemTimeZones()`.

Consequência a registrar: o identificador de fuso que o sistema devolve é o do sistema
operacional (nome Windows no Windows, IANA no Linux). Como só se distribui para Windows e
o que viaja no convite é o `VTIMEZONE`, e não o identificador, isso não vaza para o
formato.

**Biblioteca:** `Ical.Net` (MIT) para ler e escrever RFC 5545. Escrever um analisador de
iCalendar à mão é a armadilha clássica desse recurso: o formato tem dobra de linha,
escapes, fusos embutidos e recorrência com exceções.

### O que a implementação mudou em relação ao plano

**O risco de fuso desapareceu, e por um motivo melhor do que o previsto.** O plano era
depender do `VTIMEZONE` embutido no `.ics` para não precisar do mapeamento IANA que o
`InvariantGlobalization` remove. Na prática o `Ical.Net` traz o `NodaTime` junto, e o
`NodaTime` carrega a própria base IANA — então `America/Sao_Paulo` resolve sozinho, sem ICU.
O `VTIMEZONE` embutido continua sendo usado quando o nome é do Windows
(`E. South America Standard Time`), que é o que o Outlook emite. Os dois caminhos estão
cobertos por teste, e ambos devolvem 17h UTC para 14h em São Paulo.

**Convite sem `UID` ganha um inventado pela biblioteca — e diferente a cada leitura.**
Comportamento medido, não desejado: um `UID` assim não serve de identidade, e rebaixar o
corpo da mensagem criaria um compromisso novo a cada vez. A importação ganhou uma segunda
via, pela mensagem em que o convite chegou.

**A resposta viaja como parte `text/calendar` em `multipart/alternative`, não como anexo.**
É essa forma que faz o cliente do organizador atualizar o `PARTSTAT` sozinho; como anexo,
ele mostraria um `.ics` para a pessoa abrir à mão, e a metade que não abre nunca responde.

**O convite entra na agenda ao abrir a mensagem**, que é quando o corpo desce — a
sincronização traz só cabeçalhos. Falha na importação não derruba o download: perder a
mensagem por causa de um `.ics` malformado seria trocar um problema pequeno por um grande.

**Excluir uma reunião que você organiza envia o cancelamento antes de apagar o registro.**
Sumir da agenda dos outros sem avisar é o mesmo defeito que a fase evita ao recusar a
movimentação da cópia própria.

---

## Fase 13 — Sincronização de agenda com servidor

**Entregue.** A agenda deixou de depender só do que chega por e-mail: ela espelha as
coleções do servidor e devolve o que muda aqui.

**Três protocolos, não um, e a divisão não foi escolha de gosto.** O Exchange Online nunca
implementou CalDAV, e o EWS está sendo desligado (bloqueio global em 01/10/2026, remoção até
04/2027): para Microsoft 365 o único caminho suportado é o Microsoft Graph. A Google mantém
CalDAV como compatibilidade declaradamente parcial. CalDAV é o padrão aberto que cobre todo
o resto — Nextcloud, ownCloud, Baikal, Fastmail, iCloud, SOGo, Radicale, DAViCal. Daí uma
porta (`ICalendarSyncProvider`) com três implementações previstas; esta fase entrega a de
CalDAV, e Graph e Google ficam desenhados atrás dela (D-026).

**Dentro:**

- Descoberta por `/.well-known/caldav`, `current-user-principal` (RFC 5397) e
  `calendar-home-set` (RFC 4791), com listagem `Depth: 1` das coleções — nome, cor, `CTag`,
  `sync-token`, componentes aceitos e privilégios num pedido só.
- Sincronização incremental por `sync-collection` (RFC 6578), com paginação pelo 507 dentro
  do 207 e recuperação quando o servidor recusa o token (`DAV:valid-sync-token`).
- Caminho alternativo por `CTag` para servidor que não implementa `sync-collection`:
  listagem só de `ETag`, conteúdo depois em `calendar-multiget`.
- Escrita condicionada — `If-None-Match: *` para criar, `If-Match` para alterar e excluir —
  com releitura obrigatória quando o servidor não devolve ETag forte.
- Conflito visível na agenda, com escolha entre a versão local e a do servidor (D-027).
- Servidor de agenda no assistente de contas, testado junto com IMAP e SMTP.

**Fora, para a fase seguinte:** as implementações de Microsoft Graph e Google Calendar. O
Graph exige decidir a precedência **sem `SEQUENCE`** — ele não o expõe —, e isso é decisão
nova, não adaptação da regra de D-024.

### As armadilhas do protocolo que custaram desenho

Todas mediadas contra a norma e contra o comportamento documentado de servidores reais,
antes de haver um servidor para testar:

- **`AllowAutoRedirect = false` não é preferência.** O `HttpClient` transforma um PROPFIND
  em GET ao seguir um 301, e descarta o `Authorization` quando o destino é outro host — que
  é exatamente o caso do iCloud, cujo `calendar-home-set` aponta para a partição da conta em
  outro nome de servidor.
- **O ETag nunca é lido pela propriedade tipada.** Servidores fora da norma devolvem o valor
  sem aspas, e `HttpResponseHeaders.ETag` lança `FormatException` ao analisá-lo. E ETag
  fraco não serve para `If-Match`, que compara forte: ele é descartado para forçar a
  releitura.
- **O discriminador entre "alterado" e "removido" é onde o `status` está**, não o código:
  filho direto da `<response>` é o recurso; dentro de um `<propstat>` é uma propriedade que
  não existe. Confundir os dois é o erro que mais quebra cliente de CalDAV.
- **`DAV:` é literal**, com dois-pontos e sem `http://`, e os prefixos (`D:`, `d:`, `dav:`)
  são arbitrários. Casar por prefixo devolve zero elementos sem erro nenhum.
- **Ausência só significa exclusão em passada completa** — e quem declara isso é o provedor,
  não o motor. Um servidor sem `sync-collection` respondendo "o `CTag` não mudou" também
  devolve zero alterações, e tratá-lo como passada completa apagaria a coleção inteira
  (D-028).

Dois defeitos foram achados pelos testes antes de qualquer servidor real: `StringContent`
recusa media type com parâmetro — `text/calendar; charset=utf-8` lançava `FormatException`
em toda escrita —, e o `StringWriter` comum declara `encoding="utf-16"` no XML enquanto os
bytes saem em UTF-8.

---

## Origem do escopo

As fases 1 a 7 e 9 a 10 vêm da especificação em `spec/`. Duas adições posteriores, pedidas
pelo usuário e registradas aqui para que a origem não se perca:

- **Spam e lixo eletrônico** — a especificação trata apenas da *pasta* Spam, listada entre as
  padrão com ícone de alerta. Ações de marcar, leitura do veredito do servidor e listas de
  bloqueio não estavam previstas; foram distribuídas pelas fases 4, 5 e 7, cada peça onde já
  existe a infraestrutura de que precisa.
- **Assistência por IA** — ausente da especificação. Virou a fase 8, depois da pesquisa (que
  ela consome) e antes do acabamento.
- **Contatos e agenda** — pedidos pelo usuário depois da fase 10. Viraram as fases 11 e 12, e
  a sincronização de agenda com servidor virou a 13.
- **Agenda e contatos** — ausentes da especificação, que menciona apenas "agendar envio"
  (recurso diferente, entregue na fase 9). Viraram as fases 11 e 12. A ordem inverte a em
  que foram pedidos de propósito: os contatos são pequenos, melhoram o compositor que já
  existe e fornecem à agenda o seletor de participantes que ela vai precisar.

## Dependências externas

Os Client IDs de OAuth são configuração de implantação, não código. Sem eles os provedores
ficam implementados e desativados, e a interface os apresenta como "não configurados" em
vez de falhar na autenticação. Ver `appsettings.json`.

O certificado de assinatura de código é necessário apenas na fase 10.
