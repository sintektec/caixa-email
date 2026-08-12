---
name: catalogo
description: "Indice pesquisavel do catalogo completo de skills skills-globais da SINTEK, incluindo as que nao estao ativas no momento. Use quando a tarefa pedir uma capacidade que nenhuma skill carregada cobre, quando o usuario perguntar se existe skill para algum assunto, ou quando pedir para ativar/desativar skills. Busca por assunto, mostra o que existe e ativa sob demanda."
category: meta
risk: safe
source: local
version: "2.0.0"
tags: "[catalogo, indice, descoberta, skills, meta, ativacao, manifesto, busca]"
date_added: "2026-08-07"
---

# Catalogo

Ponte entre o **catalogo completo** (1.300+ skills no repositorio) e o **catalogo
ativo** (as poucas dezenas carregadas na sessao).

Esta skill viaja com um **indice embarcado**: `indice-skills.tsv`, no mesmo
diretorio deste arquivo. Uma linha por skill, com descricao. **A busca funciona
sempre** — inclusive num projeto que nao alcanca o repositorio do catalogo.

## Por que esta skill existe

O Claude Code reserva ~1% da janela de contexto para a listagem de skills. As
skills do repositorio passam de 60.000 tokens de nome+descricao — cerca de 30x o
orcamento numa janela de 200k. **Mesmo so os nomes, sem descricao alguma, dariam
6.770 tokens** — ainda 3,4x acima. Nao existe configuracao que torne o catalogo
inteiro residente: e limite aritmetico.

O harness lida com o excesso truncando, e o conjunto que sobrevive ao corte nao e
estavel — foi o que causava skills aparecerem no inicio da sessao e sumirem
depois.

A solucao e nao manter tudo residente: `skills-ativas.txt` decide o que fica
carregado, e **esta skill da acesso ao resto sob demanda**, custando uma unica
entrada no listing.

### Por que o indice e separado do corpo

| | Tamanho | Cabe em cada projeto? |
|---|---|---|
| Corpo de todas as skills | **103 MB** | nao |
| Nome + descricao de todas | **243 KB** | **sim** |

Por isso **descobrir** funciona em qualquer lugar, e so **obter o corpo** de uma
skill depende de alcancar o catalogo.

## Quando usar

- A tarefa pede uma capacidade que nenhuma skill carregada cobre.
- O usuario pergunta "existe skill para X?".
- O usuario pede para ativar, desativar ou listar skills.

## 1. Localize o indice

Ele fica ao lado deste arquivo. Procure nesta ordem e use o primeiro que existir:

```bash
for c in \
  "$CLAUDE_PROJECT_DIR/.claude/skills/catalogo/indice-skills.tsv" \
  "$HOME/.claude/skills/catalogo/indice-skills.tsv" \
  "$CLAUDE_PROJECT_DIR/.claude/skills-globais-repo/catalogo/indice-skills.tsv"
do [ -f "$c" ] && INDICE="$c" && break; done
echo "${INDICE:-NAO ENCONTRADO}"
```

```powershell
$INDICE = @(
  "$env:USERPROFILE\.claude\skills\catalogo\indice-skills.tsv"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
```

## 2. Busque

Cada linha e `nome<TAB>categoria<TAB>descricao`, entao **um acerto de grep ja
traz o registro inteiro**. `*` no inicio marca skill ja ativa.

```bash
grep -i 'kubernetes\|helm' "$INDICE" | cut -c1-160
grep -i 'terraform' "$INDICE" | awk -F'\t' '{printf "%-34s %s\n", $1, substr($3,1,110)}'
grep -c '^\*' "$INDICE"          # quantas ja estao ativas
```

**Nunca leia o indice inteiro** — sao 243 KB (~60.000 tokens) e estouraria o
contexto pelo mesmo motivo que o catalogo nao pode ser residente. Sempre grep.

Apresente as candidatas ao usuario com uma linha de descricao cada.

## 3. Obtenha o corpo da skill

Descobrir e barato; trazer a skill exige o catalogo em disco. Descubra se ele
esta alcancavel:

