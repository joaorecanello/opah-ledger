# ADR-0007 — Ordenação do ledger: global agora, sequência por conta como opção

- **Status:** aceito
- **Data:** 2026-11-07
- **Requisitos relacionados:** RF-02, RNF-02 (alta demanda), RNF-07 (concorrência)

## Problema

Movimentações precisam de ordem — para calcular saldo, para paginar extrato e
para auditoria. Que tipo de ordem?

A pergunta esconde uma segunda: a ordem precisa ser **contígua por conta**, sem
lacunas? É aí que a decisão fica interessante, porque contiguidade custa
contenção.

## Alternativas consideradas

1. **Identificador ordenável por tempo** (ULID) mais `OccurredAt`, sem
   sequência por conta.
2. **Contador monotônico por conta**, com *unique constraint* em
   `(AccountId, Sequence)` e concorrência otimística com retry no conflito.

## Decisão

Alternativa 1 agora. Alternativa 2 documentada e com o esquema preparado para
recebê-la.

## Justificativa

### O que a alternativa 2 oferece

Sequência contígua por conta é uma propriedade de auditoria forte: ela **prova
que nada foi suprimido**. Se o extrato vai de 1 a 100 sem lacunas, ninguém
apagou o lançamento 57. Reguladores às vezes exigem exatamente isso, e é um
requisito legítimo.

### O que ela custa

Contiguidade exige que alguém decida qual é o próximo número, e isso recria o
ponto de serialização que [ADR-0002](0002-ledger-append-only.md) eliminou:

- duas escritas concorrentes na mesma conta competem pelo próximo número;
- uma viola a constraint e precisa refazer a operação;
- sob pico na mesma conta, o retry se repete e a latência cresce.

A contenção é por conta, não global, o que é bem melhor que uma coluna de saldo
global. Mas ela existe, e volta a introduzir no caminho de escrita a classe de
problema que o desenho inteiro foi feito para evitar.

### Por que a alternativa 1, então

Duas razões, e as duas são sobre requisitos que **não** estão no enunciado:

1. **Não há exigência declarada de contiguidade.** O enunciado não menciona
   auditoria regulatória de numeração sequencial. Pagar contenção por um
   requisito que ninguém pediu é o tipo de decisão que parece prudência e é
   desperdício.
2. **Não há invariante por conta.** Por [PR-03](../premissas.md#pr-03), o ledger
   não valida saldo. Sem invariante, não há necessidade de serializar escritas
   da mesma conta — e portanto não há necessidade técnica de sequência por
   conta.

ULID dá ordenação total determinística, é gerado na aplicação sem ida ao banco,
ordena por tempo de criação e não revela volume (ao contrário de um `bigint`
sequencial exposto, que informa a terceiros quantas movimentações o banco
processa).

Com `OccurredAt` como critério de negócio e o ULID como desempate estável, toda
consulta tem ordem determinística, e escritas concorrentes não disputam nada.

### A honestidade desta decisão

Esta é a decisão deste conjunto em que eu tenho **menos convicção**, e por isso
ela é um ADR separado em vez de uma linha dentro de ADR-0002.

A razão é que ela depende inteiramente de uma informação que eu não tenho: se
auditoria exige numeração contígua. Num banco real, essa é uma pergunta de cinco
minutos para a área de *compliance*, e a resposta muda a decisão.

**É a primeira pergunta que eu levaria ao time.**

## Trade-offs aceitos

| Perco | Ganho |
|-------|-------|
| Prova estrutural de que nada foi suprimido | escrita sem contenção por conta |
| Numeração legível por humano no extrato | identificador que não vaza volume de operação |

A primeira perda é mitigada parcialmente: o ledger é append-only e há trilha de
auditoria de acesso, então suprimir uma linha exigiria acesso direto ao banco
fora da aplicação — o que é detectável por outros meios, embora não *provável*
pela própria numeração.

## Consequências

- O esquema já prevê a coluna de versão por conta, sem uso no momento. Habilitar
  a alternativa 2 é mudança localizada — um caso de uso e uma constraint —, não
  redesenho.
- A paginação do extrato usa *keyset* (cursor sobre `(OccurredAt, Id)`) e não
  *offset*. `OFFSET` degrada linearmente com a profundidade e, sob escrita
  concorrente, pode pular ou repetir registros entre páginas. Com `keyset` isso
  não acontece, e o ledger append-only o favorece: o cursor nunca aponta para
  algo que mudou.

## Gatilho de revisão

Mudaria para a alternativa 2 se **qualquer** um destes aparecer:

1. *Compliance* exigir numeração contígua por conta;
2. [PR-03](../premissas.md#pr-03) cair e o ledger passar a validar saldo — nesse
   caso já haverá fronteira de consistência por conta, e a sequência passa a
   custar quase nada, porque a serialização já existe;
3. Conciliação com sistema externo exigir correspondência por número de
   lançamento.

No cenário 2 as duas decisões se movem juntas, o que é um bom indício de que
estão corretamente separadas: elas têm a mesma causa raiz.
