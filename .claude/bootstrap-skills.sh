#!/usr/bin/env bash
# Popula .claude/skills/ com as skills ativas do catalogo skills-globais.
# Versionado de proposito: em container remoto, so o que vem no clone existe.
set -uo pipefail

REPO_URL="https://github.com/sintektec/skills-globais.git"
ROOT="${CLAUDE_PROJECT_DIR:-$(pwd)}"
CLONE="$ROOT/.claude/skills-globais-repo"
SKILLS_DIR="$ROOT/.claude/skills"
LOG="$ROOT/.claude/bootstrap-skills.log"
MANIFEST="$CLONE/skills-ativas.txt"

mkdir -p "$SKILLS_DIR"
exec > >(tee "$LOG") 2>&1
echo "[$(date -Iseconds)] bootstrap start"

git_out=""; git_rc=0
if [ ! -d "$CLONE/.git" ]; then
  echo "[$(date -Iseconds)] cloning $REPO_URL"
  git_out=$(git clone --depth 1 "$REPO_URL" "$CLONE" 2>&1) || git_rc=$?
else
  git_out=$(git -C "$CLONE" pull --ff-only 2>&1) || git_rc=$?
fi

if [ "$git_rc" -ne 0 ]; then
  echo "$git_out"
  case "$git_out" in
    *"Authentication failed"* | *"could not read Username"* | *"not found"* | *"Permission denied"*)
      echo "[$(date -Iseconds)] ERRO: sem acesso a $REPO_URL."
      echo "  Em container remoto, o git so alcanca repositorios ANEXADOS ao"
      echo "  ambiente. Anexe sintektec/skills-globais e reabra a sessao."
      echo "  Fora de container: gh auth login && gh auth setup-git"
      ;;
    *"Could not resolve host"* | *"unable to access"*)
      echo "[$(date -Iseconds)] AVISO: sem rede. Skills ja linkadas preservadas."
      ;;
    *)
      echo "[$(date -Iseconds)] AVISO: git falhou (rc=$git_rc)."
      ;;
  esac
  [ -d "$CLONE" ] || { echo "[$(date -Iseconds)] abortado: catalogo indisponivel"; exit 0; }
fi

# Manifesto: linka so as ativas. Sem ele, as 1.300+ estouram o orcamento de
# listing do harness (~1% da janela) e as skills passam a aparecer e sumir.
ACTIVE=""; use_manifest=0; n_active=0
if [ -f "$MANIFEST" ]; then
  use_manifest=1
  while IFS= read -r line; do
    line="${line%%#*}"; line="$(printf '%s' "$line" | tr -d '[:blank:]')"
    [ -n "$line" ] || continue
    ACTIVE="$ACTIVE|$line"; n_active=$((n_active + 1))
  done < "$MANIFEST"
  ACTIVE="$ACTIVE|"
  echo "[$(date -Iseconds)] manifesto: $n_active skills ativas"
else
  echo "[$(date -Iseconds)] AVISO: sem skills-ativas.txt -- linkando TUDO."
  echo "  Isso estoura o orcamento de listing e reintroduz o truncamento."
fi

is_active() {
  [ "$use_manifest" = 0 ] && return 0
  case "$ACTIVE" in *"|$1|"*) return 0 ;; *) return 1 ;; esac
}

created=0; skipped=0; removed=0
for skill_dir in "$CLONE"/*/; do
  [ -d "$skill_dir" ] || continue
  name=$(basename "$skill_dir")
  case "$name" in .git | .github | .claude | scripts) continue ;; esac
  [ -f "$skill_dir/SKILL.md" ] || continue
  is_active "$name" || continue

  target="$SKILLS_DIR/$name"
  if [ -L "$target" ]; then
    ln -sfn "${skill_dir%/}" "$target" 2>/dev/null && skipped=$((skipped + 1))
  elif [ ! -e "$target" ]; then
    ln -s "${skill_dir%/}" "$target" 2>/dev/null && created=$((created + 1))
  fi
done

while IFS= read -r link; do
  [ -n "$link" ] || continue
  dest=$(readlink "$link" 2>/dev/null) || continue
  case "$dest" in "$CLONE"/*) ;; *) continue ;; esac
  if [ ! -e "$link" ] || ! is_active "$(basename "$link")"; then
    rm -f "$link" && removed=$((removed + 1))
  fi
done < <(find "$SKILLS_DIR" -maxdepth 1 -type l -print 2>/dev/null)

total=$(find "$SKILLS_DIR" -maxdepth 1 -mindepth 1 \( -type l -o -type d \) 2>/dev/null | wc -l | tr -d ' ')
echo "[$(date -Iseconds)] done: $created novos, $skipped reapontados, $removed removidos, $total total"
exit 0