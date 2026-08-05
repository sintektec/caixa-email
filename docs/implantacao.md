# Implantação

Como colocar o Sintek.Mail em máquinas Windows 11, e o que precisa estar pronto antes.

Há dois modos de instalação. Escolha um:

| | MSIX | Sem pacote |
|---|---|---|
| Atualização automática | Sim, por App Installer | Não — reexecutar o script |
| Exige sideload liberado | Sim | Não |
| Exige .NET na máquina | Não | Não (publicação autocontida) |
| Privilégio de administrador | Não | Não (instala em `%LOCALAPPDATA%`) |

O MSIX é o caminho normal. O modo sem pacote existe para ambientes em que a política de
grupo bloqueia sideload — e nesse caso a atualização volta a ser um processo manual, que é
o preço de contornar o bloqueio.

---

## 1. Pré-requisitos de quem publica

### Certificado de assinatura de código

O MSIX precisa ser assinado por um certificado em que a máquina de destino confie. As duas
opções que funcionam:

- **Certificado de CA pública** (DigiCert, Sectigo e afins) — confiado por padrão em
  qualquer Windows. É o que se usa quando as máquinas não são todas do mesmo domínio.
- **Certificado da PKI interna** — mais barato e suficiente quando as máquinas recebem a
  CA raiz da organização por política de grupo.

Exporte como `.pfx` com a chave privada e converta para base64:

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes('sintek-mail.pfx')) | Set-Clipboard
```

Cadastre em **Settings → Secrets and variables → Actions** do repositório:

| Segredo | Conteúdo |
|---|---|
| `SIGNING_CERTIFICATE_BASE64` | O `.pfx` em base64 |
| `SIGNING_CERTIFICATE_PASSWORD` | A senha do `.pfx` |

E como **variável** (não segredo — é uma URL pública):

| Variável | Conteúdo |
|---|---|
| `APPINSTALLER_BASE_URI` | URL HTTPS da pasta que servirá o `.msix` e o `.appinstaller` |

> O `Publisher` do `Package.appxmanifest` precisa bater **exatamente** com o *subject* do
> certificado. Divergência aqui produz um erro de assinatura que não diz o que está
> errado — vale conferir antes com
> `certutil -dump sintek-mail.pfx | Select-String Subject`.

### Servidor de distribuição

Uma pasta servida por HTTPS com os dois arquivos lado a lado:

```
https://.../sintek-mail/Sintek.Mail.appinstaller
https://.../sintek-mail/Sintek.Mail_1.0.0.0_x64.msix
```

Qualquer coisa que sirva arquivos estáticos serve — IIS, nginx, um bucket. O único
requisito real é o MIME type: alguns servidores devolvem `.appinstaller` como
`application/octet-stream` e o Windows recusa. O correto é
`application/appinstaller`.

---

## 2. Registro dos aplicativos OAuth

Sem estas credenciais, os provedores aparecem no assistente de conta como "não
configurados". A aplicação continua funcionando com senha em servidor próprio — IMAP, SMTP e
agenda CalDAV. O que fica indisponível é conta Gmail, Outlook.com/Microsoft 365, e as agendas
desses dois.

**Os dois provedores discordam sobre client secret, e a diferença muda o que você coleta.**
No Entra ID um cliente público **não tem** segredo: o fluxo é *Authorization Code + PKCE*, e
o `PublicClientApplicationBuilder` do MSAL sequer expõe `WithClientSecret`. A Google emite
**Client ID e Client secret** para o tipo *Aplicativo para computador*, e exige o segundo na
troca do código e na renovação por *refresh token* — só os tipos iOS e Android saem sem
segredo.

O Client secret da Google é credencial de **aplicativo**, não de usuário: a própria Google
documenta que ele fica embutido no app e que um aplicativo instalado não guarda segredo de
verdade. Por isso ele vai em configuração, junto do Client ID. O que vai para o Gerenciador
de Credenciais é o **token de atualização**, esse sim equivalente à senha de quem entrou.

### Microsoft (Entra ID)

1. Portal do Azure → **Microsoft Entra ID** → **Registros de aplicativo** → **Novo
   registro**.
2. Nome: `Sintek.Mail`. Tipos de conta: escolha conforme o público — "somente neste
   diretório organizacional" para uso interno. Essa escolha define o `TenantId`: use o ID do
   diretório para registro de locatário único, ou `common` para aceitar qualquer locatário e
   contas pessoais.
3. **URI de redirecionamento**: plataforma **Cliente público / nativo**, valor
   `http://localhost`.
