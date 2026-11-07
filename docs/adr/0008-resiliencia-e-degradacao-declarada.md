# ADR-0008 — Resiliência e degradação declarada

- **Status:** aceito
- **Data:** 2026-11-07
- **Requisitos relacionados:** RNF-01 (instabilidade), RNF-03 (falhas parciais), RNF-04 (consistência)

## Problema

O enunciado pede que a solução continue operando de forma confiável sob
instabilidade, alta demanda ou falhas parciais.

"Confiável" precisa de definição, porque o termo esconde uma escolha: quando o
banco de dados não responde, o que o sistema faz?

## A decisão de fundo: confiável significa correto, não disponível

RNF-04 diz que qualquer inconsistência impacta diretamente o cliente e a
operação. Entre recusar uma escrita e aceitá-la sem saber se ela foi registrada,
**recuso**.

Isso coloca o caminho de escrita no lado CP do espectro, e é uma escolha
consciente, não um efeito colateral.

A assimetria importa: uma escrita recusada com `503` é um problema que o
chamador percebe e trata — ele repete, e [ADR-0003](0003-idempotencia-como-contrato.md)
garante que repetir é seguro. Um registro perdido ou duplicado é um problema que
ninguém percebe até a conciliação.

## Alternativas consideradas para a indisponibilidade do banco

1. **Enfileirar localmente** as escritas e drenar quando o banco voltar.
2. **Falhar rápido** com `503` e deixar o retry para o chamador.
3. **Aceitar com `202`** e processar depois.

## Decisão

Alternativa 2.

## Justificativa

**Alternativa 1 é perda de dados com aparência de resiliência.** A fila vive num
processo efêmero: se a instância morrer — e sob instabilidade ela morre —, as
movimentações aceitas e não persistidas desaparecem. E o chamador já recebeu
confirmação. É o pior desenho possível para dados financeiros, porque falha
exatamente quando parece estar funcionando.

**Alternativa 3 é a alternativa 1 de ADR-0006** e está rejeitada pelos mesmos
motivos: muda o contrato e cria janela de inconsistência.

**Alternativa 2 é honesta.** O chamador sabe que não foi registrado, e tem a
chave de idempotência para repetir sem risco. A responsabilidade fica com quem
tem a informação para decidir o que fazer.

## Mecanismos

| Mecanismo | Decisão |
|-----------|---------|
| Timeout | explícito em toda operação de I/O. Timeout infinito transforma lentidão de dependência em esgotamento de pool, que é como falha parcial se torna queda total |
| Retry | com *backoff* exponencial e *jitter*, **apenas** em erro transitório e operação idempotente |
| Circuit breaker | nas dependências, para parar de bater em porta fechada e dar tempo de recuperação |
| Bulkhead | pool de leitura separado do de escrita; consulta lenta não esgota a capacidade de registrar |
| Graceful shutdown | drena requisições em voo antes de encerrar |

O *jitter* no retry não é refinamento: sem ele, N clientes que falharam ao mesmo
tempo repetem ao mesmo tempo, e o retry sincronizado reproduz o pico que causou
a falha. É o *thundering herd*.

## Health checks: liveness e readiness são perguntas diferentes

| Endpoint | Pergunta | Toca dependências? |
|----------|----------|--------------------|
| `/health/live` | o processo está vivo? | **não** |
| `/health/ready` | as dependências respondem? | sim |

Confundir as duas é um erro operacional com consequência grave: se *liveness*
verificar o banco, o orquestrador mata instâncias que estão apenas esperando o
banco voltar. O resultado é um ciclo de reinício durante uma indisponibilidade
de dependência — **falha parcial convertida em queda total**, que é precisamente
o oposto de RNF-03.

`/health/live` já existe no scaffold e não toca em nada de propósito.

## Comportamento sob cada cenário de falha

| Cenário | Escrita | Leitura |
|---------|---------|---------|
| Banco primário indisponível | `503` rápido, nada registrado | servida por réplica, com indicação de atualidade na resposta |
| Banco lento | timeout e `503` | timeout e `503` |
| Worker parado | normal (não depende do worker) | cai no cálculo direto; mais lenta, igualmente correta |
| Snapshot divergente | normal | reconciliação corrige; métrica alerta |
| Pico acima da capacidade | `429` com `Retry-After` | `429` com `Retry-After` |

A linha do worker parado é consequência de [ADR-0004](0004-snapshot-de-saldo.md):
o snapshot ser cache descartável significa que a falha do componente que o
produz degrada latência, nunca correção.

`429` com `Retry-After` em vez de simplesmente lentificar é deliberado: diz ao
chamador o que fazer, em vez de deixá-lo adivinhar entre insistir e desistir.

## Trade-offs aceitos

| Custo | Avaliação |
|-------|-----------|
| Indisponibilidade de escrita durante falha do banco | aceito por RNF-04; mitigado por idempotência, que torna o retry trivial |
| Chamador precisa implementar retry | é o lado que tem a informação para decidir; documentado no contrato |
| Leitura pode servir dado levemente atrasado | sinalizado explicitamente na resposta; nunca silencioso |

A última linha é a regra geral deste ADR: **degradação é sempre declarada**. O
sistema pode responder mais devagar ou com dado menos recente, mas nunca sem
dizer que foi isso que aconteceu.

## Gatilho de revisão

- Se a indisponibilidade de escrita se mostrar inaceitável para o negócio, a
  conversa é sobre a alternativa 1 de [ADR-0006](0006-sem-broker-no-caminho-de-escrita.md)
  — com broker durável, não fila em memória de processo — e sobre o contrato
  `202` que isso implica.
- Se a origem da instabilidade for o próprio banco e não a aplicação,
  replicação síncrona com *failover* automático resolve mais que qualquer
  política de retry.
