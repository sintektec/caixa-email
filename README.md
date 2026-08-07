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

A assistência por IA usa **modelo local por padrão** — nada trafega. O provedor em nuvem é
opcional, nasce desligado e depende de autorização explícita **por Diretório de Domínio**,
que é a unidade de política do produto: a confidencialidade varia de cliente para cliente.
Cada envio externo entra na auditoria antes de sair, com destino e tamanho, nunca com o
conteúdo.

O veredito de spam é o do **servidor**, lido dos cabeçalhos de autenticação (SPF, DKIM,
DMARC) e classificação. Não há classificador local competindo com quem tem telemetria de
milhões de caixas — o modo de perder essa disputa é esconder mensagem legítima numa pasta
que o usuário não olha.

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

### O que precisa estar instalado

| Para compilar | Requisito |
|---|---|
| O núcleo multiplataforma | **.NET 10 SDK** — <https://dotnet.microsoft.com/download/dotnet/10.0> |
| A interface WinUI 3 | .NET 10 SDK **e** o SDK do Windows 11 (10.0.19041 ou superior) |

O SDK do Windows vem com o **Visual Studio 2022 17.14+** na carga de trabalho
*Desenvolvimento para desktop com .NET*. Não é preciso instalar mais nada: os pacotes do
Windows App SDK trazem os alvos de MSBuild, e é por isso que o job do CI usa apenas o
`setup-dotnet`.

Confira o que já existe na máquina com `dotnet --info`. Se ele responder *"No .NET SDKs were
found"*, nenhum SDK está instalado — o `dotnet` que existe é só o runtime, ou nem isso.

> **O Smart App Control impede executar o que você acabou de compilar.** Ele vem ligado em
> Windows 11 recém-instalado e recusa binário sem assinatura de CA reconhecida e sem
> reputação na nuvem — que é a definição de todo build local. A mensagem é *"Uma política de
> Controle de Aplicativo bloqueou este arquivo"*, e ela aparece **depois** de a compilação
> concluir sem erro, o que faz parecer defeito do projeto.
>
> Para saber se é ele:
>
> ```powershell
> Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' -Name VerifiedAndReputablePolicyState
> ```
>
> `1` é ligado e impondo. A única saída local é desligá-lo em *Windows Security → Controle de
> aplicativos e navegador*, e **isso é irreversível** — só volta a ligar reinstalando o
> Windows. Ele não aceita exclusão por arquivo nem por pasta, e **certificado autoassinado
> não ajuda**: ele não honra raiz confiada localmente, só CA do Microsoft Trusted Root
> Program. Quem prefere manter a proteção compila noutra máquina ou numa máquina virtual.

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

## Instalar

Dois modos, documentados em [implantação](docs/implantacao.md):

- **MSIX** com atualização automática por App Installer — o caminho normal
- **Sem pacote**, instalando sob `%LOCALAPPDATA%` sem privilégio de administrador — para
  ambientes em que a política bloqueia sideload

O pipeline `release.yml` dispara por tag `v*.*.*` e produz os dois, mais o manifesto de
atualização. A assinatura usa certificado vindo dos segredos do repositório; sem ele o
pipeline gera o pacote sem assinar e avisa, em vez de falhar.

## Documentação

- [Decisões arquiteturais](docs/decisoes-arquiteturais.md) — o porquê das escolhas não
  óbvias e o que quebra se forem revertidas
- [Modelo de dados](docs/modelo-de-dados.md) — entidades, índices e o que nunca é
  persistido
- [Implantação](docs/implantacao.md) — instalação, registro dos aplicativos OAuth e o que
  fica na máquina do usuário
- [Roadmap](docs/roadmap.md) — as dez fases de construção, todas concluídas

## Skills SINTEK

O hook `SessionStart` em `.claude/settings.json` clona
[skills-globais](https://github.com/sintektec/skills-globais) em `.claude/skills/`. Se o
clone falhar — repositório privado sem credencial, proxy bloqueando o host — o script
avisa em stderr e a sessão segue sem as skills.