4. Em **Autenticação**, confirme que *Allow public client flows* está **habilitado**. Sem
   isso o MSAL falha.
5. Em **Permissões de API**, adicione as delegadas — **atenção, elas vivem em duas APIs
   diferentes**:

   | Permissão | Onde encontrá-la no portal |
   |---|---|
   | `IMAP.AccessAsUser.All` | *APIs que minha organização usa* → **Office 365 Exchange Online** |
   | `SMTP.Send` | *APIs que minha organização usa* → **Office 365 Exchange Online** |
   | `Calendars.ReadWrite` | **Microsoft Graph** |
   | `offline_access` | **Microsoft Graph** |

   Procurar as duas primeiras dentro do Microsoft Graph é o erro mais comum aqui — elas não
   estão lá.
6. Conceda consentimento do administrador se a organização exigir.
7. Copie o **ID do aplicativo (cliente)** e o **ID do diretório (locatário)**.

> **O consentimento vai pedir duas vezes, e isso não é defeito.** O Entra emite token *por
> recurso*: um token de `outlook.office.com` não abre o `graph.microsoft.com`, e pedir os
> dois na mesma chamada é recusado com `AADSTS28000`. O assistente pede os escopos de e-mail
> e depois os de agenda. Recusar o segundo não invalida o primeiro — a conta é cadastrada e a
> agenda fica sem espelho remoto até o usuário consentir.

### Google Cloud

1. Console do Google Cloud → **APIs e serviços** → **Biblioteca**. Ative **as duas**:
   - **Gmail API** (IMAP e SMTP)
   - **Google Calendar API** (agenda)
2. **Tela de permissão OAuth**: escolha o tipo de usuário — ver o quadro abaixo, porque essa
   escolha é a mais cara de reverter. Declare os escopos:
   - `https://mail.google.com/`
   - `https://www.googleapis.com/auth/calendar`
3. **Credenciais** → **Criar credenciais** → **ID do cliente OAuth** → tipo
   **Aplicativo para computador**.
4. Copie **os dois valores**: o *ID do cliente* e a *Chave secreta do cliente*.

> **Interno ou Externo — decida antes de registrar.**
>
> **Interno** (só disponível com Google Workspace, restringe aos usuários do próprio
> domínio): não exige verificação, não exige avaliação de segurança, não tem limite de
> usuários. É o caminho para uso corporativo em domínio próprio.
>
> **Externo em modo *Testing***: limitado a 100 contas de teste, e o *refresh token*
> **vence em 7 dias**. Serve para experimentar, não para operar — a conta pára de sincronizar
> toda semana.
>
> **Externo publicado**: exige verificação do aplicativo pelo Google, porque
> `https://mail.google.com/` é um **escopo restrito**. Isso inclui a avaliação de segurança
> **CASA**, feita por avaliador credenciado, com custo e **renovação anual**. Um aplicativo
> que guarda os dados apenas na máquina do usuário — como este — tem processo mais leve, mas
> não isento.

### Onde colocar

Crie `appsettings.Local.json` ao lado do executável — ele fica fora do controle de versão
e sobrevive às atualizações do modo sem pacote:

```json
{
  "OAuth": {
    "Microsoft": {
      "ClientId": "00000000-0000-0000-0000-000000000000",
      "TenantId": "00000000-0000-0000-0000-000000000000",
      "RedirectUri": "http://localhost"
    },
    "Google": {
      "ClientId": "000000000000-xxxxxxxxxxxxxxxx.apps.googleusercontent.com",
      "ClientSecret": "GOCSPX-xxxxxxxxxxxxxxxxxxxxxxxx",
      "RedirectUri": "http://localhost"
    }
  }
}
```

O `ClientSecret` só existe no bloco Google. Preenchê-lo no bloco Microsoft não tem efeito —
o Entra ID não emite um, e a aplicação nunca o lê.

No modo MSIX o arquivo vai na pasta do pacote instalado, o que exige reimplantar para
alterá-lo. Em frota grande, distribuir os Client IDs por política de grupo
(`SINTEK_MAIL_OAuth__Microsoft__ClientId` como variável de ambiente) evita reempacotar a
cada mudança — a aplicação lê variáveis com o prefixo `SINTEK_MAIL_`.

---

## 3. Assistente de IA (opcional)

A assistência por IA usa **modelo local por padrão** e não exige nada da rede. Para
habilitá-la, instale na máquina um runtime com API no formato OpenAI — Ollama, LM Studio
ou llama.cpp — e confirme o endereço em `appsettings.Local.json`:

