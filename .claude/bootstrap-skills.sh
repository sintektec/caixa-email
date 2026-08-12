#!/usr/bin/env bash
# Popula um diretorio de skills do Claude Code com as skills ativas do catalogo
# skills-globais, clonando o catalogo quando preciso.
#
# ESTE ARQUIVO E O ORIGINAL -- NAO EDITE AS COPIAS INSTALADAS.
#   install.sh / install.ps1  -> copiam para .claude/ de um PROJETO  (modo projeto)
#   install-global.sh         -> copia para ~/.claude/               (modo global)
#
#   Antes cada instalador carregava a sua propria copia do algoritmo. Manter as
#   copias iguais dependia de disciplina, e elas divergiram: a versao do
#   install-global.sh ficou sem suporte a manifesto e voltou a linkar o catalogo
#   inteiro -- reintroduzindo em silencio o exato problema que o manifesto existe
#   para resolver. Um original so elimina essa classe de defeito.
#
# PLACEHOLDERS trocados na instalacao:
#   https://github.com/sintektec/skills-globais.git   URL do catalogo
#   "${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"  expressao shell que resolve a raiz (projeto ou $HOME)
#   bootstrap-skills.log   nome do arquivo de log dentro de .claude/
#   projeto       "projeto" ou "global"
#
# INVARIANTES
#   1. Nenhuma falha e absorvente. Todo estado ruim que ele encontra, ele
#      desfaz. Nunca existe um estado do qual so se sai a mao.
#   2. Duas instancias simultaneas nao se atrapalham: a segunda desiste.
#   3. O caminho final do clone nunca contem um clone parcial.
#   4. Falhar e sair com 0. Uma sessao jamais deixa de abrir por causa disto.

set -uo pipefail

REPO_URL="https://github.com/sintektec/skills-globais.git"
MODO="projeto"
ROOT="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
CLONE="$ROOT/.claude/skills-globais-repo"
SKILLS_DIR="$ROOT/.claude/skills"
LOG="$ROOT/.claude/bootstrap-skills.log"
LOCK="$ROOT/.claude/.bootstrap-skills.lock"
MANIFEST="$CLONE/skills-ativas.txt"

# Idade a partir da qual um lock e considerado orfao, quando a vivacidade do
# dono nao pode ser determinada.
LOCK_TTL_MIN=10

mkdir -p "$SKILLS_DIR" 2>/dev/null || true

# A descoberta de repositorio do git SOBE na arvore. Com um .git parcial dentro
# de um projeto que e ele proprio um repositorio, qualquer comando git rodado no
# clone resolveria para o repositorio DO USUARIO -- e um `git pull` ali faria
# fast-forward do working tree dele no meio do SessionStart. O teto barra a
# subida: em vez de acertar o repo errado, o git responde "not a git repository".
# Precisa do caminho fisico: o git nao resolve symlinks nas entradas do teto.
GIT_CEILING_DIRECTORIES=$(cd "$ROOT/.claude" 2>/dev/null && pwd -P) || GIT_CEILING_DIRECTORIES="$ROOT/.claude"
export GIT_CEILING_DIRECTORIES

# Log SEMPRE anexa. A versao anterior usava `exec > >(tee "$LOG")`, que trunca:
# com duas instancias concorrentes uma zerava o arquivo da outra e o
# diagnostico da falha desaparecia junto com ele.
log() { printf '[%s] %s\n' "$(date -Iseconds)" "$*" >> "$LOG" 2>/dev/null; }

# Rotaciona para o log nao crescer sem limite ao longo de centenas de sessoes.
if [ -f "$LOG" ] && [ "$(wc -c < "$LOG" 2>/dev/null || echo 0)" -gt 262144 ]; then
  mv -f "$LOG" "$LOG.old" 2>/dev/null || true
fi

# ---------------------------------------------------------------------------
# Instancia unica
# ---------------------------------------------------------------------------
# `mkdir` e atomico em POSIX: ou este processo criou o diretorio, ou outro ja
# tinha criado. Serve de mutex sem depender de flock (ausente em varias imagens).
#
# Por que e necessario: dois hooks SessionStart apontando para o mesmo bootstrap
# rodam em paralelo e disputam o mesmo destino de clone. O resultado observado em
# campo foi exatamente o que o git produz nesse caso -- "cannot copy
# .../templates/description to .../.git/description: File exists" e
# ".git/hooks/: No such file or directory" -- deixando para tras um diretorio sem
# .git utilizavel.
lock_velho() { [ -n "$(find "$LOCK" -maxdepth 0 -mmin "+$1" 2>/dev/null)" ]; }

