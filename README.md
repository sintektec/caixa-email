# Sintek.Mail

Cliente de e-mail corporativo nativo para Windows 11, com organização rigorosa por
**Diretório de Domínio** e operação offline-first sobre base local criptografada.

## O diferencial

A hierarquia de navegação é `Domínio → Conta → Pastas`, e o domínio não é apenas um rótulo:

- Uma conta só pode ser vinculada a um Diretório de Domínio se o domínio do endereço for
  **exatamente** o do diretório. `contato@sintek.com.br` entra em `sintek.com.br`;
  `admin@gmail.com` não. Subdomínios são bloqueados por padrão.
- Uma pasta pode ser marcada como restrita a um domínio. Mensagens que não satisfaçam a
  regra não entram nela — nem por arrastar e soltar, nem por regra automática, nem durante
  a sincronização.
- Subpastas herdam a restrição da pasta acima, e nenhuma pasta responde a mais de um
  Diretório de Domínio.

Cinco modos de validação definem quem conta como participante (`SenderOnly`,
`RecipientOnly`, `SenderOrRecipient`, `SenderAndRecipient`, `AnyParticipant`) e quatro
ações definem o que fazer com o que não passa (bloquear, alertar e confirmar, desviar para
pendências, apenas registrar).

## Offline-first

Toda ação grava no banco local e enfileira a sincronização **na mesma transação**. A
interface lê sempre do banco, nunca da rede — por isso ler, pesquisar, redigir, organizar
e categorizar funcionam sem conexão. Quando a internet volta, a fila é drenada em ordem.

## Segurança

Base local criptografada com SQLCipher, cuja chave — como toda senha e token — vive no
Gerenciador de Credenciais do Windows e nunca no banco. HTML de mensagem passa por três
camadas de defesa antes de ser exibido, com imagens remotas bloqueadas por padrão porque
carregá-las confirma ao remetente que a mensagem foi aberta.

## Stack

.NET 10 LTS · WinUI 3 / Windows App SDK 2.3 · MVVM · Clean Architecture ·
EF Core 10 + SQLite + SQLCipher · MailKit · MSIX e unpackaged

## Estrutura

```
src/
  Sintek.Mail.Domain/                 regras de negócio, sem dependência alguma
  Sintek.Mail.Application/            casos de uso e portas
  Sintek.Mail.Persistence/            EF Core, SQLCipher, migrações
  Sintek.Mail.Infrastructure/         MailKit, OAuth, sanitização, sincronização
  Sintek.Mail.Infrastructure.Windows/ Credential Manager
  Sintek.Mail.App/                    interface WinUI 3
tests/                                um projeto por camada
docs/                                 decisões, modelo de dados, roadmap
```

## Compilar e testar

O núcleo compila em qualquer sistema operacional; a interface exige Windows.

```bash
# Núcleo multiplataforma (Linux, macOS ou Windows)
dotnet test Sintek.Mail.CrossPlatform.slnf

# Solution completa, incluindo WinUI 3 (só Windows)
dotnet build Sintek.Mail.sln

# Pacote MSIX (só Windows)
dotnet build src/Sintek.Mail.App -p:Packaged=true
```

O CI executa os dois: `ubuntu-latest` para o núcleo — o que também verifica que ele não
adquiriu dependência do Windows — e `windows-latest` para a solution inteira e o MSIX.

## Configuração

Os Client IDs de OAuth são configuração de implantação. Copie `appsettings.json` para
`appsettings.Local.json` (ignorado pelo Git) e informe os seus. Sem eles, a autenticação
por senha continua funcionando e os provedores OAuth aparecem como não configurados.

## Documentação

- [Decisões arquiteturais](docs/decisoes-arquiteturais.md) — o porquê das escolhas não
  óbvias e o que quebra se forem revertidas
- [Modelo de dados](docs/modelo-de-dados.md) — entidades, índices e o que nunca é
  persistido
- [Roadmap](docs/roadmap.md) — fases de construção

## Skills SINTEK

O hook `SessionStart` em `.claude/settings.json` clona
[skills-globais](https://github.com/sintektec/skills-globais) em `.claude/skills/`. Se o
clone falhar — repositório privado sem credencial, proxy bloqueando o host — o script
avisa em stderr e a sessão segue sem as skills.
