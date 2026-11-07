# Arquitetura

Diagramas no modelo C4, em três níveis de zoom. As decisões que produziram esta
estrutura estão em [adr/](adr/); aqui está o resultado.

> **Nota sobre a notação.** Os diagramas usam `flowchart` do Mermaid com
> agrupamentos, e não a extensão `C4Context`. O motivo é prático: a extensão C4
> do Mermaid ainda é experimental e o layout dela no GitHub é instável. Como
> todo requisito obrigatório do desafio depende de a documentação estar legível
> **neste repositório**, escolhi a notação que renderiza de forma confiável.

---

## Nível 1 — Contexto

Onde o sistema se encaixa e quem fala com ele.

```mermaid
flowchart TB
    subgraph banco["Banco digital"]
        originadores["Sistemas originadores<br/>PIX, TED, boleto, cartao, tarifas<br/><i>registram movimentacoes</i>"]
        consumidores["Sistemas consumidores<br/>app, internet banking, BI, contabilidade<br/><i>consultam saldo e extrato</i>"]
        clientes["Contexto de Clientes<br/><i>dados pessoais e titularidade</i>"]
        idp["Provedor de identidade<br/><i>emite tokens</i>"]
    end

    ledger["<b>opah-ledger</b><br/>razao contabil<br/>registra movimentacoes e<br/>responde saldo em qualquer instante"]

    originadores -->|"registra movimentacao<br/>HTTPS + JWT"| ledger
    consumidores -->|"consulta saldo e extrato<br/>HTTPS + JWT"| ledger
    ledger -.->|"valida titularidade da conta"| clientes
    idp -.->|"valida token"| ledger

    style ledger fill:#1f4e79,stroke:#0d2d4a,color:#ffffff
    style banco fill:#f6f8fa,stroke:#d0d7de,color:#24292f
```

Dois pontos de leitura:

**O ledger não guarda dado pessoal.** Ele referencia contas por identificador e
consulta o contexto de Clientes quando precisa de titularidade. Minimização é a
primeira camada de proteção, antes de criptografia ou controle de acesso — ver
[ADR-0009](adr/0009-protecao-de-dados-sensiveis.md).

