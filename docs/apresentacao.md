# Roteiro de apresentação

Material para explicar a solução em voz alta. Não substitui os ADRs: aponta para
eles. Está organizado para três tempos — **cinco minutos**, **vinte** e **as
perguntas difíceis**.

## Em cinco minutos

**O problema, em uma frase.** Um razão contábil para um banco digital: registrar
créditos e débitos e responder o saldo em qualquer ponto do tempo, sem nunca
perder nem duplicar dinheiro, mesmo com falha parcial e alta demanda.

**A hipótese que orienta o desenho.** O legado lento e instável provavelmente
guarda o saldo como coluna mutável. Sob concorrência isso é perda silenciosa de
atualização ou serialização por conta. **É hipótese**: não tive acesso ao legado, e
está escrita como tal em [README](../README.md#o-problema).

**As três decisões, na ordem em que as defenderia.**

1. **O saldo não existe como campo** ([ADR-0002](adr/0002-ledger-append-only.md)).
   Movimentação é fato imutável; saldo é função do log. Escrever é `INSERT`, e dois
   `INSERT` não disputam recurso. A concorrência não é gerenciada: deixa de existir
   no modelo.
2. **Idempotência é contrato** ([ADR-0003](adr/0003-idempotencia-como-contrato.md),
   [ADR-0012](adr/0012-idempotencia-em-tabela-propria.md)). Retry é o comportamento
   normal de todo cliente correto, e duplicar dinheiro é pior que indisponibilidade
   porque é invisível até a conciliação.
3. **Snapshot é cache, nunca verdade** ([ADR-0004](adr/0004-snapshot-de-saldo.md)).
   Fechamento diário mais o delta do período corrente; descartável e reconciliado
   contra o razão.

**E o que deliberadamente não fiz.** Microsserviços
([ADR-0001](adr/0001-monolito-modular.md)) e broker no caminho de escrita
([ADR-0006](adr/0006-sem-broker-no-caminho-de-escrita.md)). Decidir não adotar é a
decisão que mais precisa de justificativa, e as duas têm gatilho de revisão
escrito.

## Demonstração (cerca de 5 minutos)

Pré-requisito: Docker. Nenhum .NET instalado.

```bash
docker compose up --build
```

```bash
TOKEN=$(dotnet run --project tools/Ledger.DevToken -- --cross-customer)
```

Roteiro, com o que mostrar em cada passo (comandos completos em
[Exercitando a API](../README.md#exercitando-a-api)):

| Passo | O que acontece | O que isso prova |
|-------|----------------|------------------|
| Chamar sem token | `401` com ProblemDetails | nada é aberto por padrão |
| Abrir conta, registrar crédito | `201`, `Location` aponta para um recurso que responde | contrato HTTP consistente |
| **Repetir o mesmo pedido** | `200`, mesma resposta, saldo inalterado | idempotência |
| Mesma chave, valor diferente | `409` | o sistema não adivinha intenção |
| Consultar saldo com `asOf` no passado | número diferente, `source` informado | consulta temporal |
| Token de **outro cliente** na mesma conta | `404`, igual a conta inexistente | titularidade sem oráculo de existência |
| `docker stop ledger-db`, depois uma escrita | `503` com `Retry-After`, não `500` | degradação declarada |
| `GET /health/ready` x `/health/live` | ready cai, live não | orquestrador não mata quem só espera |
| `docker start ledger-db` | a API volta sozinha | recuperação sem intervenção |

## Em vinte minutos: o que mais vale mostrar

- **A armadilha que a implementação revelou**
  ([ADR-0012](adr/0012-idempotencia-em-tabela-propria.md)): o constraint de
  idempotência não pode ficar em `movements` se a tabela for particionada por data,
  porque o PostgreSQL exigiria a data no unique e a mesma chave passaria em dias
  diferentes. Um esquema que parece correto e permite a duplicata que existe para
  impedir.
- **O teste de escala** ([docs/testes.md](testes.md#desempenho-medido)): mil
  lançamentos na mesma conta, cada um enviado duas vezes ao mesmo tempo, soma exata.
  É a evidência empírica da decisão 1.
- **A reconciliação**: o snapshot é verificado contra o razão e se autocorrige; a
  métrica `ledger_snapshot_divergence_total` existe, e qualquer valor acima de zero é
  incidente.
- **Observabilidade que não vaza** ([ADR-0010](adr/0010-observabilidade.md)): a
  medição achou o identificador da conta em quatro linhas de log, em três camadas
  diferentes; o terceiro vazamento só apareceu depois de corrigir os dois primeiros.
  Hoje um teste inspeciona mensagem, campos, escopos e exceção.
- **Segurança em duas perguntas** ([ADR-0016](adr/0016-autenticacao-e-titularidade.md)):
  escopo responde "pode ler saldos?"; titularidade responde "pode ler **esta** conta?",
  e é decidida no caso de uso para valer em qualquer porta de entrada.

## As perguntas difíceis, e a resposta honesta

**"Por que não microsserviços?"** Um monólito modular com dois processos (API e
Worker) já separa o que escala diferente. O que justificaria separar é equipe e
cadência de deploy independentes, não carga. Gatilho de revisão no ADR-0001.

**"E se o snapshot estiver errado?"** Ele é cache: descartável e reconstruível do
razão. A invalidação por movimentação retroativa acontece na **mesma transação** da
gravação, e a reconciliação detecta e corrige o que escapar.

**"Isso aguenta alta demanda?"** O que posso afirmar é o que medi: ~215 req/s de
escrita, todas na mesma conta, num notebook, com banco e cliente na mesma máquina,
sem perder nem duplicar. **Não é capacidade de produção.** O desenho tira a
contenção do caminho de escrita (sem lock de saldo), mas não fiz teste de carga
dimensionado nem testei réplica de leitura. Está em
[LIMITATIONS.md](../LIMITATIONS.md).

**"O que acontece se o banco cair no meio de um commit?"** Existe o caso ambíguo: o
banco grava e a resposta se perde. O cliente repete com a mesma chave e recebe a
resposta original. Há teste que simula exatamente isso, e outro para o conflito.

**"A chave de assinatura está no repositório."** Está, e é pública de propósito
(ambiente local). A API usa chave **simétrica**: quem valida poderia emitir. Em
produção seria JWKS do provedor de identidade. Está como concessão no
[ADR-0016](adr/0016-autenticacao-e-titularidade.md) e em LIMITATIONS, não escondida.

**"Quem fez cada lançamento?"** Hoje o razão grava o `correlation_id`, não o `sub` do
token. É uma lacuna real para auditoria; o gatilho de revisão está no ADR-0016.

**"E a LGPD num razão imutável?"** Minimização primeiro: o razão guarda
identificadores opacos, não dados pessoais. A obrigação de retenção contábil
prevalece sobre o pedido de eliminação (art. 16). Detalhe no
[ADR-0009](adr/0009-protecao-de-dados-sensiveis.md).

**"O que você não verificou?"** Respondo com a lista de
[LIMITATIONS.md](../LIMITATIONS.md). Os pontos que eu citaria primeiro: exportação
real para um coletor OTLP, alertas (a métrica existe, a regra não), partição da
tabela por data (desenhada, não aplicada), e o provedor de identidade.

## O que eu faria com mais tempo, por ordem de valor

1. Chave assimétrica e validação por JWKS, e gravar o `sub` no razão.
2. Regra de alerta sobre `ledger_snapshot_divergence_total` e painel versionado.
3. Teste de carga dimensionado, em ambiente separado do cliente.
4. Particionar `movements` por data, com o unique de idempotência já na tabela própria.
5. Outbox transacional ([ADR-0006](adr/0006-sem-broker-no-caminho-de-escrita.md)) quando
   o primeiro consumidor de eventos aparecer.

## Defeitos que a suíte não pegou, e o que mudou por causa deles

Estão em [docs/testes.md](testes.md). Vale citar ao menos um: a lição de que um
teste que passa sem ter rodado a mudança (build que falhou, binário antigo) é pior
que não ter teste — por isso toda verificação de mutação neste projeto confirma
primeiro que o build compilou.
