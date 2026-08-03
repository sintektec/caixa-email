# Especificação Unificada — Cliente de E-mail Desktop para Windows 11

## 1. Papel e objetivo

Atue como Engenheiro de Software Sênior, Arquiteto de Soluções Microsoft e Especialista em aplicações desktop offline-first.

Desenvolva um cliente de e-mail desktop corporativo, instalável e nativo para Windows 11, inspirado nas principais funcionalidades do Microsoft Outlook e baseado no Fluent Design System.

O diferencial obrigatório do sistema é uma arquitetura de organização rigorosa por domínio, com estrutura hierárquica:

**Domínio → Conta de e-mail pertencente ao domínio → Pastas padrão e personalizadas**

A aplicação deve funcionar em modo offline-first: todas as ações do usuário devem ser registradas imediatamente no banco de dados local. Quando houver conexão com a internet, o sistema deverá sincronizar e-mails, alterações, mensagens pendentes, pastas e status com os servidores configurados.

## 2. Stack tecnológica obrigatória

Utilize obrigatoriamente as tecnologias abaixo:

- **Linguagem:** C# 12;
- **Framework:** .NET 8 ou superior;
- **Interface desktop:** WinUI 3 com Windows App SDK;
- **Padrão de arquitetura:** MVVM;
- **Persistência local:** SQLite;
- **ORM:** Entity Framework Core;
- **Criptografia do banco local:** SQLCipher;
- **Protocolos de e-mail:** MailKit para IMAP e SMTP;
- **Segurança de credenciais:** Windows Credential Manager ou mecanismo seguro equivalente do Windows;
- **Instalação:** pacote instalável compatível com Windows 11;
- **Arquitetura de código:** Clean Architecture ou arquitetura em camadas, com separação clara entre Domínio, Aplicação, Infraestrutura, Persistência e Interface.

Não utilizar Electron, aplicações web empacotadas ou soluções que não tenham comportamento nativo adequado no Windows 11.

## 3. Princípios fundamentais

### 3.1 Operação offline-first

A aplicação deve ser plenamente utilizável sem acesso à internet para dados previamente sincronizados.

Em modo offline, o usuário deve conseguir:

- Ler mensagens armazenadas localmente;
- Pesquisar e-mails localmente;
- Criar, editar e excluir rascunhos;
- Criar novas mensagens;
- Anexar arquivos;
- Organizar e-mails em pastas;
- Marcar mensagens como lidas, não lidas, importantes ou sinalizadas;
- Criar e editar categorias;
- Arquivar ou excluir mensagens;
- Mover mensagens entre pastas, respeitando as regras de domínio;
- Criar e administrar Diretórios de Domínio;
- Criar regras de organização;
- Consultar a fila de sincronização.

Todas as alterações devem ser gravadas no banco SQLite de forma imediata. Quando a conectividade retornar, a aplicação deve sincronizar as operações pendentes de maneira confiável e persistente.

### 3.2 Segurança e privacidade

Implementar os seguintes requisitos de segurança:

- Criptografar a base local de dados com SQLCipher;
- Nunca armazenar senhas em texto simples;
- Usar OAuth 2.0 quando suportado pelo provedor de e-mail;
- Armazenar credenciais, tokens e segredos no Windows Credential Manager;
- Bloquear o carregamento automático de imagens remotas em mensagens HTML por padrão;
- Renderizar mensagens HTML de forma segura;
- Impedir execução automática de scripts, conteúdo ativo ou arquivos potencialmente perigosos;
- Exibir alerta para extensões de anexos suspeitas;
- Manter logs técnicos sem registrar conteúdo sigiloso de mensagens;
- Possibilitar limpeza segura do cache local;
- Solicitar confirmação antes de remover contas, apagar mensagens permanentemente ou excluir dados locais.

## 4. Estrutura de navegação obrigatória

A barra lateral esquerda deve apresentar uma estrutura em árvore usando o componente TreeView do WinUI 3.

A estrutura obrigatória deve seguir este padrão:

```
Favoritos
Contas e Diretórios
  sintek.com.br
    contato@sintek.com.br
      Caixa de Entrada
      Itens Enviados
      Rascunhos
      Lixeira
      Spam
      Arquivados
      Pastas personalizadas
    financeiro@sintek.com.br
      Pastas da conta
  cliente.com.br
    suporte@cliente.com.br
      Pastas da conta
  Gmail pessoal
    conta@gmail.com
```

Cada nível deve ter ícones consistentes com o Fluent Design:

- Domínio: ícone de organização, rede ou globo;
- Conta: ícone de e-mail;
- Caixa de entrada: ícone de inbox;
- Enviados: ícone de envio;
- Rascunhos: ícone de documento;
- Lixeira: ícone de descarte;
- Spam: ícone de alerta;
- Pastas personalizadas: ícone de pasta.

Exibir contador de mensagens não lidas junto às contas e pastas relevantes.

## 5. Regra crítica: Diretórios de Domínio

### 5.1 Conceito

