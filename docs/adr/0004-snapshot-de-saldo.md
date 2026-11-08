# ADR-0004 — Saldo consolidado por snapshot de período fechado mais delta

- **Status:** aceito
- **Data:** 2026-11-07
- **Requisitos relacionados:** RF-02, RNF-02 (alta demanda), RNF-04 (consistência)

## Problema

[ADR-0002](0002-ledger-append-only.md) tornou o saldo uma agregação sobre o
histórico. Com a retenção de 5 anos assumida em
[PR-02](../premissas.md#pr-02), somar o histórico inteiro a cada consulta não
atende RNF-02.

Ou seja: este ADR existe para pagar a dívida que o ADR-0002 contraiu de
propósito.

## Alternativas consideradas

1. **Agregação completa a cada consulta.**
2. **Saldo materializado** atualizado por *trigger* ou projeção a cada
   movimentação.
3. **Snapshot do saldo de fechamento por período**, somado apenas às
   movimentações posteriores ao snapshot.

## Decisão

Alternativa 1 primeiro, alternativa 3 depois — e a alternativa 3 com uma
restrição que faz toda a diferença: **snapshot somente de períodos encerrados.**

A ordem é parte da decisão, não cronograma. A agregação completa é o oráculo
contra o qual a otimização é testada.

## Justificativa

**Alternativa 1 é correta e escala mal.** É o que entra primeiro, porque é
obviamente correta e porque me dá a referência de verdade: o teste da otimização
é "o snapshot devolve exatamente o mesmo número que a soma direta".

Otimizar sem ter o resultado correto medido antes é como refatorar sem teste.

> **Estado da implementação.** A alternativa 1 está pronta e em uso: a consulta
> faz uma única agregação no banco, filtrada pelo índice de conta e instante, e
> devolve `source: "DirectSum"`.
>
> Ela já vem com o oráculo montado — um teste gera 120 movimentações em datas
> aleatórias, soma pelo banco e pelo `BalanceCalculator` do domínio, e exige o
> mesmo número. Quando o snapshot entrar, é esse mesmo teste, com o terceiro
> caminho somado à comparação.

**Alternativa 2 reintroduz o pecado de ADR-0002 pela porta dos fundos.** Um saldo
materializado mutável pode divergir da verdade sem ninguém notar. A diferença em
relação à coluna de saldo rejeitada em ADR-0002 é apenas quem escreve nela — e o
modo de falha é o mesmo.

**Alternativa 3 dá complexidade O(movimentações do período corrente)** — na
prática, dezenas de linhas: um *seek* no snapshot do período anterior mais um
*range scan* curto.

### A restrição que torna isso seguro

O snapshot cobre **apenas períodos já encerrados**. Como o passado é imutável
([ADR-0002](0002-ledger-append-only.md)), o snapshot de um dia fechado **nunca
fica errado**. Não existe invalidação por mudança de estado, porque não existe
mudança de estado.

É a propriedade que separa isto de um cache comum. Cache normal tem a pergunta
"está atualizado?"; aqui a pergunta não se aplica ao passado fechado.

### O snapshot é cache descartável, nunca fonte da verdade

Esta frase precisa estar em letras garrafais em qualquer lugar onde alguém vá
mexer nisso:

> **A tabela de snapshots pode ser truncada por inteiro a qualquer momento, e o
> worker a reconstrói a partir do ledger.**

É isso que torna a otimização aceitável em dados financeiros. Se eu tivesse
dúvida sobre um número do snapshot, eu o jogo fora e recalculo. Com a
alternativa 2, se o saldo materializado divergir, não há de onde recuperar o
valor certo sem... recalcular do ledger — ou seja, a alternativa 2 só funciona
se o ledger existir, e nesse caso a alternativa 3 é melhor em todos os
aspectos.

## O caso difícil: movimentação retroativa

Uma movimentação cujo `occurredAt` é anterior a snapshots já calculados
([PR-04](../premissas.md#pr-04)) torna todos os snapshots posteriores obsoletos.

Esta é a parte mais sutil do sistema e a que eu mais testaria antes de produção,
porque um erro aqui produz saldo histórico errado **sem erro visível**.

Tratamento:

1. O registro retroativo marca como *stale* os snapshots da conta a partir
   daquela data.
2. Consulta que cai numa janela *stale* usa o caminho da alternativa 1 —
   correto e mais lento.
3. O worker recalcula os snapshots afetados em segundo plano.

O princípio: **correto sempre, rápido quase sempre.** Nunca o contrário. A
degradação é de latência, nunca de valor.

## Verificação contínua

Otimização sem verificação automática, em sistema financeiro, é passivo e não
ativo. Portanto:

- **Job de reconciliação** recalcula do zero e compara com o snapshot.
- **Métrica `ledger_snapshot_divergence_total`**, com alerta em qualquer valor
  diferente de zero. É a métrica mais importante do sistema: é o detector de o
  dinheiro ter divergido.
- **Teste de propriedade** gerando 10.000 movimentações em datas aleatórias e
  exigindo igualdade entre snapshot e cálculo direto.

Divergência não precisa de plano de contingência elaborado: trunca e
reconstrói.

## Trade-offs aceitos

| Custo | Avaliação |
|-------|-----------|
| Complexidade adicional (worker, invalidação, reconciliação) | é a razão de isto não ser prioridade máxima: o sistema está correto sem o snapshot |
| Armazenamento de ~365 linhas por conta por ano | irrelevante frente ao volume do ledger |
| Mais uma coisa que pode estar errada | mitigado por reconciliação automática e pela possibilidade de descarte total |

## Consequências

- A resposta da consulta de saldo informa **como** o número foi calculado
  (`computedFrom: "snapshot:2026-11-04"` ou cálculo direto) e se a janela está
  *stale*. Em finanças, rastreabilidade do número vale mais que elegância do
  payload: quando alguém contestar um saldo, a primeira pergunta é de onde ele
  veio.
- A série histórica diária de saldo fica praticamente de graça — é a própria
  tabela de snapshots ([PR-12](../premissas.md#pr-12)).
- O passado fechado é infinitamente cacheável em camadas superiores, porque é
  imutável. O saldo do período corrente não é.

## Gatilho de revisão

**Para baixo:** se a escala real for muito menor que a assumida em
[PR-02](../premissas.md#pr-02), o snapshot é complexidade sem retorno e eu o
removeria. A agregação direta continuaria correta — este ADR seria revertido sem
dano, o que é consequência de o snapshot nunca ter sido fonte da verdade.

**Para cima:** se o volume do período corrente crescer ao ponto de o delta
diário ficar caro, a granularidade do snapshot diminui — horário em vez de
diário. É mudança de parâmetro, não de desenho.
