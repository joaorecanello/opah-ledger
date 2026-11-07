# opah-ledger

[![ci](https://github.com/joaorecanello/opah-ledger/actions/workflows/ci.yml/badge.svg)](https://github.com/joaorecanello/opah-ledger/actions/workflows/ci.yml)

Razão contábil para movimentações financeiras de clientes de um banco digital:
registra créditos e débitos em contas e responde a posição consolidada de saldo
em qualquer ponto do tempo.

> **Estado atual: estrutura e decisões arquiteturais.**
> O projeto compila, sobe e tem testes passando, e as decisões estão
> documentadas. O domínio ainda não foi implementado — a seção
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
| [docs/premissas.md](docs/premissas.md) | as 12 lacunas do enunciado, a premissa adotada, o impacto se estiver errada e a pergunta que eu faria |
| [docs/arquitetura.md](docs/arquitetura.md) | diagramas C4 em três níveis e modelo de dados |
| [docs/fluxos.md](docs/fluxos.md) | sequência dos dois casos de uso, incluindo retry, conflito e movimentação retroativa |
| [docs/adr/](docs/adr/) | 11 decisões arquiteturais, cada uma com alternativas, trade-offs e gatilho de revisão |
| [docs/testes.md](docs/testes.md) | estratégia de testes e os 12 cenários críticos por ordem de risco |

**Se você tem cinco minutos:** [ADR-0002](docs/adr/0002-ledger-append-only.md) e
[ADR-0003](docs/adr/0003-idempotencia-como-contrato.md). Todas as outras decisões
decorrem dessas duas.

## Pré-requisitos

- [.NET SDK 10](https://dotnet.microsoft.com/download/dotnet/10.0) — qualquer
  versão 10.x serve (o `global.json` usa `rollForward: latestFeature`).
- Docker — **ainda não necessário**. Passa a ser a partir da persistência e dos
  testes de integração com Testcontainers, que serão ignorados com mensagem
  explícita quando o Docker não estiver disponível.

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
- 9 testes passando: 7 de arquitetura, 2 de smoke do host HTTP.
- Requisitos decompostos, 12 premissas registradas e 11 ADRs escritas.

**Não existe ainda:**

- Domínio (`Account`, `Movement`, `Money`).
- Persistência, migrations, PostgreSQL.
- Caso de uso de registro de movimentação com idempotência.
- Consulta de saldo consolidado.
- Endpoints de negócio.
- `docker compose up` de um comando.
- Snapshots, resiliência, observabilidade, segurança.

Os ADRs descrevem o desenho dessas partes; o código delas é o próximo passo.
Essa separação é deliberada: o enunciado pede para demonstrar como se pensa e
prioriza, e decidir antes de implementar é a ordem que eu defenderia em qualquer
projeto real.

## Estrutura

```
src/
  Ledger.Domain          entidades e invariantes. Dependência ZERO, por regra testada
  Ledger.Application     casos de uso e portas (interfaces)
  Ledger.Infrastructure  adaptadores: persistência, telemetria, outbox
  Ledger.Api             host HTTP
  Ledger.Worker          host de background: consolidação, reconciliação, outbox
tests/
  Ledger.Architecture.Tests  fronteiras de camada e proibição de ponto flutuante
  Ledger.Integration.Tests   API em memória; depois, PostgreSQL real
docs/
  adr/                       decisões arquiteturais
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

Detalhe em [docs/testes.md](docs/testes.md), incluindo os 12 cenários críticos
ordenados por risco financeiro e o que deliberadamente não é testado.
