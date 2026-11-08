# Limitações conhecidas

O que **não** está resolvido, por quê, e o que eu faria. Está aqui porque
omissão justificada é decisão, enquanto omissão silenciosa parece lacuna — e
porque um avaliador que encontra sozinho um limite que eu não mencionei tem
razão em duvidar do resto.

Nada nesta página é surpresa de implementação: cada item foi uma escolha, e cada
escolha tem o requisito que a motivou (ou a ausência dele).

## Escala

### A consolidação varre todas as contas

`ILedgerRepository.ListAccountIdsAsync` devolve uma lista em memória. No volume
assumido em [PR-02](docs/premissas.md#pr-02) — dez milhões de contas — isso não
serve.

**Por que está assim.** A consolidação precisava funcionar e ser verificável
agora. Paginar e filtrar sem ter o comportamento correto primeiro seria otimizar
antes de medir.

**O que eu faria.** Consultar apenas contas com movimentação posterior ao último
fechamento, em lotes. Isso transforma a varredura num conjunto proporcional à
**atividade do dia**, não ao total de clientes — numa carteira de dez milhões de
contas com cem mil ativas por dia, é quatro ordens de grandeza de diferença.

A mesma limitação vale para a reconciliação, com o agravante de ela recalcular
cada fechamento do razão. Em produção ela rodaria por amostragem contínua mais
uma varredura completa em janela de baixo uso.

### `movements` não está particionada

[ADR-0005](docs/adr/0005-persistencia-postgresql.md) prevê particionamento por
mês para a retenção de cinco anos. O esquema está preparado — a coluna de
particionamento existe e a unicidade da idempotência já mora em tabela própria
justamente por causa disso ([ADR-0012](docs/adr/0012-idempotencia-em-tabela-propria.md)) —
mas as partições não foram criadas.

**O que falta.** Converter a tabela em particionada por `occurred_at`, criar
partições por mês e um job que crie a do mês seguinte antes de ela ser
necessária. O ganho é a consulta temporal tocar uma partição e o descarte de
dados expirados ser operação de metadados.

### Sem réplicas de leitura

Escrita e leitura no mesmo banco. A resposta de saldo já carrega indicação de
atualidade (`source`, `stale`), logo o contrato não muda quando réplicas
entrarem — mas elas não entraram.

## Requisitos não funcionais ainda não implementados

Estes estão desenhados em ADR e **não** em código. A separação é deliberada:
implementei o que protege a corretude do dinheiro e documentei o que protege a
escala e a operação.

| Pendente | Onde está desenhado |
|----------|---------------------|
| Resiliência: timeouts, retry com jitter, circuit breaker, readiness | [ADR-0008](docs/adr/0008-resiliencia-e-degradacao-declarada.md) |
| Observabilidade: OpenTelemetry, log estruturado, métricas de negócio | [ADR-0010](docs/adr/0010-observabilidade.md) |
| Segurança: JWT com escopos, autorização por titularidade, redação de log | [ADR-0009](docs/adr/0009-protecao-de-dados-sensiveis.md) |
| Outbox transacional | [ADR-0006](docs/adr/0006-sem-broker-no-caminho-de-escrita.md) |

**A ausência mais relevante é a de segurança.** Hoje a API não autentica nada:
qualquer requisição registra movimentação em qualquer conta. Isso é inaceitável
em produção e está assim porque autenticação sem um provedor de identidade real
seria encenação — ver [PR-09](docs/premissas.md#pr-09). O desenho está no
ADR-0009; a implementação não.

Há uma consequência prática: **o endpoint de provisionamento de conta é aberto**.
Ele existe para o projeto ser executável sem o contexto de Clientes
([PR-07](docs/premissas.md#pr-07)), e não deveria existir numa implantação real.

## Observabilidade do que já existe

A reconciliação **detecta** divergência de saldo e registra em log de nível de
erro, com os números. O que falta é a métrica
`ledger_snapshot_divergence_total` e o alerta que dispara em qualquer valor
diferente de zero.

Isso importa mais que parece: hoje a divergência está num log que alguém precisa
estar olhando. O alerta é o que transforma detecção em resposta.

## Métrica de contagem no caminho de snapshot

`movementCount` na resposta de saldo soma a contagem do fechamento com a do
delta. Está correto, e é menos útil do que parece: ele não distingue quantas
movimentações vieram de cada caminho. Para diagnóstico seria melhor expor as
duas separadamente.

## Teste de carga

Não há. O teste de concorrência da suíte usa vinte requisições simultâneas, o que
prova a **corretude** do mecanismo de idempotência sob concorrência — não a
capacidade do sistema.

**O que eu faria.** Perfil realista com NBomber ou k6: razão de leitura para
escrita conforme [PR-02](docs/premissas.md#pr-02), distribuição de contas quente
e fria, e medição de p99 sob carga sustentada. E, antes disso, um experimento que
derrube o banco no meio de uma escrita para verificar o comportamento de
[ADR-0008](docs/adr/0008-resiliencia-e-degradacao-declarada.md) na prática em
lugar de no papel.

## Domínio

### Não há dupla entrada contábil

O razão registra movimentação por conta, não débito e crédito balanceados entre
contas. Um razão bancário de verdade fecha em partida dobrada: toda saída de uma
conta é entrada em outra, e a soma de tudo é zero.

**Por que não está aqui.** O enunciado pede saldo por conta, não balanço entre
contas. Implementar partida dobrada mudaria o modelo de uma movimentação para um
lançamento com duas pernas, e seria complexidade sem requisito que a sustente.

**O que isso impede hoje.** Não há como verificar que o sistema inteiro fecha —
só que cada conta fecha. Numa instituição real, essa verificação global é o
controle mais forte que existe.

### Sem estorno como operação

Correção de erro é movimentação compensatória que referencia a original
([PR-06](docs/premissas.md#pr-06)), e esse é o desenho correto. O que falta é a
operação explícita: um endpoint que receba o identificador da movimentação a
estornar, valide que ela existe e não foi estornada, e crie a compensação com a
referência. Hoje isso seria feito registrando uma movimentação comum na mão.

### Sem multimoeda

Moeda única por conta. `Money` carrega a moeda e recusa operação entre moedas
diferentes desde o início — barato de manter correto, caro de retrofitar — mas
não há conversão nem conta multimoeda. Ver [PR-05](docs/premissas.md#pr-05).

### Sem extrato paginado

Há leitura de uma movimentação por identificador, não listagem. A decisão de
paginação já está tomada — cursor por *keyset*, nunca `offset`, porque `offset`
degrada com a profundidade e pula registros sob escrita concorrente
([ADR-0007](docs/adr/0007-ordenacao-do-ledger.md)) — mas o endpoint não existe.

## Operação

### Sem infraestrutura como código

`docker compose` sobe o sistema localmente. Não há Terraform nem manifesto de
Kubernetes, e não exigidos — ver [PR-10](docs/premissas.md#pr-10). IaC que não é
executada e validada não é avaliável.

### Sem migração do legado

O enunciado pede para repensar a solução, não para migrar. A estratégia está
documentada em [PR-11](docs/premissas.md#pr-11): escrita em paralelo, backfill
por saldo inicial mais replay, e *shadow read* comparando os dois saldos com
métrica de divergência até zero sustentado antes de cortar o legado.

### O CI não constrói a imagem

O workflow roda restore, formatação, build e testes. Ele **não** constrói a
imagem Docker nem sobe o `compose` — isso foi verificado só na minha máquina. Se
o Dockerfile quebrasse por um detalhe de ambiente, o CI não acusaria.

É a lacuna mais fácil de fechar da lista: um passo de `docker compose build`.

## Premissas que, se erradas, mudam o desenho

Duas merecem destaque porque a correção não é localizada:

**[PR-03](docs/premissas.md#pr-03) — o razão não valida saldo.** Se ele tiver que
recusar débito a descoberto, surge um invariante por conta e com ele uma
fronteira de consistência: concorrência otimística com versão, ou reserva prévia.
Isso reintroduz contenção por conta, que é exatamente o que
[ADR-0002](docs/adr/0002-ledger-append-only.md) eliminou. O campo de versão já
está no esquema para o plug ser localizado, mas a decisão de concorrência muda.

**[PR-04](docs/premissas.md#pr-04) — retroatividade existe.** Se não existisse, a
invalidação de fechamento e metade da complexidade de
[ADR-0004](docs/adr/0004-snapshot-de-saldo.md) seriam desnecessárias. É a
premissa que mais custou em código.
