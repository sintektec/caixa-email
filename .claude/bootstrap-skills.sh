#!/usr/bin/env bash
# Bootstrap: clona ou atualiza skills globais SINTEK a cada SessionStart.
SKILLS_DIR=".claude/skills"
REPO_URL="https://github.com/sintektec/skills-globais.git"
if [ ! -d "$SKILLS_DIR/.git" ]; then
  git clone --depth 1 "$REPO_URL" "$SKILLS_DIR" >/dev/null 2>&1 || true
else
  (cd "$SKILLS_DIR" && git pull --ff-only >/dev/null 2>&1 || true)
fi