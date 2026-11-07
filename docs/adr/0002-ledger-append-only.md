# ADR-0002 — Ledger append-only: o saldo é derivado, nunca estado mutável

- **Status:** aceito
- **Data:** 2026-11-07
- **Requisitos relacionados:** RF-01, RF-02, RNF-04 (consistência), RNF-07 (concorrência)

Esta é a decisão central da solução. As outras se organizam em volta dela.

## Problema

Onde vive o saldo de uma conta?

A pergunta parece ingênua e não é. Dela decorre o comportamento do sistema sob
concorrência, que é o requisito que o enunciado destaca, e provavelmente a
causa-raiz do problema relatado.

## Hipótese sobre o legado

O enunciado descreve três sintomas: lentidão, instabilidade em horário de pico e
dificuldade de manutenção. Esse conjunto, num sistema de movimentação
financeira, quase sempre vem de um **saldo armazenado como coluna mutável**.

O mecanismo é este:

- **Sem proteção**, duas transações concorrentes leem o mesmo saldo, cada uma
  soma a sua movimentação e a segunda sobrescreve a primeira. É *lost update*:
  dinheiro desaparecendo. Silenciosamente, porque nenhum erro é lançado.
- **Com lock pessimista** (`SELECT ... FOR UPDATE`), a corretude está salva, mas
  a coluna se torna um ponto de serialização por conta. No pico, as conexões
  empilham esperando o lock, o pool esgota, os timeouts cascateiam para
  chamadas que nem tocavam aquela conta.

É exatamente "lentidão e instabilidade em horários de pico". A hipótese não é
verificável sem acesso ao legado — está registrada como hipótese, não como
fato —, mas ela é o que orienta o desenho.

## Alternativas consideradas

1. **Coluna `Balance` em `Account`**, atualizada na mesma transação da
   movimentação.
2. **Coluna `Balance` com lock pessimista** por conta.
3. **Ledger imutável**: movimentações são fatos append-only e o saldo é uma
   agregação sobre eles.

## Decisão

Alternativa 3. **O saldo não existe como campo em `Account`.**

Essa ausência é a decisão. Movimentação é fato imutável: não há *setter*, não há
`UPDATE`, não há `DELETE` no caminho de escrita do ledger.

## Justificativa

**Alternativa 1 é a arquitetura que produz o problema do enunciado.** Não é uma
alternativa fraca, é a provável causa.

**Alternativa 2 troca corretude por disponibilidade,** e o enunciado pede as
duas.

**Alternativa 3 elimina o recurso compartilhado mutável.** Escrever passa a ser
`INSERT`, e dois `INSERT` não disputam nada. O ponto não é que a concorrência
fique bem gerenciada: é que ela deixa de existir no modelo. Não há estado a
proteger porque não há estado.

Isso é o que diferencia a decisão de uma otimização. Não estou resolvendo o
problema de concorrência melhor — estou escolhendo um modelo onde ele não
aparece.

### Benefícios que o domínio exige e que vêm de graça

**A trilha de auditoria é o próprio ledger.** Não existe a pergunta "quem mudou
esse saldo, quando e por quê" — o saldo nunca mudou, ele só se acumulou. Num
sistema financeiro, isso não é conveniência: é requisito regulatório que, na
alternativa 1, exigiria uma tabela de auditoria paralela que pode divergir.

**Qualquer estado passado é reconstruível.** O saldo de qualquer instante sai do
mesmo log, sem precisar de histórico separado — o que atende RF-02
estruturalmente, e não com uma funcionalidade adicional.

### Bitemporalidade: o fato tem dois instantes

A movimentação guarda `occurredAt` (quando aconteceu no mundo) e `postedAt`
(quando o sistema registrou, definido pelo servidor e nunca pelo cliente).

São perguntas diferentes e ambas legítimas: "qual era meu saldo na sexta" é
tempo de negócio; "o que o sistema sabia na sexta" é tempo de sistema, a
pergunta de auditoria e de investigação de incidente. Guardar só um fecha metade
das perguntas para sempre, e não há como recuperar depois.

A consequência operacional — movimentação retroativa invalidando snapshots — está
em [ADR-0004](0004-snapshot-de-saldo.md). O detalhamento da premissa está em
[PR-04](../premissas.md#pr-04).

### O que esta decisão não é

**Não é event sourcing como framework.** Não há *event store*, versionamento de
eventos nem *upcasting*. As movimentações já **são** eventos no sentido
contábil, e o esquema é estável por natureza: débito e crédito não mudam de
forma com o tempo.

Adotar a maquinaria completa de event sourcing aqui seria a complexidade sem
justificativa que o próprio enunciado pede para evitar. O que eu tomo do
padrão é a ideia — fato imutável como fonte da verdade, estado como projeção —
e não a infraestrutura.

## Trade-offs aceitos

| Custo | Mitigação |
|-------|-----------|
| Consulta de saldo passa de O(1) para O(n) em movimentações | [ADR-0004](0004-snapshot-de-saldo.md): snapshot de período fechado mais delta |
| Armazenamento cresce monotonicamente | é obrigação de retenção regulatória, não desperdício opcional |
| Correção de erro operacional exige movimentação compensatória | mais cerimônia; auditoria intacta. Ver [PR-06](../premissas.md#pr-06) |
| Nenhum invariante de saldo é imposto na escrita | premissa consciente: ver [PR-03](../premissas.md#pr-03) |

O primeiro custo é real e é o motivo de ADR-0004 existir. Os outros três são
custos que eu escolheria pagar de novo.

## Consequências

- `Movement` é imutável por construção, e isso é verificado por teste.
- O valor monetário usa `decimal`, nunca `double` ou `float` — há teste de
  arquitetura que proíbe ponto flutuante binário no domínio e nos casos de uso.
  São base 2, não representam 0,1 exatamente, e num razão erro de arredondamento
  acumulado é dinheiro que não fecha.
- O valor é sempre positivo; o sinal é semântica do tipo da movimentação. Isso
  impede a classe de bug em que um crédito negativo se torna um débito
  silencioso.
- A escrita escala horizontalmente sem coordenação: são `INSERT` independentes.

## Gatilho de revisão

Reconsideraria **se** o ledger passar a precisar impor um invariante de saldo —
recusar débito a descoberto ([PR-03](../premissas.md#pr-03)). Aí volta a existir
uma fronteira de consistência por conta, e com ela contenção: concorrência
otimística com versão, ou reserva prévia.

Mesmo nesse cenário eu **não** voltaria à coluna mutável. Manteria o ledger
append-only e adicionaria o controle de versão por conta — o custo seria
serializar escritas da mesma conta, não reintroduzir *lost update*.