# Quem morre de SIGKILL -- container recolhido, sessao fechada -- nao executa o
# trap, e deixa o lock para tras. Expirar so por tempo transformaria isso numa
# falha absorvente de 10 minutos: mesma classe de defeito, prazo menor. O teste
# que decide e a vivacidade do dono; o tempo fica so de rede de seguranca.
lock_abandonado() {
  local pid
  pid=$(cat "$LOCK/pid" 2>/dev/null || true)
  case "$pid" in
    '' | *[!0-9]*)
      # Sem PID legivel. Ou o dono foi morto entre o mkdir e a gravacao do pid,
      # ou acabou de criar o lock e ainda nao gravou. Da o beneficio da duvida
      # por 1 minuto; passado isso, ninguem vai gravar mais.
      lock_velho 1
      ;;
    *)
      # PID vivo manda: o dono esta trabalhando. Se estiver morto, o lock e
      # lixo e some agora, sem esperar TTL nenhum.
      if kill -0 "$pid" 2>/dev/null; then
        # Reuso de PID existe: um numero reciclado por outro processo faria o
        # lock parecer vivo para sempre. O TTL cobre esse caso residual.
        lock_velho "$LOCK_TTL_MIN"
      else
        return 0
      fi
      ;;
  esac
}

tenho_lock=0
if mkdir "$LOCK" 2>/dev/null; then
  tenho_lock=1
elif lock_abandonado; then
  log "lock abandonado (dono morto sem limpar) -- quebrando"
  rm -rf "$LOCK" 2>/dev/null || true
  mkdir "$LOCK" 2>/dev/null && tenho_lock=1
fi

if [ "$tenho_lock" -ne 1 ]; then
  log "outra instancia do bootstrap esta rodando -- saindo sem tocar em nada"
  exit 0
fi
trap 'rm -rf "$LOCK" 2>/dev/null || true' EXIT INT TERM HUP
echo "$$" > "$LOCK/pid" 2>/dev/null || true

log "bootstrap start (modo=$MODO, pid=$$)"

