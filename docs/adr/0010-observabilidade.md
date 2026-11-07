# ADR-0010 — Observabilidade com métricas de negócio, não só técnicas

- **Status:** aceito
- **Data:** 2026-11-07
- **Requisitos relacionados:** RNF-01 (instabilidade), RNF-04 (consistência), RNF-06 (manutenibilidade)

## Problema

O enunciado relata lentidão e instabilidade em horário de pico. Isso implica uma
pergunta que o sistema precisa ser capaz de responder: **o que, exatamente, está
lento ou errado agora?**

Um sistema que não responde a isso não é mantível, independentemente de quão bem
estruturado esteja o código.

## Decisão

OpenTelemetry para traces, métricas e logs, com *exporter* OTLP. Serilog em JSON
estruturado. *Correlation id* propagado do header, presente em todo log **e
persistido na movimentação**.

E, o ponto deste ADR: **métricas de negócio ao lado das técnicas.**

## Justificativa

### Por que OpenTelemetry e não uma solução proprietária

É padrão aberto e desacopla a instrumentação do destino. O banco pode ter
Datadog, New Relic, Grafana ou Application Insights — e provavelmente vai trocar
em algum momento. Instrumentar com OTLP significa que a troca é mudança de
configuração, não reinstrumentação de código.

### Por que log estruturado em JSON

Log em texto livre é legível por humano e inútil para agregação. Com instabilidade
intermitente em pico, a pergunta não é "o que diz esta linha" — é "quantas vezes
isso aconteceu, em quais contas, em que janela". Isso exige campo consultável,
não string.

### Por que o correlation id vai também na movimentação persistida

É a decisão menos óbvia deste ADR e a que mais paga.

Quando alguém contestar um lançamento meses depois, a pergunta vai ser "de onde
veio isso?". Com o correlation id gravado no próprio fato, dá para recuperar a
requisição original, o sistema que a originou e o trace completo — mesmo depois
de o log ter saído da retenção quente.

Sem isso, a trilha de auditoria do ledger diz *o que* aconteceu mas não *de que
chamada* veio.

## Métricas

### Técnicas

Latência por endpoint (p50, p95, p99), taxa de erro por classe, uso de pool de
conexões, GC e thread pool.

### De negócio

| Métrica | O que ela detecta |
|---------|-------------------|
| `ledger_movements_posted_total{type}` | volume real, por crédito e débito |
| `ledger_idempotent_replays_total` | alta indica cliente com timeout mal calibrado — não é erro do ledger, mas é informação operacional útil |
| `ledger_balance_query_duration_seconds` | degradação da consulta antes de virar reclamação |
| `ledger_snapshot_lag_seconds` | atraso do worker; indica quando consultas estão caindo no caminho lento |
| `ledger_snapshot_divergence_total` | **a mais importante do sistema** |

### Por que `ledger_snapshot_divergence_total` é a métrica mais importante

Porque é o detector de **o dinheiro ter divergido**.

Todas as outras medem se o sistema está rápido ou se está devolvendo erro.
Nenhuma delas detecta o modo de falha que realmente importa num razão contábil:
responder `200 OK`, em 5 milissegundos, com o número errado.

Essa métrica vem do job de reconciliação de
[ADR-0004](0004-snapshot-de-saldo.md), que recalcula do ledger e compara com o
snapshot. Alerta em qualquer valor diferente de zero — não há limiar aceitável
para divergência de saldo.

### O princípio geral

Sem métrica de negócio, não se distingue "sistema lento" de "sistema registrando
errado". E só a segunda é emergência.

Um painel que mostra p99 saudável e taxa de erro zero pode estar descrevendo um
sistema que perdeu movimentações silenciosamente. Métrica técnica mede o
veículo; métrica de negócio mede se a carga chegou.

## Health checks

Tratados em [ADR-0008](0008-resiliencia-e-degradacao-declarada.md): `/health/live`
não toca dependências, `/health/ready` toca. A distinção existe porque
confundi-las faz o orquestrador matar instâncias que só estão esperando o banco.

## Trade-offs aceitos

| Custo | Avaliação |
|-------|-----------|
| Instrumentação adiciona overhead | amostragem de trace em produção; métricas são agregadas e baratas |
| Log estruturado é mais verboso em volume | compensado por retenção em camadas: quente curto, frio longo |
| Correlation id na movimentação ocupa espaço por linha | irrelevante frente ao valor em auditoria |
| Reconciliação consome I/O | roda em janela de baixo uso, no worker, que é processo separado por [ADR-0001](0001-monolito-modular.md) |

## Consequências

- Log não contém valor monetário, descrição nem identificador de conta em claro
  ([ADR-0009](0009-protecao-de-dados-sensiveis.md)). Depuração se faz por
  correlation id, não por valor — o que é suficiente para seguir um fluxo.
- O alerta de divergência é o único que eu classificaria como severidade
  máxima. Lentidão é incidente; divergência de saldo é incidente **e** evento de
  conformidade.

## Gatilho de revisão

- Se o custo de telemetria crescer demais, a amostragem de trace diminui —
  métricas e o alerta de divergência não são amostráveis.
- Se o banco adotar um padrão corporativo de observabilidade diferente, a troca
  é de *exporter*. Foi por isso que a instrumentação é OTLP e não um SDK
  proprietário.