Um Diretório de Domínio é uma pasta raiz lógica que representa um domínio de e-mail, como:

- sintek.com.br
- cliente.com.br
- fornecedor.net

Cada Diretório de Domínio pode conter uma ou mais contas de e-mail, desde que essas contas pertençam exatamente ao domínio do diretório.

### 5.2 Validação obrigatória de contas

Ao adicionar uma nova conta de e-mail dentro de um Diretório de Domínio, validar obrigatoriamente que o domínio da conta seja idêntico ao domínio configurado no diretório pai.

**Exemplos:**

| Diretório de Domínio | Conta | Resultado |
|---|---|---|
| sintek.com.br | contato@sintek.com.br | Permitido |
| sintek.com.br | financeiro@sintek.com.br | Permitido |
| sintek.com.br | admin@gmail.com | Bloqueado |
| sintek.com.br | suporte@cliente.com.br | Bloqueado |
| cliente.com.br | suporte@cliente.com.br | Permitido |

A validação deve:

- Extrair o texto após o caractere @;
- Converter o domínio para letras minúsculas;
- Remover espaços indevidos;
- Comparar o domínio de forma exata com o DomainName do Diretório de Domínio;
- Lançar uma exceção de domínio ou retornar erro de validação caso os valores sejam diferentes.

Por padrão, subdomínios não devem ser aceitos automaticamente.

**Exemplo:**

| Diretório de Domínio | Conta | Resultado padrão |
|---|---|---|
| empresa.com | usuario@empresa.com | Permitido |
| empresa.com | usuario@vendas.empresa.com | Bloqueado |
| empresa.com | usuario@gmail.com | Bloqueado |

Criar uma configuração explícita no Diretório de Domínio para permitir subdomínios, quando necessário.

### 5.3 Regras de organização de mensagens por domínio

Além de validar as contas vinculadas, o sistema deve permitir regras de organização de mensagens por domínio.

Em uma pasta marcada como restrita por domínio, somente poderão ser movidas ou classificadas mensagens que correspondam às regras configuradas para aquele Diretório de Domínio.

Uma mensagem pode ser considerada pertencente a um domínio quando:

- O remetente possui o domínio;
- Um dos destinatários possui o domínio;
- Um destinatário em cópia possui o domínio;
- A mensagem atende a uma regra explícita criada pelo usuário;
- O domínio está registrado como domínio adicional permitido.

Criar modos de validação configuráveis:

- **SenderOnly:** validar somente o remetente;
- **RecipientOnly:** validar somente destinatários;
- **SenderOrRecipient:** aceitar remetente ou destinatário;
- **SenderAndRecipient:** exigir correspondência de remetente e destinatário;
- **AnyParticipant:** aceitar remetente, destinatários ou cópias.

Ao tentar mover uma mensagem incompatível para uma pasta restrita, impedir a ação e exibir uma mensagem clara:

> "Este e-mail não pertence ao domínio configurado para esta pasta e não pode ser adicionado a este local."

Também disponibilizar as ações configuráveis:

- Bloquear a movimentação;
- Exibir alerta e pedir confirmação;
- Mover automaticamente para uma pasta de pendências;
- Registrar a ocorrência no log de auditoria.

### 5.4 Herança de regras

As subpastas de uma pasta restrita por domínio devem herdar automaticamente as regras do Diretório de Domínio pai.

Não permitir que uma pasta seja vinculada simultaneamente a mais de um Diretório de Domínio.

Ao alterar o domínio de um Diretório de Domínio existente:

- Revalidar todas as contas vinculadas;
- Revalidar todas as mensagens presentes em pastas restritas;
- Listar contas e mensagens incompatíveis;
- Permitir mover mensagens incompatíveis para uma pasta de pendências;
- Exigir confirmação antes de concluir a alteração;
- Registrar a ação no log de auditoria.

## 6. Funcionalidades do cliente de e-mail

Implementar as principais funções esperadas de um cliente de e-mail corporativo semelhante ao Outlook.

### 6.1 Gestão de contas

- Adicionar contas IMAP e SMTP;
- Configuração automática e manual de servidores;
- Editar, desativar e remover contas;
- Validar credenciais antes de concluir o cadastro;
- Suportar conexões SSL/TLS;
- Permitir múltiplas contas no mesmo Diretório de Domínio;
- Exibir o estado de sincronização de cada conta;
- Exibir erros de autenticação ou conexão de forma clara;
- Aplicar a validação estrita de domínio antes de vincular uma conta ao seu Diretório de Domínio.

### 6.2 Gestão de mensagens

- Receber e sincronizar mensagens;
- Ler mensagens em painel de visualização;
- Criar novas mensagens;
- Responder;
- Responder a todos;
- Encaminhar;
- Encaminhar como anexo;
- Salvar rascunhos automaticamente;
- Enviar mensagens imediatamente ou colocá-las em fila offline;
- Agendar envio;
- Excluir mensagens;
- Restaurar mensagens da lixeira;
- Arquivar;
- Marcar como lida ou não lida;
- Marcar como importante;
- Adicionar sinalizadores;
- Criar e aplicar categorias coloridas;
- Agrupar e-mails por conversa ou thread;
- Gerenciar anexos;
- Criar assinaturas por conta;
- Criar modelos de mensagem;
- Suportar campos Para, CC e CCO;
- Avisar sobre possível anexo esquecido;
- Solicitar confirmação de leitura quando suportado pelo provedor.