# ---------------------------------------------------------------------------
# Projeto vendorizado: nao ha o que buscar
# ---------------------------------------------------------------------------
# Quem rodou scripts/vendorizar.* tem as skills como diretorios REAIS
# versionados. Tentar clonar ali so produziria "ERRO: sem acesso" toda sessao,
# poluindo o log sem consertar nada.
#
# So vale no modo projeto. Em ~/.claude/skills existem diretorios reais que sao
# skills NATIVAS do Claude Code (docx, xlsx, pptx, pdf); conta-las como
# vendorizadas faria o bootstrap global desistir sempre, sem instalar nada.
if [ "$MODO" = "projeto" ]; then
  vendorizadas=0
  for d in "$SKILLS_DIR"/*/; do
    [ -d "$d" ] || continue
    [ -L "${d%/}" ] && continue
    [ -f "$d/SKILL.md" ] && vendorizadas=$((vendorizadas + 1))
  done

  # Marcador explicito quando existe; senao, um limiar. Desligar o bootstrap ao
  # ver UM unico diretorio real era demais: uma skill copiada a mao ou instalada
  # por plugin bastava para o projeto nunca mais sincronizar, em silencio. Uma
  # vendorizacao de verdade traz dezenas.
  if [ -f "$SKILLS_DIR/.vendorizado" ] || [ "$vendorizadas" -ge 5 ]; then
    log "$vendorizadas skills vendorizadas no projeto -- nada a fazer."
    log "  Para voltar ao modo clone, remova .claude/skills/ e rode de novo."
    exit 0
  elif [ "$vendorizadas" -gt 0 ]; then
    log "$vendorizadas diretorio(s) real(is) em .claude/skills -- preservados, sync segue."
  fi
fi

# ---------------------------------------------------------------------------
# Clone: saudavel, ou refeito
# ---------------------------------------------------------------------------
# "Existe .git" nao basta: um clone morto no meio deixa .git incompleto.
#
# E perguntar ao git tambem nao basta, por um motivo perigoso: a descoberta de
# repositorio SOBE na arvore. Com um .git parcial dentro de um projeto que e ele
# proprio um repositorio git, `rev-parse --git-dir` responde com o .git DO
# PROJETO -- e o `git pull` seguinte atualizaria o repositorio do usuario em vez
# do catalogo, mexendo no working tree dele.
#
# O unico teste seguro e exigir que a raiz encontrada seja exatamente o clone.
#
# Duas defesas, porque uma so nao basta:
#
#   O teto (GIT_CEILING_DIRECTORIES, definido acima) impede a subida. Sem ele o
#   git responde com o repositorio do projeto e o erro fica invisivel.
#
#   A comparacao por caminho FISICO impede o falso negativo. `--show-toplevel`
#   resolve symlinks; comparar com "$CLONE" literal falha sempre que houver um
#   symlink no caminho do projeto, e ai o clone e refeito e REPROVADO de novo a
#   cada sessao -- o guard final aborta e nenhuma skill e linkada. Verificado:
#   sob caminho com symlink, a versao com comparacao literal nunca funcionava.
clone_ok() {
  [ -d "$CLONE/.git" ] || return 1
  local top esperado
  top=$(git -C "$CLONE" rev-parse --show-toplevel 2>/dev/null) || return 1
  esperado=$(cd "$CLONE" 2>/dev/null && pwd -P) || return 1
  [ "$top" = "$esperado" ]
}

# Restos de tentativas interrompidas (o clone acontece fora do caminho final).
for resto in "$CLONE".tmp.*; do
  [ -e "$resto" ] || continue
  log "removendo resto de clone interrompido: $(basename "$resto")"
  rm -rf "$resto" 2>/dev/null || true
done

# O estado que travava tudo: diretorio ocupado sem repositorio utilizavel.
# `git clone` recusa destino nao-vazio, entao sem esta limpeza a falha se
# repetia identica em toda sessao seguinte, para sempre.
if [ -e "$CLONE" ] && ! clone_ok; then
  log "clone invalido (diretorio existe sem repositorio utilizavel) -- refazendo"
  rm -rf "$CLONE" 2>/dev/null || true
fi

git_out=""; git_rc=0

if clone_ok; then
  git_out=$(git -C "$CLONE" pull --ff-only 2>&1) || git_rc=$?
  if [ "$git_rc" -ne 0 ]; then
    case "$git_out" in
      *"unrelated histories"* | *"Not possible to fast-forward"* | *"diverged"* | *"refusing to merge"*)
        # Clone shallow cujo historico nao alcanca mais a ponta. Refazer custa
        # segundos e e deterministico; remendar um shallow divergente nao e.
        log "historico divergente do clone shallow -- refazendo do zero"
        rm -rf "$CLONE" 2>/dev/null || true
        git_rc=0
        ;;
    esac
  fi
fi

if ! clone_ok; then
  # Clona para um caminho temporario e so entao move. Assim o caminho final ou
  # tem um clone completo, ou nao existe -- nunca um pela metade. Um SIGKILL no
  # meio deixa no maximo um .tmp.NNN, recolhido na proxima execucao.
  tmp="$CLONE.tmp.$$"
  rm -rf "$tmp" 2>/dev/null || true
  log "clonando $REPO_URL"
  if git_out=$(git clone --depth 1 "$REPO_URL" "$tmp" 2>&1); then
    git_rc=0
    rm -rf "$CLONE" 2>/dev/null || true
    if ! mv "$tmp" "$CLONE" 2>/dev/null; then
      log "ERRO: clone concluido mas nao consegui move-lo para $CLONE"
      rm -rf "$tmp" 2>/dev/null || true
    fi
  else
    git_rc=$?
    rm -rf "$tmp" 2>/dev/null || true
  fi
fi

if [ "$git_rc" -ne 0 ]; then
  log "$git_out"
  case "$git_out" in
    *"Authentication failed"* | *"could not read Username"* | *"Invalid username or token"* | *"not found"* | *"Permission denied"* | *"Repository not found"*)
      log "ERRO: sem acesso a $REPO_URL."
      if [ "$MODO" = "projeto" ]; then
        log "  Em container remoto o git so alcanca repositorios ANEXADOS ao"
        log "  ambiente. Anexe sintektec/skills-globais e reabra a sessao."
      fi
      log "  Fora de container: gh auth login && gh auth setup-git"
      ;;
    *"Could not resolve host"* | *"unable to access"* | *"Connection timed out"*)
      log "AVISO: sem rede. Skills ja linkadas foram preservadas."
      ;;
    *)
      log "AVISO: git falhou (rc=$git_rc)."
      ;;
  esac
fi

# Guard correto. O anterior testava `[ -d "$CLONE" ]`, que um diretorio vazio ou
# parcial satisfaz -- o script seguia, nao linkava nada, e ainda avisava "sem
# skills-ativas.txt, linkando TUDO", mensagem que descrevia o oposto do ocorrido.
if ! clone_ok; then
  log "abortado: catalogo indisponivel. Nada foi removido do que ja existia."
  exit 0
fi

# ---------------------------------------------------------------------------
# Manifesto: linka so as ativas
# ---------------------------------------------------------------------------
# Sem manifesto o catalogo inteiro (1.300+) estoura o orcamento de listing do
# harness (~1% da janela) e as skills passam a aparecer e sumir. A lista vira
# uma unica string delimitada por '|' e o teste de pertinencia sai por
# casamento de padrao do proprio shell -- sem fork por skill.
ACTIVE=""; use_manifest=0; n_active=0
if [ -f "$MANIFEST" ]; then
  use_manifest=1
  while IFS= read -r line; do
    line="${line%%#*}"; line="$(printf '%s' "$line" | tr -d '[:blank:]')"
    [ -n "$line" ] || continue
    ACTIVE="$ACTIVE|$line"; n_active=$((n_active + 1))
  done < "$MANIFEST"
  ACTIVE="$ACTIVE|"
  log "manifesto: $n_active skills ativas"
else
  log "AVISO: clone sem skills-ativas.txt -- linkando TUDO."
  log "  Isso estoura o orcamento de listing e reintroduz o truncamento."
fi

is_active() {
  [ "$use_manifest" = 0 ] && return 0
  case "$ACTIVE" in *"|$1|"*) return 0 ;; *) return 1 ;; esac
}

created=0; skipped=0; removed=0; blocked=0
for skill_dir in "$CLONE"/*/; do
  [ -d "$skill_dir" ] || continue
  name=$(basename "$skill_dir")
  case "$name" in .git | .github | .claude | scripts | templates) continue ;; esac
  [ -f "$skill_dir/SKILL.md" ] || continue
  is_active "$name" || continue

  target="$SKILLS_DIR/$name"
  if [ -L "$target" ]; then
    ln -sfn "${skill_dir%/}" "$target" 2>/dev/null && skipped=$((skipped + 1))
  elif [ -e "$target" ]; then
    # Diretorio real de mesmo nome: skill nativa do Claude Code ou instalada por
    # plugin. Nunca sobrescreve.
    blocked=$((blocked + 1))
  else
    ln -s "${skill_dir%/}" "$target" 2>/dev/null && created=$((created + 1))
  fi
done

# Remove links quebrados ou desativados que apontem para o NOSSO clone. Só se
# chega aqui com o catalogo em disco, entao link quebrado significa mesmo skill
# ausente, e nao falha de rede. Link para outro lugar fica intacto.
while IFS= read -r link; do
  [ -n "$link" ] || continue
  dest=$(readlink "$link" 2>/dev/null) || continue
  case "$dest" in "$CLONE"/*) ;; *) continue ;; esac
  if [ ! -e "$link" ] || ! is_active "$(basename "$link")"; then
    rm -f "$link" && removed=$((removed + 1))
  fi
done < <(find "$SKILLS_DIR" -maxdepth 1 -type l -print 2>/dev/null)

total=$(find "$SKILLS_DIR" -maxdepth 1 -mindepth 1 \( -type l -o -type d \) 2>/dev/null | wc -l | tr -d ' ')
log "done: $created novos, $skipped reapontados, $removed removidos, $blocked preservados, $total total"

# Falha de sync nunca impede a sessao de abrir.
exit 0