**O ledger não decide se um débito é permitido.** Quem origina a movimentação já
tomou essa decisão; o razão registra o fato consumado. É a premissa
[PR-03](premissas.md#pr-03), e é a de maior impacto no desenho.

---

## Nível 2 — Containers

Os processos e os armazenamentos.

```mermaid
flowchart TB
    originadores["Sistemas originadores"]
    consumidores["Sistemas consumidores"]

    subgraph sistema["opah-ledger"]
        api["<b>Ledger.Api</b><br/>ASP.NET Core<br/><i>trafego sincrono,<br/>latencia previsivel</i>"]
        worker["<b>Ledger.Worker</b><br/>.NET Worker Service<br/><i>trabalho em lote</i>"]
        db[("<b>PostgreSQL</b><br/>movimentacoes append-only<br/>snapshots de saldo<br/>idempotencia, outbox")]
    end

    telemetria["Coletor OTLP<br/><i>traces, metricas, logs</i>"]

    originadores -->|"POST /movements"| api
    consumidores -->|"GET /balance, GET /movements"| api
    api -->|"INSERT fato / SELECT saldo"| db
    worker -->|"consolida snapshot<br/>reconcilia<br/>despacha outbox"| db
    api -.-> telemetria
    worker -.-> telemetria

    style api fill:#1f4e79,stroke:#0d2d4a,color:#ffffff
    style worker fill:#2d6a9f,stroke:#0d2d4a,color:#ffffff
    style db fill:#8c5a1a,stroke:#5c3a10,color:#ffffff
    style sistema fill:#f6f8fa,stroke:#d0d7de,color:#24292f
```

### Por que dois processos

A API atende requisição síncrona com latência previsível. O worker consolida
snapshot, reconcilia e varre o outbox — trabalho em lote, pesado e tolerante a
atraso.

Num processo único, uma consolidação pesada come o *thread pool* que atende
consultas de saldo. Esse é um mecanismo plausível para a "lentidão em horários
de pico" que o enunciado descreve, e separar os dois o elimina. Também permite
escalar de forma independente: muitas instâncias de API, uma de worker.

Eles compartilham banco e domínio de propósito. A fronteira aqui é de perfil de
carga, não de domínio — ver [ADR-0001](adr/0001-monolito-modular.md).

### O que o worker não é

O worker **não** está no caminho crítico de nenhuma operação. Se ele estiver
parado:

- a escrita funciona normalmente;
- a consulta de saldo cai no cálculo direto — mais lenta, igualmente correta.

Isso é consequência de o snapshot ser cache descartável
([ADR-0004](adr/0004-snapshot-de-saldo.md)). A falha do componente que produz a
otimização degrada latência, nunca correção.

---

## Nível 3 — Componentes

O interior da API. As dependências apontam só para dentro.

```mermaid
flowchart TB
    subgraph host["Ledger.Api"]
        endpoints["Endpoints v1<br/><i>HTTP, validacao, ProblemDetails</i>"]
        auth["AuthN / AuthZ<br/><i>JWT, escopos</i>"]
    end

    subgraph app["Ledger.Application"]
        registrar["RegistrarMovimentacao<br/><i>idempotencia, titularidade</i>"]
        consultar["ConsultarSaldoEm<br/><i>snapshot + delta</i>"]
        portas["Portas<br/><i>IMovimentacaoRepository<br/>ISnapshotRepository<br/>IUnitOfWork, IClock</i>"]
    end

    subgraph dominio["Ledger.Domain"]
        modelo["Account · Movement · Money<br/>BalanceSnapshot<br/><i>fatos imutaveis e invariantes</i>"]
    end

    subgraph infra["Ledger.Infrastructure"]
        ef["EF Core + Npgsql<br/><i>implementa as portas</i>"]
        outbox["Outbox<br/><i>mesma transacao do fato</i>"]
    end

    db[("PostgreSQL")]

    endpoints --> auth
    endpoints --> registrar
    endpoints --> consultar
    registrar --> modelo
    consultar --> modelo
    registrar --> portas
    consultar --> portas
    ef -. implementa .-> portas
    outbox -. implementa .-> portas
    ef --> db
    outbox --> db

    style dominio fill:#1f4e79,stroke:#0d2d4a,color:#ffffff
    style modelo fill:#1f4e79,stroke:#0d2d4a,color:#ffffff
    style app fill:#e8f0f7,stroke:#1f4e79,color:#24292f
    style infra fill:#f6f3ea,stroke:#8c5a1a,color:#24292f
    style host fill:#f6f8fa,stroke:#d0d7de,color:#24292f
```

### A seta que importa

Repare na direção de `ef -. implementa .-> portas`. A infraestrutura depende dos
casos de uso, nunca o contrário. `Ledger.Application` declara o que precisa como
interface; `Ledger.Infrastructure` implementa.

É inversão de dependência, e tem uma consequência concreta: trocar PostgreSQL
por outro banco não toca em uma linha de caso de uso nem de domínio.

### A regra mais forte do projeto

`Ledger.Domain` não tem **nenhuma** `PackageReference`. Ele não conhece banco,
HTTP, serialização nem injeção de dependência.

E isso é verificado no build, não combinado em reunião:
`Ledger.Architecture.Tests` falha se o domínio ganhar dependência de outra
camada, de EF Core, de ASP.NET, de Npgsql, ou de qualquer pacote externo.

Sem essa verificação, "modular" é intenção que não sobrevive à primeira sprint
sob pressão, porque nada impede um `using` conveniente — e ninguém percebe até
a extração de um serviço ficar caro.

---

## Modelo de dados

Nomes de tabela e coluna como o PostgreSQL de fato os criou — é o que alguém vê
ao abrir o `psql` durante um incidente.

```mermaid
erDiagram
    accounts ||--o{ movements : "registra"
    accounts ||--o{ balance_snapshots : "consolida"
    movements |o--|| idempotency_records : "garante unicidade"

    accounts {
        uuid id PK
        uuid customer_id "opaco, sem PII"
        char currency "char(3)"
        varchar status "Active, Blocked, Closed"
        bigint version "reservado, sem uso: ver ADR-0007"
    }

    movements {
        uuid id PK "v7, ordenavel por tempo"
        uuid account_id FK
        varchar type "Credit ou Debit, como texto"
        numeric amount "19,4 sempre positivo. CHECK amount gt 0"
        char currency "char(3)"
        timestamptz occurred_at "tempo de negocio"
        timestamptz posted_at "tempo de sistema"
        varchar idempotency_key "indice NAO unico: ver ADR-0012"
        varchar external_reference
        varchar correlation_id "rastreia ate a requisicao"
    }

    idempotency_records {
        varchar key PK "a unicidade vive aqui"
        uuid account_id
        uuid movement_id
        varchar request_fingerprint "distingue replay de conflito"
        timestamptz created_at
        timestamptz expires_at
    }

    balance_snapshots {
        uuid account_id PK, FK
        date as_of_date PK
        numeric closing_balance "19,4"
        char currency "char(3)"
        bigint movement_count
        boolean is_stale "invalidado por retroativa"
    }
```

Índices, além das chaves primárias:

| Índice | Serve a |
|--------|---------|
| `ix_movements_account_id_occurred_at` | a consulta de saldo e o extrato: dada uma conta, percorrer a janela de tempo. É a razão de a consulta temporal não varrer a tabela |
| `ix_movements_idempotency_key` | localizar a movimentação pela chave. **Não é único** |
| `ix_accounts_customer_id` | localizar as contas de um cliente |
| `ix_idempotency_records_expires_at` | a limpeza das chaves expiradas, sem varredura |

Cinco pontos que não se leem no diagrama:

**Não existe coluna de saldo em `accounts`.** Essa ausência é a decisão central
de [ADR-0002](adr/0002-ledger-append-only.md): saldo é função sobre o log, não
estado. Há teste de integração que consulta o catálogo do PostgreSQL e exige que
nenhuma tabela, exceto `balance_snapshots`, tenha coluna com "balance" ou
"saldo" no nome.

**`movements` nunca sofre `UPDATE` nem `DELETE`.** Correção de erro é
movimentação compensatória que referencia a original
([PR-06](premissas.md#pr-06)).

**`amount` é sempre positivo** — o sinal é semântica de `type`. Isso impede a
classe de bug em que um crédito negativo se torna um débito silencioso, e está
imposto **duas vezes**: pelo domínio e por `CHECK (amount > 0)` no esquema.
Defesa em profundidade: um caminho de código que contorne o domínio, como uma
importação em lote, ainda é barrado. Há teste que escreve direto na tabela para
verificar isso.

**A unicidade da idempotência não está em `movements`.** Está na chave primária
de `idempotency_records`, por uma razão que só apareceu ao implementar: o
PostgreSQL exige que o unique constraint de uma tabela particionada inclua a
coluna de particionamento, e `movements` será particionada por mês. Um unique em
`(chave, data)` permitiria a mesma chave em dias diferentes — a duplicata que a
chave existe para impedir. Ver [ADR-0012](adr/0012-idempotencia-em-tabela-propria.md).

**`balance_snapshots` é descartável.** Pode ser truncada por inteiro e o worker
a reconstrói do ledger. É o que torna a otimização de
[ADR-0004](adr/0004-snapshot-de-saldo.md) aceitável em dados financeiros.

### Modelo de persistência separado do domínio

Os tipos que o EF Core mapeia (`MovementRecord`, `AccountRecord`, …) **não são**
as entidades de domínio. O domínio é estritamente imutável, com construtores
privados e objetos de valor; um ORM precisa de tipos que ele consiga
materializar. Em lugar de o domínio ceder, a persistência ganhou tipos próprios
e um mapeador explícito.

O argumento decisivo: **este sistema não usa rastreamento de mudanças.** A
escrita é `INSERT` e a leitura é agregação; não há alteração a detectar. O que o
EF traz de valor aqui são migrations versionadas e esquema tipado, e para isso
tipos planos bastam. Daí `QueryTrackingBehavior.NoTracking` como padrão do
contexto — declaração de que o recurso não é usado, não otimização.
Ver [ADR-0013](adr/0013-modelo-de-persistencia-separado.md).

---

## Contratos da API

Detalhe e exemplos ficam no OpenAPI gerado em `/openapi/v1.json`. O essencial:

| Operação | Contrato |
|----------|----------|
| Registrar | `POST /api/v1/accounts/{accountId}/movements`, header `Idempotency-Key` **obrigatório** |
| Consultar saldo | `GET /api/v1/accounts/{accountId}/balance?asOf=...` |
| Extrato | `GET /api/v1/accounts/{accountId}/movements?from&to&cursor&limit` |

Decisões transversais:

- **Versionamento no path** (`/v1`). Explícito, cacheável, trivial de rotear.
- **Erros em ProblemDetails** (RFC 9457), inclusive os gerados pelo pipeline —
  já verificado por teste: rota inexistente responde `application/problem+json`,
  não HTML.
- **Paginação keyset**, não `offset`. `OFFSET` degrada com a profundidade e, sob
  escrita concorrente, pula ou repete registros entre páginas
  ([ADR-0007](adr/0007-ordenacao-do-ledger.md)).
- **A resposta de saldo diz como foi calculada** (`computedFrom`, `stale`). Em
  finanças, rastreabilidade do número vale mais que elegância do payload: quando
  alguém contestar um saldo, a primeira pergunta é de onde ele veio.
