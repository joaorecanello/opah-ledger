# ADR-0005 — PostgreSQL como persistência

- **Status:** aceito
- **Data:** 2026-11-07
- **Requisitos relacionados:** RF-02, RNF-02 (alta demanda), RNF-04 (consistência)

## Problema

Qual persistência para um razão contábil com consulta temporal, sob a escala
assumida em [PR-02](../premissas.md#pr-02)?

## Alternativas consideradas

1. **PostgreSQL.**
2. **SQL Server.**
3. **MongoDB ou DynamoDB.**
4. **Banco de séries temporais** (InfluxDB, TimescaleDB).

## Decisão

PostgreSQL. Valor monetário como `numeric(19,4)`, mapeado para `decimal` em C#.

## Justificativa

Quatro requisitos técnicos decidem, e todos vêm de decisões anteriores:

| Preciso de | Por causa de |
|------------|--------------|
| Transação ACID com *unique constraint* | [ADR-0003](0003-idempotencia-como-contrato.md): é no índice único que a garantia de não-duplicidade de fato vive |
| Aritmética decimal exata | [ADR-0002](0002-ledger-append-only.md): dinheiro não é ponto flutuante binário |
| *Range scan* eficiente por conta e data | RF-02: consulta temporal |
| Particionamento por data | [PR-02](../premissas.md#pr-02): retenção de 5 anos |

PostgreSQL faz os quatro, roda em container sem licença — o que importa para
RO-04, porque quem avalia roda com um comando — e tem suporte maduro em EF Core
via Npgsql.

**Alternativa 3 está descartada para o ledger.** Consistência eventual num razão
contábil significa saldo errado em tela, e isso colide frontalmente com RNF-04.
Além disso, tipos numéricos de precisão flutuante aplicados a dinheiro são
defeito e não trade-off. Documentos seriam uma escolha razoável para armazenar
o *payload* bruto recebido de sistemas externos, se isso virasse requisito —
não para o razão.

**Alternativa 4 não serve.** Preciso de transação e unicidade; bancos de série
temporal otimizam agregação e *downsampling*, e tratam escrita duplicada como
detalhe. É a troca errada.

### Por que não SQL Server

Alternativa 2 é **tecnicamente equivalente** para este problema. Faria ACID,
`decimal(19,4)`, índices e particionamento igualmente bem, e seria minha escolha
num banco que já é casa Microsoft — com licença paga, equipe treinada e
ferramental em volta.

Registro isso explicitamente porque escolher tecnologia por preferência pessoal
é um erro que eu não quero cometer sem perceber. A diferença que pesou aqui é
operacional e contextual, não técnica: PostgreSQL sobe em container sem licença,
e o avaliador precisa rodar isto na máquina dele.

E a decisão é reversível a baixo custo por desenho: o acesso a dados fica atrás
de portas declaradas em `Ledger.Application`, e `Ledger.Domain` não conhece
banco nenhum ([ADR-0001](0001-monolito-modular.md), verificado por teste de
arquitetura). Trocar PostgreSQL por SQL Server é mudança de *provider* e de
*migrations*, não de desenho.

## Valor monetário: `numeric`, `decimal`, e nunca `double`

| Camada | Tipo | Por quê |
|--------|------|---------|
| PostgreSQL | `numeric(19,4)` | decimal exato, sem perda na ida e na volta |
| C# | `decimal` | base 10; representa 0,1 exatamente |
| Proibido | `double`, `float` | base 2; somar centavos acumula erro |

Quatro casas decimais, e não duas: há cenários financeiros com fração de centavo
(taxas, rateio, câmbio). Arredondar na persistência é perder informação que não
volta.

Há **teste de arquitetura** que falha o build se `double` ou `float` aparecerem
em campo, propriedade, retorno ou parâmetro no domínio ou nos casos de uso. Hoje
ele passa trivialmente porque o domínio está vazio; o valor dele começa quando
houver código. A forma provável de o bug entrar não é alguém declarar
`double Saldo` — é alguém escrever `var total = 0.0;` num cálculo auxiliar, onde
nenhuma revisão de código repara.

## Decisões de esquema que decorrem dos ADRs anteriores

| Decisão | Origem |
|---------|--------|
| Tabela de movimentações append-only, sem `UPDATE` nem `DELETE` | [ADR-0002](0002-ledger-append-only.md) |
| *Unique constraint* na chave de idempotência | [ADR-0003](0003-idempotencia-como-contrato.md) |
| Índice por `(AccountId, OccurredAt)` | RF-02 |
| Colunas `OccurredAt` e `PostedAt` separadas | bitemporalidade, [PR-04](../premissas.md#pr-04) |
| Particionamento por mês | [PR-02](../premissas.md#pr-02); consulta temporal toca uma partição |
| Tabela de snapshots separada e descartável | [ADR-0004](0004-snapshot-de-saldo.md) |

## Trade-offs aceitos

| Custo | Avaliação |
|-------|-----------|
| Escrita e leitura no mesmo banco no início | réplicas de leitura entram quando a medição justificar, não antes |
| EF Core adiciona abstração sobre SQL | ganho de *migrations* versionadas e mapeamento tipado compensa; consultas críticas podem usar SQL direto onde o plano importar |
| Particionamento adiciona complexidade operacional | só entra quando o volume justificar; o esquema inicial já prevê a coluna de particionamento |

## Gatilho de revisão

- **Réplicas de leitura** quando a consulta de saldo começar a competir por I/O
  com a escrita. A resposta já carrega indicação de atualidade do dado
  ([ADR-0004](0004-snapshot-de-saldo.md)), então o contrato não muda.
- **Sharding por conta** só quando um único PostgreSQL particionado deixar de
  dar conta. É uma mudança grande, e o ledger append-only a favorece: não há
  transação entre contas no caminho de escrita.
- **SQL Server** se o contexto corporativo pesar mais que a conveniência de
  container, como explicado acima.
