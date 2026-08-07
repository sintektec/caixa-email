#!/usr/bin/env bash
# Bootstrap: clona ou atualiza as skills globais SINTEK a cada SessionStart.
#
# A versão anterior redirecionava toda a saída para /dev/null e terminava com `|| true`.
# O efeito era que uma falha de clone — repositório privado sem credencial, proxy
# bloqueando o host, rede indisponível — passava despercebida: a sessão começava com zero
# skills e nenhum aviso, enquanto o README prometia ~1.346.
#
# Agora a falha é reportada em stderr. O `exit 0` ao final é intencional: um hook de
# SessionStart que falha impediria a sessão de iniciar, e ficar sem as skills opcionais é
# um problema menor do que não conseguir trabalhar.

set -uo pipefail

SKILLS_DIR=".claude/skills"
REPO_URL="https://github.com/sintektec/skills-globais.git"

# Evita que o git abra prompt de credencial e trave o início da sessão indefinidamente.
export GIT_TERMINAL_PROMPT=0
export GIT_ASKPASS=/bin/true

warn() {
  printf 'bootstrap-skills: %s\n' "$1" >&2
}

if [ ! -d "$SKILLS_DIR/.git" ]; then
  if ! output=$(git clone --depth 1 "$REPO_URL" "$SKILLS_DIR" 2>&1); then
    warn "não foi possível clonar $REPO_URL"
    warn "${output}"
    warn "a sessão segue sem as skills globais; verifique acesso ao repositório e ao proxy"
    exit 0
  fi
  warn "skills globais clonadas em $SKILLS_DIR"
else
  if ! output=$(git -C "$SKILLS_DIR" pull --ff-only 2>&1); then
    warn "não foi possível atualizar as skills globais em $SKILLS_DIR"
    warn "${output}"
    warn "seguindo com a cópia local já existente"
    exit 0
  fi
fi

exit 0