### 6.3 Pastas e organização

- Criar pastas personalizadas;
- Criar subpastas;
- Renomear pastas;
- Excluir pastas vazias ou com confirmação;
- Favoritar pastas;
- Arrastar e soltar e-mails entre pastas;
- Aplicar as regras de domínio em todas as operações de arrastar e soltar;
- Exibir pastas padrão:
  - Caixa de Entrada;
  - Itens Enviados;
  - Rascunhos;
  - Lixeira;
  - Spam;
  - Arquivados;
- Criar pastas de pendências para mensagens incompatíveis com regras de domínio.

### 6.4 Pesquisa e filtros

Implementar pesquisa local rápida, disponível mesmo offline, com suporte a:

- Remetente;
- Destinatário;
- CC;
- Assunto;
- Corpo da mensagem;
- Nome do anexo;
- Data;
- Conta;
- Pasta;
- Domínio;
- Categoria;
- Mensagem lida ou não lida;
- Sinalizador;
- Importância;
- Status de sincronização.

Incluir pesquisa avançada com múltiplos filtros e possibilidade de salvar pesquisas frequentes.

### 6.5 Regras automáticas

Permitir criação de regras automáticas, tais como:

- Quando o remetente pertencer a determinado domínio, mover a mensagem para uma pasta;
- Quando o destinatário pertencer a determinado domínio, categorizar a mensagem;
- Quando o assunto contiver determinada expressão, aplicar uma categoria;
- Quando houver anexo, marcar a mensagem como importante;
- Quando uma mensagem não for compatível com uma regra de domínio, movê-la para pendências;
- Quando houver correspondência com mais de um Diretório de Domínio, aplicar uma prioridade configurável ou solicitar decisão do usuário.

## 7. Interface e experiência do usuário

Criar uma interface moderna, fluida, acessível e visualmente alinhada ao Windows 11.

### 7.1 Layout principal

A tela principal deve conter:

**Barra lateral esquerda**

- Botão para criar nova mensagem;
- Favoritos;
- Diretórios de Domínio;
- Contas organizadas sob seus respectivos domínios;
- Pastas de cada conta;
- Contadores de mensagens não lidas;
- Ações para criar Diretórios de Domínio, contas e pastas.

**Painel central**

- Lista de mensagens;
- Remetente;
- Assunto;
- Prévia da mensagem;
- Data e hora;
- Indicadores de anexo, prioridade, leitura, categoria e sinalizador;
- Indicador visual de status de sincronização;
- Indicador visual de domínio relacionado quando aplicável.

**Painel de leitura**

- Conteúdo da mensagem;
- Informações de remetente e destinatários;
- Lista de anexos;
- Ações de responder, responder a todos, encaminhar, arquivar, excluir, mover e categorizar.

**Barra superior**

- Campo de busca;
- Botão de sincronização manual;
- Indicador de modo online, offline, sincronizando ou com erro;
- Acesso às configurações;
- Alternância entre tema claro e escuro.

### 7.2 Usabilidade e acessibilidade

- Suporte a tema claro e escuro;
- Navegação por teclado;
- Atalhos similares aos clientes de e-mail profissionais;
- Compatibilidade com recursos de acessibilidade do Windows;
- Mensagens de erro compreensíveis;
- Indicadores claros para operações pendentes;
- Confirmações antes de operações destrutivas;
- Feedback visual durante a sincronização;
- Interface responsiva para monitores com diferentes resoluções.

## 8. Modelo de dados

Utilizar Entity Framework Core com SQLite e criar migrações para a estrutura local.

### 8.1 Entidades obrigatórias

**Domains** — Representa o Diretório de Domínio raiz.

- Id — Guid, chave primária;
- DomainName — string, nome do domínio;
- Description — string opcional;
- ValidationMode — enum;
- InvalidEmailAction — enum;
- AllowSubdomains — bool;
- IsActive — bool;
- CreatedAt — DateTime;
- UpdatedAt — DateTime.

**Accounts** — Representa uma conta de e-mail vinculada a um domínio.

- Id — Guid, chave primária;
- DomainId — Guid, chave estrangeira para Domains;
- EmailAddress — string;
- DisplayName — string;
- ImapHost — string;
- ImapPort — int;
- SmtpHost — string;
- SmtpPort — int;
- UseSsl — bool;
- AuthenticationType — enum;
- IsActive — bool;
- LastSyncAt — Date

> **Nota:** o documento original termina truncado aqui (em `LastSyncAt — Date`). O modelo de dados completo a partir de `Folders`/`Messages` foi projetado no documento `02-plano-sintek-mail.md`.
