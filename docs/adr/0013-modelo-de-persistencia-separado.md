# ADR-0013 — Modelo de persistência separado do modelo de domínio

- **Status:** aceito
- **Data:** 2026-11-07
- **Requisitos relacionados:** RNF-04 (consistência), RNF-06 (manutenibilidade)

## Problema

O domínio é estritamente imutável: construtores privados, nenhuma propriedade
com `set`, objetos de valor (`Money`, `Currency`, `IdempotencyKey`) e
identificadores tipados. Há teste de reflexão que falha o build se isso mudar.

Um ORM precisa de tipos que ele consiga materializar. Como conciliar?

## Alternativas consideradas

1. **Mapear o domínio direto**, com o EF Core materializando as entidades por
   construtor e os objetos de valor como *complex types* ou conversores.
2. **Ceder no domínio**: construtor sem argumentos, propriedades com `set`
   privado, objetos de valor achatados em primitivos.
3. **Modelo de persistência separado**, com tipos próprios e mapeamento
   explícito.

## Decisão

Alternativa 3. Quatro tipos de persistência — `AccountRecord`,
`MovementRecord`, `IdempotencyRecord`, `BalanceSnapshotRecord` — e um
`PersistenceMapper` explícito.

## Justificativa

**Alternativa 2 está fora de questão.** As propriedades que o domínio ganharia
são exatamente as que ele existe para não ter: se `Movement` tiver
`set`, o ledger deixa de ser append-only por construção e passa a ser
append-only por convenção. [ADR-0002](0002-ledger-append-only.md) perde a força.

**Alternativa 1 é possível e é a que eu teria tentado primeiro num domínio
menos restritivo.** O EF Core consegue vincular construtores e converter objetos
de valor. O que pesou contra:

- O mapeamento de agregado profundamente imutável com objetos de valor
  aninhados — `Money` contendo `Currency`, que por sua vez é um objeto de valor
  com construtor privado — é a parte mais frágil e menos documentada do EF. O
  modo de falha é em tempo de execução, na materialização, não no build.
- Mesmo funcionando, ele acopla a forma do domínio à forma da tabela. Renomear
  uma propriedade de domínio passa a ser migração de esquema, e vice-versa.

**O argumento decisivo é outro, e é sobre o que este sistema faz.** O caminho de
escrita é `INSERT`: o ledger é append-only, nada é alterado. O caminho de
leitura é agregação — `SUM` sobre uma janela. Em nenhum dos dois o rastreamento
de mudanças do ORM serve para algo: ele existe para detectar alterações em
entidades carregadas, e aqui não há alteração a detectar.

Então o que o EF Core traz de valor neste projeto não é o mapeamento rico de
agregados. São **migrations versionadas** e **esquema declarado em código
tipado**. Para isso, tipos planos de persistência bastam — e são a ferramenta
certa.

É por isso que o contexto roda com `QueryTrackingBehavior.NoTracking` por
padrão: não é otimização, é declaração de que o recurso não é usado.

## O encaixe que já existia

`Movement.Rehydrate` e `BalanceSnapshot.Rehydrate` foram escritos antes desta
decisão, e por uma razão independente: **um fato que já está no razão é
histórico, e histórico não se revalida.** Se a regra de criação mudar amanhã, os
lançamentos de ontem continuam sendo o que foram.

Esses métodos são exatamente a costura que o mapeamento precisa. O mapeador usa
`Rehydrate`, nunca a fábrica de criação.

## Trade-offs aceitos

| Custo | Avaliação |
|-------|-----------|
| Uma camada de mapeamento a manter | o preço de o domínio não ceder nada |
| Lista de campos duplicada entre domínio e persistência | explícita e verificada por teste de ida e volta |
| Mais código que mapear direto | código explícito e depurável, em lugar de convenção que surpreende em runtime |

A segunda linha é o custo real: adicionar um campo exige tocar dois lugares.
Mitigado pelos testes de ida e volta, que comparam campo por campo — se alguém
adicionar um campo ao domínio e esquecer o mapeamento, o teste falha.

## Consequências

- `Ledger.Domain` continua com dependência zero, verificado por teste de
  arquitetura.
- O esquema pode evoluir sem arrastar o domínio: nomes de coluna em
  `snake_case`, enums como texto para serem legíveis em consulta manual durante
  incidente, valores em `numeric(19,4)`.
- Há testes de integração que verificam a ida e volta completa — domínio,
  tabela, domínio — campo por campo, incluindo precisão decimal e instante com
  fuso.
- Os tipos de persistência são públicos. Isso é consequência de os testes de
  integração os usarem direto para verificar o que o esquema aceita e recusa,
  inclusive contornando o domínio de propósito.

## Gatilho de revisão

Se o domínio ganhar agregados com coleções e alterações de estado — o que
aconteceria, por exemplo, se o ledger passasse a manter reservas de saldo
([PR-03](../premissas.md#pr-03)) —, o rastreamento de mudanças passa a ter
valor, e a alternativa 1 volta à mesa. O mapeamento explícito é o que torna
essa transição possível sem reescrever o domínio.
