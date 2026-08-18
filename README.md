# SINTEK Claude Code Project Template

Template para iniciar um novo projeto Claude Code ja configurado para consumir as skills globais SINTEK (centurionarx-cp/skills-globais).

## Como usar

### Opcao 1 - Botao "Use this template" (recomendado)

1. Clique em "Use this template" no topo desta pagina
2. De nome ao seu projeto novo
3. Faca git clone do seu novo repo
4. Abra o projeto no Claude Code
5. Na primeira sessao, o hook SessionStart clona skills-globais em .claude/skills/ automaticamente
6. Pronto - ~1.346 skills disponiveis

### Opcao 2 - Copy manual

Use os comandos do git clone direto deste repo, remova o .git e refaca git init.

## O que vem dentro

- .claude/bootstrap-skills.sh - clona/atualiza skills-globais em .claude/skills/
- .claude/settings.json - hook SessionStart que invoca o bootstrap
- .gitignore - ignora .claude/skills/

## Refs

- Skills: https://github.com/centurionarx-cp/skills-globais
- Catalogo: https://github.com/centurionarx-cp/skills-globais/blob/main/INDEX.md