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
| 1 | Mesma `Idempotency-Key` enviada duas vezes resulta em uma movimentação, com resposta idêntica | integração | **duplicar dinheiro é a pior falha deste domínio**: é invisível até a conciliação | **mecanismo verificado**; falta a resposta HTTP |
| 2 | Mesma chave com conteúdo diferente resulta em `409` e nada persistido | integração | impede adivinhar intenção do chamador | **distinção verificada**; falta o `409` da API |
| 3 | 1.000 movimentações concorrentes na mesma conta resultam em saldo igual à soma exata | concorrência | prova empírica de [ADR-0002](adr/0002-ledger-append-only.md); é o bug provável do legado | **20 simultâneas verificadas**; falta a escala |
| 4 | Saldo `asOf` anterior à primeira movimentação devolve zero, não erro | integração | fronteira clássica | **no domínio**; falta no fim a fim |
| 5 | Saldo `asOf` exatamente no instante de uma movimentação é inclusivo e documentado | integração | ambiguidade que vira discrepância de centavos | pendente (E07) |
| 6 | Snapshot é equivalente ao cálculo direto para 10.000 movimentações em datas aleatórias | propriedade | **a otimização não pode mudar o número** | **em miniatura**; falta com banco (E10) |
| 7 | Movimentação retroativa invalida snapshots e o saldo segue correto | integração | o caso difícil de [PR-04](premissas.md#pr-04) | pendente (E10) |
| 8 | Falha de commit (banco derrubado no meio) não persiste nada e o retry é limpo | integração | atomicidade real, não presumida | **atomicidade no conflito verificada**; falta derrubar o banco |
| 9 | Soma de um milhão de valores em `decimal` não acumula erro de arredondamento | unitário | precisão monetária | **implementado** |
| 10 | Requisição sem token ou com escopo errado responde `401`/`403` | integração | RNF-05 | pendente (E13) |
| 11 | Banco indisponível faz `/health/ready` falhar e `/health/live` continuar OK | integração | RNF-03; a confusão entre os dois converte falha parcial em queda total | pendente (E11) |
| 12 | Fronteiras de camada: domínio sem dependência de infraestrutura | arquitetura | protege [ADR-0001](adr/0001-monolito-modular.md) da erosão | **implementado** |

Os cenários 1, 3 e 6 são os que eu mostraria primeiro: cada um **prova uma
decisão arquitetural**, em vez de apenas exercitar código.

## O que já roda hoje

121 testes: 87 de domínio, 7 de arquitetura, 27 de integração.

Numa máquina sem Docker e sem `LEDGER_TEST_DB`, os 25 testes que dependem de
banco são **ignorados com mensagem explícita** e `dotnet test` sai com código 0.
Verificado nos dois caminhos.

### Testes de domínio (87)

Rodam em meio segundo, sem banco, sem HTTP e sem Docker — consequência direta de
`Ledger.Domain` não ter dependência nenhuma. Agrupados por tipo:

| Arquivo | O que cobre |
|---------|-------------|
| `MoneyTests` | escala máxima de 4 casas, rejeição em vez de arredondamento, operação entre moedas diferentes, comparação, igualdade com escalas distintas, soma de um milhão de centavos |
| `CurrencyTests` | normalização ISO 4217, rejeição de formato inválido, `default` da struct reconhecido como ausente |
| `IdempotencyKeyTests` | tamanho mínimo e máximo, rejeição de espaço e caractere de controle |
| `AccountTests` | sinal pelo tipo, moeda da conta, conta encerrada e bloqueada, ausência de saldo na conta, bitemporalidade, correlação obrigatória |
| `BalanceCalculatorTests` | zero sem movimentação, comutatividade, saldo de abertura, equivalência incremental, saldo negativo |
| `ImmutabilityTests` | nenhum tipo do domínio tem propriedade alterável, campo público mutável ou método que altere estado |

Três deles merecem destaque porque não testam código, **testam decisões**:

- **`Conta_nao_expoe_saldo`** compara a lista de propriedades de `Account` com a
  lista esperada. A ausência de saldo é a decisão central da solução
  ([ADR-0002](adr/0002-ledger-append-only.md)); sem este teste, reintroduzi-la
  seria uma linha conveniente que passa em revisão de código.
- **`Nao_valida_saldo_suficiente`** registra um débito de R$ 1.000.000 numa conta
  sem lastro e exige sucesso. É [PR-03](premissas.md#pr-03) em forma executável:
  se alguém adicionar validação de saldo, a conversa acontece antes do merge.
- **`Ponto_flutuante_binario_acumularia_erro_na_mesma_soma`** soma um milhão de
  vezes `0.01` em `double` e exige que o resultado **não** seja 10.000. Não
  verifica o domínio: documenta, de forma que não envelhece, por que o domínio
  proíbe `double`. Se alguém propuser a troca, este é o número que responde.

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

As duas últimas regras deixaram de passar trivialmente: com o domínio
implementado, elas agora varrem tipos reais — campos, propriedades, retornos e
parâmetros de `Money`, `Movement`, `Account` e dos demais — e confirmam que
nenhum deles usa `double` ou `float`.

A forma provável de o bug entrar nunca foi alguém declarar `double Saldo`. É
alguém escrever `var total = 0.0;` num cálculo auxiliar, onde nenhuma revisão de
código repara — e é esse caso que a regra pega.

### Testes de integração (27)

Contra PostgreSQL real. Dois deles são smoke do host HTTP e não precisam de
banco; os outros 25 precisam.

| Arquivo | O que cobre |
|---------|-------------|
| `HostSmokeTests` | o host sobe e responde `/health/live`; rota inexistente responde `application/problem+json`, não HTML |
| `SchemaTests` | o esquema **como o PostgreSQL o criou**: tabelas, `numeric(19,4)`, `timestamptz`, o índice que serve à consulta de saldo, onde a unicidade vive, e a ausência de coluna de saldo |
| `IdempotencyConstraintTests` | mesma chave duas vezes grava uma movimentação só; a transação da tentativa recusada é inteira desfeita; 20 tentativas simultâneas registram uma só; chaves diferentes com mesmo valor são duas movimentações |
| `PersistenceRoundTripTests` | precisão decimal na ida e volta, soma de mil centavos pelo próprio banco, instante com fuso de origem, ida e volta completa do domínio, e o esquema recusando valor não positivo e conta inexistente |

Três merecem destaque:

- **`Vinte_tentativas_simultaneas_com_a_mesma_chave_registram_uma_so`** é o caso
  que uma consulta prévia do tipo "esta chave já existe?" **não** resolveria:
  todas as tentativas começam juntas, nenhuma vê a outra, e só o constraint
  decide. É a razão de a suíte usar PostgreSQL real — num provedor em memória
  este teste passaria sem verificar nada.
- **`Tentativa_duplicada_nao_deixa_movimentacao_orfa`** verifica que a
  movimentação e o registro de idempotência entram na mesma transação. Se fossem
  separados, a segunda tentativa gravaria a movimentação e só depois falharia na
  chave — deixando uma duplicata no razão com a aparência de ter sido recusada.
- **`Nao_existe_coluna_de_saldo_em_lugar_nenhum`** consulta o catálogo do
  PostgreSQL. A ausência de saldo mutável é a decisão central da solução, e
  precisa valer no esquema e não só no domínio.

Que as **migrations aplicam** não tem teste próprio: o fixture as aplica contra o
banco em toda execução da suíte. Verificação de graça e sempre, em lugar de um
teste que alguém poderia esquecer de rodar.

Um projeto que compila mas não sobe não está pronto — daí o smoke do host. É a
classe de erro que, sem teste, só aparece ao rodar a aplicação à mão.

### Como obter o banco

Três caminhos, em ordem de preferência:

```bash
docker compose up -d
```

Com Docker disponível, não é nem necessário: o Testcontainers sobe e descarta um
container sozinho, e é assim que o CI roda.

Para apontar para um PostgreSQL que já existe:

```bash
LEDGER_TEST_DB="Host=localhost;Port=5432;Database=ledger;Username=ledger;Password=ledger" dotnet test
```

A diferença de tratamento entre os dois é deliberada: se `LEDGER_TEST_DB` está
definida e o banco não responde, os testes **falham** — quem definiu a variável
pediu para rodar contra aquele banco, e ignorar em silêncio esconderia o
problema. Sem a variável e sem Docker, eles são **ignorados**.

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