```json
{
  "Assistant": {
    "Local": {
      "Endpoint": "http://127.0.0.1:11434/v1/chat/completions",
      "Model": "llama3.2"
    }
  }
}
```

Sem runtime instalado, os botões de IA simplesmente não aparecem.

O provedor em nuvem é opcional, desligado por padrão e **exige duas coisas**: a
configuração abaixo, e a autorização explícita em cada Diretório de Domínio cujo conteúdo
possa ser enviado. A chave nunca vai no arquivo — é gravada no Gerenciador de Credenciais
do Windows sob a chave indicada em `CredentialKey`.

```json
{
  "Assistant": {
    "Cloud": {
      "Endpoint": "https://api.exemplo.com/v1/chat/completions",
      "Model": "modelo",
      "DisplayName": "Serviço de IA corporativo",
      "CredentialKey": "sintek-mail/assistant/cloud"
    }
  }
}
```

```powershell
cmdkey /generic:sintek-mail/assistant/cloud /user:api /pass
```

---

## 4. Publicar uma versão

Marque a versão e empurre a tag. O pipeline `release.yml` faz o resto:

```bash
git tag v1.0.0
git push origin v1.0.0
```

Ele gera três artefatos:

- `Sintek.Mail_1.0.0.0_x64.msix` — assinado, se os segredos estiverem cadastrados
- `Sintek.Mail.appinstaller` — manifesto de atualização
- `sintek-mail-unpackaged.zip` — publicação autocontida com o script de instalação

Copie o `.msix` e o `.appinstaller` para o servidor de distribuição, na mesma pasta.

> Sem `SIGNING_CERTIFICATE_BASE64` cadastrado o pipeline **não falha**: gera o pacote sem
> assinatura e emite um aviso. É útil para validar o empacotamento antes de ter
> certificado, mas um MSIX sem assinatura não instala em máquina nenhuma sem que o
> sideload de pacotes não confiáveis seja liberado.

---

## 5. Instalar nas máquinas

### MSIX

Requisito: **Configurações → Sistema → Para desenvolvedores → Aplicativos sideload**
habilitado, ou a política de grupo equivalente
(`Computer Configuration → Administrative Templates → Windows Components → App Package
Deployment → Allow all trusted apps to install`).

O usuário abre a URL do `.appinstaller` no navegador e o Windows conduz a instalação.
Em frota, o mesmo arquivo pode ser distribuído por Intune ou por script:

```powershell
Add-AppxPackage -AppInstallerFile 'https://.../sintek-mail/Sintek.Mail.appinstaller'
```

A partir daí a atualização é automática: o Windows consulta o manifesto a cada 8 horas de
uso e instala a versão nova em segundo plano.

### Sem pacote

```powershell
Expand-Archive sintek-mail-unpackaged.zip -DestinationPath .\sintek-mail
cd .\sintek-mail
.\install-unpackaged.ps1
```

Instala em `%LOCALAPPDATA%\Programs\Sintek.Mail`, cria o atalho e registra a desinstalação
no Painel de Controle. Atualizar é reexecutar o script com a versão nova —
`appsettings.Local.json` é preservado.

Desinstalar:

```powershell
.\install-unpackaged.ps1 -Uninstall
```

A desinstalação **não apaga** o banco local nem as credenciais. Dados do usuário não somem
por remoção de programa; o script informa os caminhos para quem quiser apagá-los também.

---

## 6. O que fica na máquina do usuário

| O quê | Onde | Sai na desinstalação? |
|---|---|---|
| Banco de mensagens (cifrado) | `%LOCALAPPDATA%\Sintek.Mail\mail.db` | Não |
| Anexos baixados | `%LOCALAPPDATA%\Sintek.Mail\Attachments` | Não |
| Senhas, tokens e chave do banco | Gerenciador de Credenciais do Windows | Não |
| Aplicação | `%LOCALAPPDATA%\Programs\Sintek.Mail` ou pacote MSIX | Sim |

O banco é cifrado com SQLCipher e a chave vive no Gerenciador de Credenciais — copiar o
arquivo para outra máquina não dá acesso ao conteúdo. Em contrapartida, **perder o perfil
do Windows do usuário significa perder a chave**: o banco local vira ilegível e a caixa
precisa ser ressincronizada do servidor. Não há mecanismo de recuperação, e é assim de
propósito.

Os anexos ficam em disco fora do banco cifrado. A limpeza de cache
(**Configurações → Analisar cache**) os descarta quando necessário, preservando os
metadados: o conteúdo volta a ser baixado sob demanda.
