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

## Fase 11 — Contatos e histórico de destinatários

**Estado hoje: não existe.** O que se parece com isso é `KnownCorrespondent`, e ele não
serve: carrega apenas nome exibido e domínio — não o endereço —, e é alimentado somente por
mensagens **lidas e não marcadas como spam**, porque existe para detectar remetente
disfarçado. Usá-lo para autocompletar sugeriria endereços incompletos e deixaria de fora
justamente quem o usuário ainda não leu.

O Outlook tem duas coisas distintas, e o pedido ("históricos para facilitar preencher") é a
primeira:

1. **Cache de autocompletar** — endereços para quem você já escreveu, ordenados por
   frequência e recência, sugeridos ao digitar e removíveis um a um.
2. **Contatos** — o catálogo de endereços propriamente dito, com nome, empresa, telefone.

### 11.1 Cache de autocompletar

Entidade `RecipientHistory` (endereço, nome exibido, conta, contador de uso, último uso),
alimentada **no envio** — é a intenção do usuário que conta, não a entrega. Sugestão no
`AutoSuggestBox` de Para/CC/CCO, ordenada por uso e recência, com teto de oito itens.

**Remoção individual é requisito, não refinamento.** Um endereço digitado errado que entrou
no cache é o incômodo clássico do Outlook, e o usuário espera o "x" para apagá-lo.

**Onde este produto diverge do Outlook, de propósito:** a sugestão cujo domínio não é aceito
pelo Diretório de Domínio da conta aparece **marcada**, não escondida. Esconder quebraria o
e-mail externo legítimo; não marcar deixaria enviar para um domínio sósia sem perceber — o
mesmo vetor que o `SenderTrustEvaluator` já cobre na leitura.

### 11.2 Catálogo de contatos

Entidade `Contact` com os campos que o Outlook expõe, importação e exportação em **vCard
(RFC 6350)** — que é o formato que Outlook e Google leem e escrevem, e portanto o que torna
a migração possível nos dois sentidos.

Contato pertence a uma conta e, por consequência, a um Diretório de Domínio: a lista fica
naturalmente segmentada por cliente, que é o que o produto inteiro faz.

---

## Fase 12 — Agenda

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

**Fora, para a fase 13:** sincronização bidirecional com servidor (CalDAV, EWS). É outra
pilha de protocolo inteira, e a agenda local alimentada por e-mail já entrega o caso de uso
sem ela.

**Risco a verificar antes de começar:** `InvariantGlobalization` está ligado, e o iCalendar
identifica fuso por nome IANA (`America/Sao_Paulo`). A conversão de nome IANA para fuso do
Windows normalmente depende do ICU, que esse modo remove. Se a verificação confirmar o
problema, as saídas são desligar `InvariantGlobalization` — o que reabre as duas armadilhas
já documentadas no `CLAUDE.md` — ou embarcar uma tabela de fusos. **Isso precisa ser medido
antes de a fase começar**, porque muda o desenho.

**Biblioteca:** `Ical.Net` (MIT) para ler e escrever RFC 5545. Escrever um analisador de
iCalendar à mão é a armadilha clássica desse recurso: o formato tem dobra de linha,
escapes, fusos embutidos e recorrência com exceções.

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
- **Agenda e contatos** — ausentes da especificação, que menciona apenas "agendar envio"
  (recurso diferente, entregue na fase 9). Viraram as fases 11 e 12. A ordem inverte a em
  que foram pedidos de propósito: os contatos são pequenos, melhoram o compositor que já
  existe e fornecem à agenda o seletor de participantes que ela vai precisar.

## Dependências externas

Os Client IDs de OAuth são configuração de implantação, não código. Sem eles os provedores
ficam implementados e desativados, e a interface os apresenta como "não configurados" em
vez de falhar na autenticação. Ver `appsettings.json`.

O certificado de assinatura de código é necessário apenas na fase 10.
