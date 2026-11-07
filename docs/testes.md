# Estratégia de testes

Testes proporcionais ao **risco financeiro**, não a uma meta de cobertura.

Não persigo um número de cobertura. Em lugar disso, nomeio os cenários que, se
falharem em produção, custam dinheiro ao cliente — e garanto que todos tenham
teste. Cobertura alta com os cenários críticos descobertos é pior que cobertura
média com eles cobertos, porque dá sensação de segurança sem a segurança.

## Como rodar

```bash
dotnet test
```

Os testes de integração que dependem de PostgreSQL via Testcontainers detectam a
ausência de Docker e são **ignorados com mensagem explícita**, para que
`dotnet test` nunca falhe por ambiente na máquina de quem avalia. Isso é
requisito de RO-04, não conveniência.

## Cenários críticos, em ordem de risco

| # | Cenário | Tipo | Por que é crítico | Status |
|---|---------|------|-------------------|--------|
| 1 | Mesma `Idempotency-Key` enviada duas vezes resulta em uma movimentação, com resposta idêntica | integração | **duplicar dinheiro é a pior falha deste domínio**: é invisível até a conciliação | pendente (E06) |
| 2 | Mesma chave com conteúdo diferente resulta em `409` e nada persistido | integração | impede adivinhar intenção do chamador | pendente (E06) |
| 3 | 1.000 movimentações concorrentes na mesma conta resultam em saldo igual à soma exata | concorrência | prova empírica de [ADR-0002](adr/0002-ledger-append-only.md); é o bug provável do legado | pendente (E14) |
| 4 | Saldo `asOf` anterior à primeira movimentação devolve zero, não erro | integração | fronteira clássica | pendente (E07) |
| 5 | Saldo `asOf` exatamente no instante de uma movimentação é inclusivo e documentado | integração | ambiguidade que vira discrepância de centavos | pendente (E07) |
| 6 | Snapshot é equivalente ao cálculo direto para 10.000 movimentações em datas aleatórias | propriedade | **a otimização não pode mudar o número** | pendente (E10) |
| 7 | Movimentação retroativa invalida snapshots e o saldo segue correto | integração | o caso difícil de [PR-04](premissas.md#pr-04) | pendente (E10) |
| 8 | Falha de commit (banco derrubado no meio) não persiste nada e o retry é limpo | integração | atomicidade real, não presumida | pendente (E06) |
| 9 | Soma de um milhão de valores em `decimal` não acumula erro de arredondamento | unitário | precisão monetária | pendente (E04) |
| 10 | Requisição sem token ou com escopo errado responde `401`/`403` | integração | RNF-05 | pendente (E13) |
| 11 | Banco indisponível faz `/health/ready` falhar e `/health/live` continuar OK | integração | RNF-03; a confusão entre os dois converte falha parcial em queda total | pendente (E11) |
| 12 | Fronteiras de camada: domínio sem dependência de infraestrutura | arquitetura | protege [ADR-0001](adr/0001-monolito-modular.md) da erosão | **implementado** |

Os cenários 1, 3 e 6 são os que eu mostraria primeiro: cada um **prova uma
decisão arquitetural**, em vez de apenas exercitar código.

## O que já roda hoje

9 testes. Nenhuma linha de lógica de negócio foi escrita ainda, então o que
existe protege estrutura, não comportamento:

### Testes de arquitetura (7)

| Teste | O que trava |
|-------|-------------|
| Domínio não depende de nenhuma outra camada | erosão de [ADR-0001](adr/0001-monolito-modular.md) |
| Domínio não depende de infraestrutura técnica | EF Core, ASP.NET, Npgsql, `System.Data`, `System.Text.Json` |
| Casos de uso não dependem de infraestrutura nem de hosts | inversão de dependência |
| Casos de uso não dependem de tecnologia de persistência ou web | idem |
| Domínio não referencia nenhum pacote externo | olha o grafo de assemblies, não só dependência entre tipos |
| Domínio não usa ponto flutuante binário | `double`/`float` em campo, propriedade, retorno ou parâmetro |
| Casos de uso não usam ponto flutuante binário | idem |

Sem esses testes, "monólito modular" degrada para "monólito" em poucas sprints,
porque nada impede um `using` conveniente — e ninguém percebe até a extração de
um serviço ficar caro.

Os dois últimos passam trivialmente hoje, porque o domínio está vazio. O valor
deles começa quando houver código, e a forma provável de o bug entrar não é
alguém declarar `double Saldo` — é alguém escrever `var total = 0.0;` num
cálculo auxiliar, onde nenhuma revisão de código repara.

### Testes de smoke do host (2)

| Teste | O que pega |
|-------|-----------|
| O host sobe e responde `/health/live` | erro de composição: DI mal registrada, middleware em ordem inválida, configuração ausente |
| Rota inexistente responde `application/problem+json`, não HTML | que RFC 9457 vale para o pipeline inteiro, não só para os handlers escritos à mão |

Um projeto que compila mas não sobe não está pronto. Esta é a classe de erro
que, sem teste, só aparece ao rodar a aplicação à mão.

## Ferramental, e por quê

| Ferramenta | Decisão |
|------------|---------|
| xUnit | padrão do ecossistema |
| Shouldly | mensagem de falha legível, sem o problema de licença do FluentAssertions ([ADR-0011](adr/0011-biblioteca-de-assercao.md)) |
| NetArchTest | regras de dependência entre tipos |
| `Microsoft.AspNetCore.Mvc.Testing` | sobe a API em memória: sem porta a alocar, sem espera por readiness, sem flakiness de startup no CI |
| Testcontainers (E06) | PostgreSQL **real** |
| NBomber (E14) | carga leve, com evidências versionadas no repositório |

### Por que PostgreSQL real e não banco em memória

Porque banco em memória **não reproduz unique constraint sob concorrência**.

O cenário 1 — o mais importante da lista — depende exatamente dessa propriedade.
Um teste de idempotência contra provedor em memória passaria e não provaria nada:
seria falso-positivo no único cenário onde eu mais preciso de certeza.

O custo é depender de Docker nos testes de integração, mitigado pelo *skip* com
mensagem explícita.

## O que não vou testar, e por quê

| Não testado | Razão |
|-------------|-------|
| Código gerado (migrations, OpenAPI) | testar gerador de terceiro não encontra bug meu |
| Mapeamento trivial de DTO | o compilador já verifica; teste aqui só duplica a declaração |
| Percentual de cobertura como meta | ver o primeiro parágrafo deste documento |

## Critérios de aceitação da entrega

- [ ] build sem erros e sem warnings em ambiente limpo (RO-06) — **verificado no CI**
- [ ] `dotnet test` verde
- [ ] `docker compose up` seguido das chamadas do README reproduz o fluxo completo
- [ ] os 12 cenários acima com teste nomeado
