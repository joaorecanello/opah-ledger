# ADR-0008 — Resiliência e degradação declarada

- **Status:** aceito, implementado
- **Data:** 2026-11-07 (implementação: 2026-11-08)
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

## O que foi implementado, e o que a medição mudou

Antes de escrever uma linha, derrubei o banco do `compose` e medi o que a API
fazia. O resultado justificou o ADR inteiro:

| Banco parado | Antes | Depois |
|---|---|---|
| `/health/live` | `200` | `200` |
| `/health/ready` | `404` — não existia | `503` |
| escrita | **`500`** | `503` + `Retry-After` + `application/problem+json` |
| leitura | **`500`** | `503` + `Retry-After` + `application/problem+json` |

O `500` é o problema, não o sintoma: ele diz "defeito do servidor", e o chamador
conclui que repetir não ajuda quando repetir é exatamente a resposta certa. O
painel acusa bug de código numa falha de infraestrutura. E o texto do erro do
driver, com host e porta do banco, tem uma chance de vazar na resposta.

Esta seção registra as quatro coisas que a implementação mostrou e que o texto
original não previa.

### 1. Retry sem orçamento de tempo só adia o erro

A primeira versão usou o retry embutido do EF Core: duas repetições. Medi de
novo, e a requisição passou a levar **7,1 s**. Cada tentativa gastava cerca de
dois segundos para falhar — o nome do banco deixava de resolver no DNS do Docker
— e três tentativas multiplicavam isso.

Retry serve para soluço rápido: uma conexão que o pool descartou, uma rede que
piscou. Para queda de verdade, só mantém conexões e threads presas por mais
tempo. O que o retry do EF limita é o *número* de tentativas, não o *tempo*.

A correção é `LedgerExecutionStrategy`: uma estratégia própria, sobre a API
pública do EF, com a regra **não começa uma tentativa que já nasce estourada**.
Falha rápida cabe no orçamento (2 s) e é repetida; falha lenta o consome sozinha
e sobe na hora. Resultado medido: de **7,1 s para ~2,1 s** por requisição, e dois
testes fixam os dois lados — falha rápida usa as três tentativas, falha lenta
usa uma.

Efeito colateral bom: a estratégia usa `TransientFailure` para decidir o que
repetir, a mesma definição que o circuito e o mapeamento para `503` usam. Eram
três mecanismos e passou a ser uma só noção de "transitório".

### 2. Retry e transação explícita: a armadilha do sucesso falso

Com retry ligado, o EF recusa uma transação iniciada à mão fora de uma
estratégia de execução — e a escrita retroativa precisa de uma, porque a
invalidação de fechamentos é um `UPDATE` em massa que roda fora do `SaveChanges`.

A forma ingênua de consertar é perigosa. Se a primeira tentativa falhar
*depois* de o `SaveChanges` aceitar as entidades, elas ficam `Unchanged`; a
repetição não grava nada, e o método devolveria `Persisted` sem ter registrado
coisa alguma. **Perda de dinheiro com resposta de sucesso.**

A saída é recriar o estado a cada tentativa: o delegado limpa o rastreador e
adiciona as entidades de novo. Há teste para o caminho comum e para o
retroativo, nos dois momentos possíveis da falha.

### 3. Resposta perdida depois do commit

É o cenário que justifica ter retry e idempotência juntos. Uma queda entre o
commit e a resposta deixa o cliente sem saber se o lançamento existe. Repetir sem
proteção duplicaria dinheiro; não repetir perderia o lançamento se a falha foi
antes.

O sistema não tem como saber *quando* a falha ocorreu, e não precisa. Os dois
momentos foram injetados dentro da transação do EF, nos dois caminhos de escrita:

| Falha | O que a repetição encontra | Resultado |
|---|---|---|
| **antes** do commit | nada gravado | grava, `Registered` |
| **depois** do commit | a própria chave | viola o unique, `Replayed` |

Nos quatro cenários: **um lançamento, nunca zero e nunca dois.** O número de
commits tentados distingue os dois destinos (2 antes, 1 depois), e os fechamentos
são invalidados nos dois casos.

### 4. O circuito conta a história inteira

Medido com o banco parado e 10 leituras em sequência: as oito primeiras levam
~2 s cada; o circuito abre; a nona e a décima levam **5 ms**. O log registra a
sequência completa — `ABERTO`, recusas sem consultar o banco, `SEMI-ABERTO`,
`ABERTO` de novo (a sonda falhou), `SEMI-ABERTO`, `FECHADO` quando o banco
voltou. A mesma instância se recuperou sozinha, sem reinício.

## Mecanismos

| Mecanismo | Decisão |
|-----------|---------|
| Timeout | conexão (3 s), comando (10 s) e requisição inteira (15 s, `503` ao estourar). Timeout infinito transforma lentidão de dependência em esgotamento de pool, que é como falha parcial se torna queda total |
| Retry | até 2 repetições, com *backoff* e *jitter*, **apenas** em erro transitório, dentro de um **orçamento de 2 s**. Seguro porque a escrita é idempotente |
| Circuit breaker | Polly, por instância: abre com 50% de falha em 8+ requisições na janela de 30 s, por 10 s. Aplicado por filtro de endpoint, então vale para rota nova sem ninguém lembrar |
| Bulkhead | limites de concorrência **separados** para escrita (200) e leitura (400); o excedente recebe `429` na hora, sem fila |
| Graceful shutdown | `ShutdownTimeout` de 30 s na aplicação e `stop_grace_period` de 40 s no Docker. Verificado: `docker stop` sai com código `0`, não `137` |

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

`/health/live` não toca em nada de propósito. `/health/ready` sonda o banco com
uma consulta real e tem limite próprio de 2 s.

Os dois ficam **fora** do circuito e dos limites de capacidade, e há teste para
isso. O orquestrador precisa conseguir perguntar mesmo — e principalmente —
quando a instância está saturada ou o banco caiu. Se a pergunta fosse recusada
com `429`, uma instância sobrecarregada pareceria morta e seria reiniciada,
piorando a sobrecarga que a deixou assim.

A sondagem **não usa o EF Core**, e isso é proposital: o contexto tem retry, e
uma sondagem por ele esperaria as tentativas antes de admitir que o banco está
fora. Readiness precisa responder "não" tão rápido quanto "sim".

## Comportamento sob cada cenário de falha

| Cenário | Escrita | Leitura |
|---------|---------|---------|
| Banco indisponível | `503` + `Retry-After`, nada registrado | `503` + `Retry-After` |
| Banco lento | timeout e `503` | timeout e `503` |
| Worker parado | normal (não depende do worker) | recua para um fechamento mais antigo ou cai no cálculo direto; mais lenta, igualmente correta |
| Snapshot divergente | normal | reconciliação corrige |
| Capacidade esgotada | `429` com `Retry-After` | `429` com `Retry-After` |
| Circuito aberto | `503` na hora, **sem consultar o banco** | idem |

*Leitura por réplica não foi implementada*: escrita e leitura usam o mesmo
banco. Ver [LIMITATIONS.md](../../LIMITATIONS.md).

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
