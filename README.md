# opah-ledger

[![ci](https://github.com/joaorecanello/opah-ledger/actions/workflows/ci.yml/badge.svg)](https://github.com/joaorecanello/opah-ledger/actions/workflows/ci.yml)

Razão contábil para movimentações financeiras de clientes de um banco digital:
registra créditos e débitos em contas e responde a posição consolidada de saldo
em qualquer ponto do tempo.

> **Estado atual: solução demonstrável de ponta a ponta.** Os dois requisitos
> funcionais do enunciado estão implementados e expostos por HTTP, com 200
> testes — 61 deles contra PostgreSQL real. A seção
> [Estado atual](#estado-atual) é explícita sobre o que existe e o que não
> existe.

## O problema

O sistema legado do banco cresceu sem planejamento e apresenta lentidão,
instabilidade em horário de pico e dificuldade de manutenção. O pedido foi
repensar a solução do zero.

A hipótese de causa-raiz que orienta este desenho: esse conjunto de sintomas
quase sempre vem de um **saldo armazenado como coluna mutável**, atualizado em
concorrência. Sem proteção, duas transações simultâneas leem o mesmo saldo e uma
sobrescreve a outra — dinheiro desaparecendo, silenciosamente. Com lock, a coluna
se torna um ponto de serialização por conta, as conexões empilham no pico e os
timeouts cascateiam para chamadas que nem tocavam aquela conta.

A hipótese não é verificável sem acesso ao legado, e está registrada como
hipótese. Mas é ela que orienta a decisão central.

## As três decisões centrais

1. **O saldo não existe como campo.** O ledger é append-only: movimentação é
   fato imutável, saldo é função sobre o log. Escrever é `INSERT`, e dois
   `INSERT` não disputam recurso nenhum. O ponto não é que a concorrência fique
   bem gerenciada — é que ela deixa de existir no modelo. De graça, o ledger
   passa a ser a própria trilha de auditoria.
   → [ADR-0002](docs/adr/0002-ledger-append-only.md)

2. **Idempotência é contrato, não detalhe.** `Idempotency-Key` obrigatória na
   escrita, com unique constraint no banco e resposta original persistida. Retry
   é o comportamento normal de todo cliente com timeout — ou seja, de todo
   cliente correto. E duplicar dinheiro é pior que indisponibilidade, porque é
   invisível até a conciliação.
   → [ADR-0003](docs/adr/0003-idempotencia-como-contrato.md)

   A implementação revelou uma armadilha: o constraint **não** pode ficar em
   `movements`. Com a tabela particionada por data, o PostgreSQL exigiria a data
   no unique, o que permitiria a mesma chave em dias diferentes — a duplicata
   que a chave existe para impedir, com um esquema que parece correto. A
   unicidade vive em tabela própria.
   → [ADR-0012](docs/adr/0012-idempotencia-em-tabela-propria.md)

3. **Snapshot de saldo é cache, nunca verdade.** A consulta temporal é servida
   por snapshot de período fechado mais o delta do período corrente. Como o
   passado é imutável, o snapshot de um período fechado nunca fica errado; e
   como é descartável, pode ser truncado por inteiro e reconstruído do ledger.
   Há reconciliação automática e métrica de divergência.
   → [ADR-0004](docs/adr/0004-snapshot-de-saldo.md)

O que foi deliberadamente **deixado de fora**, com justificativa escrita:
microsserviços ([ADR-0001](docs/adr/0001-monolito-modular.md)) e broker de
mensageria no caminho de escrita
([ADR-0006](docs/adr/0006-sem-broker-no-caminho-de-escrita.md)). O enunciado
pede para justificar as decisões, e decidir **não** adotar algo é a decisão que
mais precisa de justificativa.

## Documentação

Toda no repositório, conforme o requisito obrigatório.

| Documento | Conteúdo |
|-----------|----------|
| [docs/requisitos.md](docs/requisitos.md) | requisitos funcionais, não funcionais e restrições, rastreados ao enunciado |
| [docs/premissas.md](docs/premissas.md) | as 13 lacunas do enunciado, a premissa adotada, o impacto se estiver errada e a pergunta que eu faria |
| [docs/arquitetura.md](docs/arquitetura.md) | diagramas C4 em três níveis e modelo de dados |
| [docs/fluxos.md](docs/fluxos.md) | sequência dos dois casos de uso, incluindo retry, conflito e movimentação retroativa |
| [docs/adr/](docs/adr/) | 11 decisões arquiteturais, cada uma com alternativas, trade-offs e gatilho de revisão |
| [docs/testes.md](docs/testes.md) | estratégia de testes, os 12 cenários críticos por ordem de risco, e os dois defeitos que a suíte não pegou |

**Se você tem cinco minutos:** [ADR-0002](docs/adr/0002-ledger-append-only.md) e
[ADR-0003](docs/adr/0003-idempotencia-como-contrato.md). Todas as outras decisões
decorrem dessas duas.

## Pré-requisitos

- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0) — qualquer
  versão 10.x serve (o `global.json` usa `rollForward: latestFeature`).
- **Docker — opcional para os testes, necessário para rodar a API.** Sem ele,
  `dotnet test` roda os testes que não dependem de banco e **ignora** os que
  dependem, com mensagem explícita e código de saída 0. Com ele, os 200 rodam.
  Verificado nos dois caminhos.

Nenhum certificado TLS de desenvolvimento é necessário: localmente a API sobe em
HTTP puro de propósito. Em produção, o TLS é terminado antes da aplicação.

## Como rodar

```bash
dotnet restore
```

```bash
dotnet build -warnaserror
```

```bash
dotnet test
```

Para rodar também os testes de integração, suba o PostgreSQL:

```bash
docker compose up -d
```

Com Docker disponível isso não é obrigatório — o Testcontainers sobe e descarta
um container sozinho, e é assim que o CI roda. O `docker compose` serve a quem
quer um banco persistente para desenvolver.

Aplicar as migrations nesse banco:

```bash
dotnet tool restore && dotnet ef database update --project src/Ledger.Infrastructure --startup-project src/Ledger.Infrastructure
```

Subir a API:

```bash
dotnet run --project src/Ledger.Api
```

A API responde em `http://localhost:5080`. Para conferir:

```bash
curl http://localhost:5080/health/live
```

Resposta esperada: `{"status":"live"}`

A especificação OpenAPI fica em `http://localhost:5080/openapi/v1.json` (apenas
em ambiente de desenvolvimento).

## Exercitando a API

O fluxo abaixo foi executado contra a aplicação rodando, e as respostas são as
reais. Provisione uma conta:

```bash
curl -sS -X POST http://localhost:5080/api/v1/accounts -H 'Content-Type: application/json' -d '{"customerId":"2f1b8d4e-0c7a-4a1f-9b3e-5d6c7a8b9c01","currency":"BRL"}'
```

Registre um crédito. A chave de idempotência é **obrigatória**:

```bash
curl -sS -i -X POST http://localhost:5080/api/v1/accounts/SUA_CONTA/movements -H 'Content-Type: application/json' -H 'Idempotency-Key: pix-e3012-0001' -d '{"type":"Credit","amount":1500.00,"currency":"BRL","occurredAt":"2026-11-07T13:00:00Z"}'
```

```
HTTP/1.1 201 Created
Location: /api/v1/accounts/01a1bb50-.../movements/01a1bb50-692a-...
{"movementId":"01a1bb50-692a-...","amount":1500.0000,"currency":"BRL",...}
```

**Repita o comando exatamente igual.** A resposta é `200` com o corpo idêntico —
byte a byte — e **nenhuma segunda movimentação é criada**. É o comportamento que
torna seguro repetir depois de um timeout.

Agora mude só o valor, mantendo a mesma chave:

```bash
curl -sS -X POST http://localhost:5080/api/v1/accounts/SUA_CONTA/movements -H 'Content-Type: application/json' -H 'Idempotency-Key: pix-e3012-0001' -d '{"type":"Credit","amount":9999.00,"currency":"BRL","occurredAt":"2026-11-07T13:00:00Z"}'
```

```json
{"type":"urn:ledger:problem:idempotency-conflict","title":"Conflito de idempotência","status":409,
 "detail":"A chave de idempotência já foi usada com um conteúdo diferente...","traceId":"00-755e47c8..."}
```

A mesma chave com conteúdo diferente é **erro do chamador**, não ambiguidade a
resolver: as duas interpretações possíveis movimentam dinheiro.

Consulte o saldo:

```bash
curl -sS http://localhost:5080/api/v1/accounts/SUA_CONTA/balance
```

```json
{"accountId":"01a1bb54-...","asOf":"2026-11-08T13:23:43.571117+00:00","balance":1099.7500,
 "currency":"BRL","movementCount":2,"source":"DirectSum","stale":false}
```

E em um instante do passado — `asOf` é inclusivo e usa o tempo de negócio:

```bash
curl -sS 'http://localhost:5080/api/v1/accounts/SUA_CONTA/balance?asOf=2026-11-06T13:00:00Z'
```

> Use o sufixo `Z`. Um deslocamento com `+` precisa ser codificado como `%2B`:
> um `+` cru na URL é lido como espaço. A API responde `400` com essa instrução
> em lugar de falhar de forma obscura — foi um defeito real, descrito no
> [ADR-0015](docs/adr/0015-contrato-http.md).

**Todo erro responde ProblemDetails** (RFC 9457) com `type`, `title`, `status`,
`detail` e `traceId`. O `type` carrega o código do invariante violado — por
exemplo `urn:ledger:problem:amount-not-positive` — para que quem integra trate
cada caso sem interpretar texto livre.

Subir o worker (hoje ele sobe e fica ocioso — nenhum job registrado ainda):

```bash
dotnet run --project src/Ledger.Worker
```

## Estado atual

**Existe e está verificado:**

- Estrutura de solução em quatro camadas, com as dependências apontando só para
  dentro, travadas por teste de arquitetura.
- Dois processos separados: API (tráfego síncrono) e Worker (lote).
- Higiene de build: warnings são erros, analisadores em modo `Recommended`,
  estilo verificado no build e no CI.
- CI no GitHub Actions: formatação, build em Release com `-warnaserror` e testes.
  Verde em runner limpo.
- **Domínio completo e imutável:** `Account`, `Movement`, `Money`, `Currency`,
  `IdempotencyKey`, `BalanceSnapshot` e o cálculo de saldo.
- **Persistência em PostgreSQL:** esquema, migrations aplicáveis, índices,
  `CHECK (amount > 0)` e a unicidade da idempotência verificada no banco.
- **Registro de movimentação idempotente:** o caso de uso decide entre
  registrar, devolver a resposta original e recusar por conflito, verificado
  contra PostgreSQL real com 20 retries simultâneos.
- **Consulta de posição consolidada:** saldo em qualquer instante, somado no
  banco, com a equivalência contra o cálculo do domínio verificada por teste.
- **API REST v1** com OpenAPI, todos os erros em ProblemDetails (RFC 9457) e o
  `Location` do `201` apontando para um recurso que responde.
- 200 testes passando: 87 de domínio, 33 de aplicação, 7 de arquitetura,
  73 de integração (61 contra PostgreSQL real).
- Requisitos decompostos, 14 premissas registradas e 15 ADRs escritas.

**Não existe ainda:**
- Consulta de saldo consolidado.
- Endpoints de negócio.
- `docker compose up` de um comando.
- Snapshots, resiliência, observabilidade, segurança.

Os ADRs descrevem o desenho dessas partes. Decidir antes de implementar é a
ordem que eu defenderia em qualquer projeto real, e é também o que o enunciado
pede ao dizer que quer entender como se pensa e prioriza.

## O domínio

Seis tipos, nenhuma dependência externa, 87 testes. O que mais importa saber:

**`Account` não tem saldo.** A ausência é a decisão, não um esquecimento — e há
teste que falha se alguém adicionar a propriedade. A conta também não carrega a
coleção de movimentações: ela guarda os invariantes que governam o registro
(moeda e situação) e é a porta obrigatória por onde a movimentação é criada. É um
agregado que protege regras sem materializar o histórico que elas governam.

**`Movement` é imutável e guarda valor sempre positivo.** O sinal é semântica do
tipo (`Credit`/`Debit`), não do número, o que impede a classe de bug em que um
crédito negativo se comporta como débito sem que nada pareça errado. Guarda dois
instantes: `OccurredAt` (tempo de negócio) e `PostedAt` (tempo de sistema,
definido pelo servidor).

**`Money` usa `decimal` com até quatro casas, e rejeita em vez de arredondar.**
Quatro casas porque existe fração de centavo em taxa e rateio; rejeitar porque
arredondamento silencioso move dinheiro sem ninguém ter decidido isso. Operação
entre moedas diferentes é recusada: converter é decisão de negócio, não
conversão implícita.

**`MovementId` é UUID v7**, ordenável por tempo. Dá localidade de índice na
escrita e não revela volume de operação. Veio da biblioteca base do .NET em
lugar de ULID porque o domínio tem dependência zero — ver
[ADR-0007](docs/adr/0007-ordenacao-do-ledger.md).

**O cálculo de saldo é uma função pura** em `BalanceCalculator`, e é comutativo:
há teste que embaralha os lançamentos e exige o mesmo resultado. Isso confirma
que o saldo não depende de ordenação contígua, que é o que permite escrever sem
contenção.

Duas premissas estão verificadas de forma executável, e não apenas escritas:

- **o razão não valida saldo** ([PR-03](docs/premissas.md#pr-03)) — há teste que
  registra um débito de R$ 1.000.000 numa conta sem lastro e exige sucesso;
- **conta bloqueada aceita registro, encerrada não**
  ([PR-13](docs/premissas.md#pr-13)) — bloqueio impede originar operação, não
  registrar fato que já aconteceu.

Se alguém discordar de uma dessas premissas, o teste falha e a conversa acontece
antes do merge.

## Estrutura

```
src/
  Ledger.Domain          entidades e invariantes. Dependência ZERO, por regra testada
  Ledger.Application     casos de uso e portas (interfaces)
    Abstractions/          IClock, ILedgerRepository, política temporal
    Movements/             RegisterMovement, RequestFingerprint
    Balances/              GetBalance
    Accounts/              OpenAccount, GetMovement
  Ledger.Infrastructure  adaptadores
    Persistence/           DbContext, records, migrations, mapeamento explícito
  Ledger.Api             host HTTP
    Contracts/             DTOs de requisição e resposta
    Endpoints/             rotas v1
    LedgerProblems         desfecho de negócio para ProblemDetails
  Ledger.Worker          host de background: consolidação, reconciliação, outbox
tests/
  Ledger.Domain.Tests        invariantes do domínio. Sem banco, sem HTTP, sem Docker
  Ledger.Application.Tests   casos de uso com portas falsas em memória
  Ledger.Architecture.Tests  fronteiras de camada e proibição de ponto flutuante
  Ledger.Integration.Tests   API em memória e PostgreSQL real via Testcontainers
docs/
  adr/                       decisões arquiteturais
docker-compose.yml       PostgreSQL para desenvolvimento
```

As setas de dependência apontam `Api`/`Worker` → `Infrastructure` →
`Application` → `Domain`, e nunca o contrário. `Ledger.Domain` não tem uma única
`PackageReference`: ele não conhece banco, HTTP, serialização nem injeção de
dependência. Se um conceito precisa de qualquer um deles, está na camada errada.

## Padrões de build

Um dos requisitos obrigatórios do desafio é que o código compile **sem erros e
sem warnings**, sob pena de desconsideração. Isso está tratado por construção,
não por disciplina:

| Onde | O que garante |
|---|---|
| `Directory.Build.props` | `TreatWarningsAsErrors` — o build recusa compilar fora do padrão |
| `Directory.Build.props` | `AnalysisLevel` fixado em `10.0`, não em `latest`: um SDK mais novo na máquina de quem avalia não introduz regras novas que virariam erro |
| `global.json` | `rollForward: latestFeature` — qualquer SDK .NET 10 compila o projeto |
| `Directory.Packages.props` | versões de pacote centralizadas; divergência entre projetos é impossível |
| `.editorconfig` | estilo e nomenclatura verificados no build (`EnforceCodeStyleInBuild`) |
| `.github/workflows/ci.yml` | `dotnet format --verify-no-changes` mais build Release com `-warnaserror`, em runner limpo |

Há **uma** exclusão, e ela é por diretório: as migrations geradas pelo EF Core
ficam fora da análise de estilo e qualidade. O código que o EF gera não segue o
padrão do repositório — na primeira migration, `IDE0161` e `CA1861` —, e as
alternativas eram editar código gerado à mão, que a próxima migration desfaz, ou
baixar o nível de análise do projeto inteiro. A exclusão cobre só
`**/Migrations/*.cs`, e erro de compilação continua sendo erro: o requisito de
compilar sem warnings permanece verificado em todo o código escrito à mão.

A combinação de SDK tolerante com conjunto de regras congelado parece
contraditória e é o ponto: quem avalia compila com o SDK que tiver, e as regras
que decidem o que é warning não mudam com ele.

O `dotnet format` está no CI de propósito: nem toda regra de estilo do
`.editorconfig` é reportada por `dotnet build`, mesmo com
`EnforceCodeStyleInBuild`. Sem o passo separado, build local e CI poderiam
discordar sobre o que é código válido — e isso foi descoberto na prática, não
previsto.

## Testes

```bash
dotnet test
```

200 testes. Os de integração usam **PostgreSQL real**, não banco em memória:
banco em memória não reproduz unique constraint sob concorrência, logo o teste
de idempotência — o mais importante da suíte — passaria sem provar nada.

Detalhe em [docs/testes.md](docs/testes.md), incluindo os 12 cenários críticos
ordenados por risco financeiro, o que deliberadamente não é testado, e como
obter o banco.