```bash
# clone do bootstrap no projeto, ou alvo de um link ja existente
REPO=""
[ -d "$CLAUDE_PROJECT_DIR/.claude/skills-globais-repo/.git" ] && REPO="$CLAUDE_PROJECT_DIR/.claude/skills-globais-repo"
[ -z "$REPO" ] && [ -L "$HOME/.claude/skills/catalogo" ] && REPO=$(dirname "$(readlink "$HOME/.claude/skills/catalogo")")
echo "${REPO:-INDISPONIVEL}"
```

### Caso A — catalogo alcancavel

```bash
bash "$REPO/scripts/ativar-skill.sh" nome-da-skill outra-skill
```

```powershell
powershell -ExecutionPolicy Bypass -File "$repo\scripts\ativar-skill.ps1" nome-da-skill
```

O script adiciona ao `skills-ativas.txt` e cria o link na hora.

> **Fica disponivel na sessao corrente.** O harness re-escaneia o diretorio de
> skills durante a sessao: a skill ativada aparece em ate uma chamada de
> ferramenta, sem reabrir nada. Verificado no Claude Code on the web.

### Caso B — catalogo indisponivel

Acontece quando `sintektec/skills-globais` **nao esta anexado ao ambiente** desta
sessao remota, ou nao ha rede. **Diga isso ao usuario em vez de falhar mudo**, e
de as duas saidas:

1. **Anexar o repositorio ao ambiente** do projeto e reabrir a sessao — passa a
   valer para todas as skills, nao so esta.
2. **Copiar a skill para dentro do projeto**, na maquina onde o catalogo existe:

   ```powershell
   powershell -ExecutionPolicy Bypass `
     -File C:\DESENVOLVIMENTO\skills-globais\scripts\vendorizar.ps1 `
     -Projeto C:\caminho\do\projeto -Skills nome-da-skill
   ```

Nunca invente o conteudo de uma skill que voce nao conseguiu ler. Diga o que ela
faz — o indice tem a descricao — e qual dos dois caminhos seguir.

## 4. Depois de usar: pergunte o que fica

**Skill copiada para dentro de um projeto nao sai sozinha.** Ela passa a custar
listing em toda sessao daquele projeto e a envelhecer em relacao ao catalogo.

Ao terminar uma tarefa em que voce trouxe skills sob demanda, liste o que foi
trazido e pergunte, por item, **manter ou remover**:

```bash
# o que foi copiado para este projeto (diretorios reais, nao links)
find "$CLAUDE_PROJECT_DIR/.claude/skills" -maxdepth 1 -mindepth 1 -type d \
  -exec test -f {}/SKILL.md \; -print | xargs -n1 basename
```

Sugira **manter** o que o projeto vai usar de novo, e **remover** o que serviu
uma vez. Remover e sempre reversivel: o catalogo continua com tudo.

```bash
rm -rf "$CLAUDE_PROJECT_DIR/.claude/skills/nome-da-skill"
bash "$REPO/scripts/ativar-skill.sh" --remover nome-da-skill   # se veio por link
```

Nao decida sozinho: e o dono do projeto que sabe se aquilo se repete.

## Custo de ativar demais

Cada skill ativa consome contexto em toda sessao. Ordem de grandeza pelo tamanho
medio das descricoes deste catalogo (~180 chars ≈ 45 tokens por entrada):

| Skills ativas | Listing | Situacao |
|---|---|---|
| 95 (selecao atual) | ~4.800 tokens | Confortavel |
| 200 | ~10.000 tokens | Limite de uma janela de 1M |
| 500 | ~25.000 tokens | Truncamento volta |
| tudo (1.300+) | 60.000+ tokens | O problema original |

Se o usuario for ativar muita coisa de uma vez, diga o custo antes. E melhor
ativar sob demanda e podar depois do que reconstruir o catalogo inteiro.

## Manutencao do indice

O indice e gerado, nao editado:

```bash
python3 scripts/gerar-indice.py              # regera
python3 scripts/gerar-indice.py --verificar  # so checa se esta em dia
```

Rode depois de adicionar ou remover skills do catalogo. Um indice velho nao
quebra nada — so deixa de mostrar o que chegou depois dele.
