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

## Fase 2 — Contas

Assistente de configuração com descoberta automática, fluxo interativo de OAuth para
Microsoft e Google, teste de credenciais antes de concluir o cadastro, edição e remoção
de contas com confirmação, e a interface de criação de Diretórios de Domínio com a
validação estrita já implementada no domínio.

Descoberta automática pendente: consulta a registros SRV do DNS (RFC 6186) e ao banco
ISPDB da Mozilla. A estrutura de `IAutodiscoverService` já as acomoda sem mudança de
contrato.

## Fase 3 — Sincronização

Descoberta e espelhamento de pastas, sincronização incremental por UID com `CONDSTORE` e
`QRESYNC` quando o servidor suportar, `IDLE` para recebimento imediato com retorno a
sondagem periódica, e ressincronização completa quando o `UIDVALIDITY` mudar.

Ampliar `OutboxProcessor` para os tipos de operação ainda não tratados (envio, rascunhos,
operações de pasta) e expor a fila na interface, como a especificação exige.

## Fase 4 — Leitura e composição

Painel de leitura completo com anexos, download sob demanda de corpo e anexos, compositor
com Para/CC/CCO, rascunho automático, resposta, resposta a todos, encaminhamento e
encaminhamento como anexo, assinaturas por conta e aviso de anexo esquecido.

## Fase 5 — Pastas e regras de domínio na interface

Criação, renomeação e exclusão de pastas, favoritos, arrastar e soltar ligado ao
`MoveMessageHandler`, interface de vínculo de pasta a Diretório de Domínio com a
propagação de herança, pastas de pendências e o fluxo de troca de domínio com relatório de
impacto e confirmação.

## Fase 6 — Pesquisa

Pesquisa local sobre o índice FTS5 com todos os filtros da seção 6.4, pesquisa avançada
com combinação de critérios e pesquisas salvas na barra lateral.

Requer estender os gatilhos do FTS5 para indexar corpo, participantes e nomes de anexo à
medida que forem baixados.

## Fase 7 — Automação

Editor de regras automáticas, motor de avaliação na chegada de mensagens, categorias
coloridas com atalhos, modelos de mensagem e resolução de prioridade quando mais de um
Diretório de Domínio corresponder.

## Fase 8 — Acabamento

Envio agendado, confirmação de leitura, agrupamento por conversa, atalhos completos no
padrão Outlook, revisão de acessibilidade com leitor de tela e navegação apenas por
teclado, limpeza segura de cache e confirmações antes de operações destrutivas.

## Fase 9 — Distribuição

Assinatura do pacote MSIX com certificado corporativo, atualização por App Installer,
instalador para o modo unpackaged e documentação de implantação, incluindo o registro dos
aplicativos OAuth no Entra ID e no Google Cloud Console.

## Dependências externas

Os Client IDs de OAuth são configuração de implantação, não código. Sem eles os provedores
ficam implementados e desativados, e a interface os apresenta como "não configurados" em
vez de falhar na autenticação. Ver `appsettings.json`.

O certificado de assinatura de código é necessário apenas na fase 9.
