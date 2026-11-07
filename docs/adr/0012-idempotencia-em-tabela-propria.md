# ADR-0012 — Chave de idempotência em tabela própria, não em coluna de `movements`

- **Status:** aceito
- **Data:** 2026-11-07
- **Revisa:** [ADR-0003](0003-idempotencia-como-contrato.md), que não dizia onde o constraint vive
- **Requisitos relacionados:** RNF-02 (alta demanda), RNF-04 (consistência)

## Problema

[ADR-0003](0003-idempotencia-como-contrato.md) estabeleceu que a garantia de
não-duplicidade vive num *unique constraint*, porque é o único lugar que não
mente sob concorrência. A leitura natural é: uma coluna
`movements.idempotency_key` com índice único.

Ao implementar a persistência, isso colidiu com
[ADR-0005](0005-persistencia-postgresql.md), que prevê **particionar `movements`
por mês** para a retenção de 5 anos assumida em
[PR-02](../premissas.md#pr-02).

**O PostgreSQL exige que o unique constraint de uma tabela particionada inclua
todas as colunas da chave de particionamento.**

## Por que isso quebra a garantia

Com `movements` particionada por data, o unique possível seria
`(idempotency_key, occurred_at)`. Esse constraint permite:

| Tentativa | `idempotency_key` | `occurred_at` | Aceita? |
|-----------|-------------------|---------------|---------|
| 1 | `pix-9f3a` | 2026-11-05 | sim |
| 2 | `pix-9f3a` | 2026-11-06 | **sim** |

Ou seja: ele permite exatamente a duplicata que a chave existe para impedir. Um
cliente que repita a requisição com a data corrigida — ou um retry que
atravesse a meia-noite com `occurredAt` gerado no cliente — registra o
lançamento duas vezes.

Pior: o constraint existiria, o esquema pareceria correto numa revisão rápida, e
a falha só apareceria na conciliação.

## Alternativas consideradas

1. **Não particionar `movements`.** Mantém o unique global simples.
2. **Unique em `(idempotency_key, occurred_at)`.** Compatível com
   particionamento.
3. **Tabela `idempotency_records` própria, não particionada**, com a chave como
   chave primária.

## Decisão

Alternativa 3.

A movimentação **mantém** a coluna `idempotency_key`, com índice **não único**,
para rastreabilidade e para reconstruir a movimentação de domínio — que carrega
a chave. A unicidade vive em `idempotency_records`.

## Justificativa

**Alternativa 1 troca uma garantia por outra.** O particionamento não é enfeite:
é o que permite que a consulta temporal toque uma partição em lugar de cinco
anos de histórico, e o que torna o descarte de dados expirados uma operação de
metadados. Abrir mão dele para simplificar o constraint resolve o problema
errado.

**Alternativa 2 é a pior das três**, porque parece resolver. Já está explicado
acima: o constraint existe e não garante.

**Alternativa 3 separa as duas coisas que têm ciclos de vida diferentes.** A
movimentação é permanente, cresce para sempre e se beneficia de particionamento
por data. O registro de idempotência é temporário — [ADR-0003](0003-idempotencia-como-contrato.md)
já previa TTL de 7 dias — e precisa de unicidade global sobre uma única coluna.
São requisitos opostos na mesma tabela; separá-las deixa cada uma com o esquema
que serve a ela.

E há um ganho que não era o objetivo: a tabela de idempotência guarda a
**impressão do conteúdo** da requisição original. É o que permite distinguir
repetição legítima — mesma chave, mesmo conteúdo, devolve a resposta original —
de erro do chamador: conteúdo diferente, devolve `409`. Numa coluna de
`movements` isso exigiria comparar campo por campo contra a movimentação já
gravada; aqui é a comparação de um hash.

## Como a garantia se mantém

As duas inserções acontecem na **mesma transação**. Um único `SaveChanges` é uma
transação: ou as duas linhas entram, ou nenhuma entra.

Se fossem transações separadas, haveria uma janela em que a movimentação existe
sem a chave — e o retry nessa janela duplicaria. Há teste de integração que
verifica precisamente isso: depois de uma segunda tentativa recusada, a conta
tem **uma** movimentação, não duas.

## Trade-offs aceitos

| Custo | Avaliação |
|-------|-----------|
| Duas tabelas em lugar de uma | cada uma com o esquema que serve ao seu ciclo de vida |
| Uma inserção a mais por movimentação | mesma transação, mesmo `INSERT` em lote; irrelevante |
| A chave aparece em dois lugares | a de `movements` é cópia para rastreabilidade, declarada como tal |
| Job de limpeza das chaves expiradas | já previsto no ADR-0003 |

## Consequências

- `movements.idempotency_key` tem índice **não único**, e há teste que verifica
  que ele não é único — para que ninguém o "conserte" por engano e crie a
  ilusão de garantia da alternativa 2.
- O particionamento por mês segue possível, sem conflito.
- A impressão do conteúdo cobre apenas os campos que definem o fato: conta,
  tipo, valor, moeda, instante de negócio e referência externa.
  Deliberadamente **não** cobre `postedAt` nem o identificador de correlação,
  que mudam a cada tentativa por natureza — incluí-los faria toda repetição
  parecer conteúdo divergente, e o mecanismo recusaria com `409` exatamente o
  retry que deveria aceitar.

## Gatilho de revisão

Se o particionamento for abandonado — por exemplo, se o volume real
([PR-02](../premissas.md#pr-02)) se mostrar muito menor que o assumido —, a
alternativa 1 volta a ser viável e mais simples. A migração seria mover a
unicidade para `movements` e descartar a tabela; nada no domínio muda, porque o
domínio não sabe onde o constraint vive.
