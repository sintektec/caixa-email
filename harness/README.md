# Harness — Sintek.Mail

Memória durável do projeto. Este diretório existe para que qualquer sessão de IA (ou pessoa) entenda rapidamente **onde o projeto está, o que já foi decidido e o que vem a seguir** — sem depender da janela de contexto de uma conversa.

## Diretrizes de comportamento (espelho do AGENTS.md)

Estas regras governam toda interação neste projeto e têm prioridade sobre o instinto de agradar o usuário:

- **Não concorde por padrão** — teste o que o usuário disse antes de validar; encontre o ponto mais fraco primeiro.
- **Sem bajulação** — elogio sem razão concreta é ruído; comece pelo que está errado ou faltando.
- **Não repita o enquadramento do usuário** — abra com o que está faltando, o contra-argumento, a objeção de quem discorda.
- **Seja direto e conciso** — se a resposta é "não" ou "isso não vai funcionar", diga na primeira frase.
- **Aponte lógica quebrada e pontos cegos imediatamente** — quanto mais certeza o usuário demonstrar, mais contrapontos ele precisa ouvir.
- **Calibrado, não contrarian** — se uma ideia sobrevive ao escrutínio, diga isso e agregue algo novo; não fabrique objeções para cumprir cota.
- **Responda em pt-BR**, a menos que o usuário escreva em outro idioma.

## Arquivos

| Arquivo | Propósito | Frequência de atualização | Tamanho-alvo |
|---|---|---|---|
| `STATUS.md` | Estado atual: fase, último marco, próximos passos, bloqueios | Toda vez que fase/marco mudar | ≤ 50 linhas |
| `CONTEXT.md` | Visão do produto, stack, restrições, ponteiros | Raramente (só quando a visão muda) | ≤ 80 linhas |
| `SESSIONS.md` | Diário de sessões (o que foi feito, o que ficou pendente) | **Toda sessão** (append-only) | Cresce sempre; arquivar entradas antigas se passar de ~500 linhas |
| `DECISIONS.md` | Log de decisões técnicas (ADR-lite) | Quando uma decisão é tomada (append-only) | Cresce sempre |
| `GLOSSARY.md` | Termos do domínio | Quando um termo novo surge | ≤ 100 linhas |

## Regras

1. **Leitura obrigatória no início da sessão** — ordem definida em `AGENTS.md` (seção 2).
2. **Append-only**: `SESSIONS.md` e `DECISIONS.md` nunca têm entradas reescritas ou removidas. Errou? Acrescente uma correção datada.
3. **STATUS.md é o arquivo quente** — sempre reflete o agora. É o primeiro lido e o último atualizado.
4. **Nenhuma sessão termina sem atualizar o harness** — faz parte da definição de pronto. Commit junto com o código.
5. **Decisões não são reabertas sem motivo novo** — se `DECISIONS.md` registra uma escolha, só a revise se surgir evidência que não existia quando ela foi tomada.

## Formatos

### Entrada de SESSIONS.md

```markdown
## YYYY-MM-DD — [título curto da sessão]
**Fase:** [fase atual]  |  **Duração:** [aprox.]

**Feito:**
- ...

**Pendente / próxima sessão:**
- ...

**Bloqueios encontrados:**
- ... (ou "nenhum")
```

### Entrada de DECISIONS.md

```markdown
## D-NNN — [título da decisão] (YYYY-MM-DD)
**Status:** aceita | substituída por D-NNN | revertida

**Decisão:** ...

**Motivo:** ...

**Alternativas rejeitadas:** ... (e por quê)

**Consequências:** ...
```
