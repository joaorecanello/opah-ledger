# opah-ledger

[![ci](https://github.com/joaorecanello/opah-ledger/actions/workflows/ci.yml/badge.svg)](https://github.com/joaorecanello/opah-ledger/actions/workflows/ci.yml)

Razao contabil para movimentacoes financeiras de clientes de um banco digital:
registra creditos e debitos em contas e responde a posicao consolidada de saldo
em qualquer ponto do tempo.

> **Estado atual: estrutura inicial (etapa E03 do plano).**
> O projeto compila, sobe e tem testes passando, mas o dominio ainda nao foi
> implementado. A secao [Estado atual](#estado-atual) e explicita sobre o que
> existe e o que nao existe.

## O problema

O sistema legado do banco cresceu sem planejamento e apresenta lentidao,
instabilidade em horario de pico e dificuldade de manutencao. O pedido foi
repensar a solucao do zero.

A hipotese de causa-raiz que orienta este desenho: esse conjunto de sintomas
quase sempre vem de um **saldo armazenado como coluna mutavel**, atualizado em
concorrencia. Sem protecao, duas transacoes simultaneas leem o mesmo saldo e uma
sobrescreve a outra - dinheiro desaparecendo. Com lock, a coluna se torna um
ponto de serializacao por conta, as conexoes empilham no pico e os timeouts
cascateiam.

## As tres decisoes centrais

1. **O saldo nao existe como campo.** O ledger e append-only: movimentacao e
   fato imutavel, saldo e funcao sobre o log. Escrever e `INSERT`, e dois
   `INSERT` nao disputam recurso nenhum. A concorrencia deixa de ser um problema
   a gerenciar e passa a ser uma propriedade ausente do modelo. De graca, o
   ledger passa a ser a propria trilha de auditoria.

2. **Idempotencia e contrato, nao detalhe.** `Idempotency-Key` obrigatoria na
   escrita, com unique constraint no banco e resposta original persistida. Retry
   e o comportamento normal de qualquer cliente com timeout, e duplicar dinheiro
   e pior que indisponibilidade - porque e invisivel ate a conciliacao.

3. **Snapshot de saldo e cache, nunca verdade.** A consulta temporal e servida
   por snapshot de periodo fechado mais o delta do periodo corrente. Como o
   passado e imutavel, o snapshot de um periodo fechado nunca fica errado; e
   como ele e descartavel, pode ser truncado e reconstruido do ledger a qualquer
   momento. Ha reconciliacao automatica e metrica de divergencia.

O que foi deliberadamente **deixado de fora**, com justificativa escrita:
microsservicos, broker de mensageria no caminho de escrita e infraestrutura
distribuida. Ver [`docs/adr/`](docs/adr/).

## Pre-requisitos

- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0) - qualquer
  versao 10.x serve (o `global.json` usa `rollForward: latestFeature`).
- Docker - **ainda nao necessario**. Passa a ser a partir de E05 (PostgreSQL) e
  dos testes de integracao com Testcontainers, que serao ignorados com mensagem
  explicita quando o Docker nao estiver disponivel.

Nenhum certificado TLS de desenvolvimento e necessario: localmente a API sobe em
HTTP puro de proposito. Em producao, TLS e terminado antes da aplicacao.

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

Subir a API:

```bash
dotnet run --project src/Ledger.Api
```

A API responde em `http://localhost:5080`. Para conferir:

```bash
curl http://localhost:5080/health/live
```

Resposta esperada: `{"status":"live"}`

A especificacao OpenAPI fica em `http://localhost:5080/openapi/v1.json`
(apenas em ambiente de desenvolvimento).

Subir o worker (hoje ele sobe e fica ocioso - nenhum job registrado ainda):

```bash
dotnet run --project src/Ledger.Worker
```

## Estado atual

**Existe e esta verificado:**

- Estrutura de solucao em 4 camadas, com as dependencias apontando so para
  dentro, travadas por teste de arquitetura.
- Dois processos separados: API (trafego sincrono) e Worker (lote).
- Higiene de build: warnings sao erros, analisadores em modo `Recommended`,
  estilo verificado no build e no CI.
- CI no GitHub Actions: formatacao, build em Release com `-warnaserror` e testes.
- 9 testes passando: 7 de arquitetura, 2 de smoke do host HTTP.

**Nao existe ainda:**

- Dominio (`Account`, `Movement`, `Money`) - etapa E04.
- Persistencia, migrations, PostgreSQL - E05.
- Caso de uso de registro de movimentacao com idempotencia - E06.
- Consulta de saldo consolidado - E07.
- Endpoints de negocio - E08.
- `docker compose up` de um comando - E09.
- Snapshots, resiliencia, observabilidade, seguranca - E10 a E13.
- ADRs 0001 a 0010 - E02 (o [0011](docs/adr/0011-biblioteca-de-assercao.md) ja
  esta escrito porque a decisao foi tomada aqui, no scaffold).

## Estrutura

```
src/
  Ledger.Domain          entidades e invariantes. Dependencia ZERO, por regra testada
  Ledger.Application     casos de uso e portas (interfaces)
  Ledger.Infrastructure  adaptadores: persistencia, telemetria, outbox
  Ledger.Api             host HTTP
  Ledger.Worker          host de background: consolidacao, reconciliacao, outbox
tests/
  Ledger.Architecture.Tests  fronteiras de camada e proibicao de ponto flutuante
  Ledger.Integration.Tests   API em memoria; a partir de E06, PostgreSQL real
docs/
  adr/                       decisoes arquiteturais
```

As setas de dependencia apontam `Api`/`Worker` -> `Infrastructure` ->
`Application` -> `Domain`, e nunca o contrario. `Ledger.Domain` nao tem uma
unica `PackageReference`: ele nao conhece banco, HTTP, serializacao nem injecao
de dependencia. Se um conceito precisa de qualquer um deles, esta na camada
errada.

## Padroes de build

Um dos requisitos obrigatorios do desafio e que o codigo compile **sem erros e
sem warnings**, sob pena de desconsideracao. Isso esta tratado por construcao,
nao por disciplina:

| Onde | O que garante |
|---|---|
| `Directory.Build.props` | `TreatWarningsAsErrors` - o build recusa compilar fora do padrao |
| `Directory.Build.props` | `AnalysisLevel` fixado em `10.0`, nao em `latest`: um SDK mais novo na maquina de quem avalia nao introduz regras novas que virariam erro |
| `global.json` | `rollForward: latestFeature` - qualquer SDK .NET 10 compila o projeto |
| `Directory.Packages.props` | versoes de pacote centralizadas; divergencia entre projetos e impossivel |
| `.editorconfig` | estilo e nomenclatura verificados no build (`EnforceCodeStyleInBuild`) |
| `.github/workflows/ci.yml` | `dotnet format --verify-no-changes` + build Release com `-warnaserror`, em runner limpo |

O `dotnet format` esta no CI de proposito: nem toda regra de estilo do
`.editorconfig` e reportada por `dotnet build`, mesmo com
`EnforceCodeStyleInBuild`. Sem o passo separado, build local e CI poderiam
discordar sobre o que e codigo valido.

## Testes

```bash
dotnet test
```

Hoje a suite cobre:

- **Arquitetura** - o dominio nao depende de nenhuma outra camada nem de
  tecnologia (EF Core, ASP.NET, Npgsql); os casos de uso nao dependem de
  infraestrutura; o dominio nao referencia pacote externo algum. Sem estes
  testes, "monolito modular" degrada para "monolito" em poucas sprints, porque
  nada impede um `using` conveniente.
- **Precisao monetaria** - proibe `double` e `float` no dominio e nos casos de
  uso. Sao base 2 e nao representam 0,1 exatamente; somar centavos acumula erro,
  e num razao contabil erro acumulado e dinheiro que nao fecha. O tipo do
  projeto e `decimal`. Hoje este teste passa trivialmente, porque o dominio esta
  vazio; o valor dele comeca em E04, e a forma mais provavel de o bug entrar e
  alguem escrever `var total = 0.0;` num calculo auxiliar, onde nenhuma revisao
  de codigo repara.
- **Smoke do host** - o processo sobe, responde `/health/live`, e rota
  inexistente responde `application/problem+json` em vez de HTML.
