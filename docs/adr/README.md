# Decisoes arquiteturais (ADRs)

Cada ADR registra: o problema, as alternativas consideradas, a decisao, a
justificativa tecnica e de negocio, os trade-offs aceitos e o gatilho concreto
que levaria a revisar a decisao.

O gatilho e a parte que costuma faltar. Sem ele, um ADR registra o que foi
escolhido mas nao ajuda quem vier depois a saber se a escolha ainda vale.

## Indice

| ADR | Decisao | Status |
|-----|---------|--------|
| 0001 | Monolito modular em lugar de microsservicos | planejado (E02) |
| 0002 | Ledger append-only; saldo derivado, nunca estado mutavel | planejado (E02) |
| 0003 | Idempotencia como contrato de primeira classe | planejado (E02) |
| 0004 | Saldo consolidado por snapshot de periodo fechado + delta | planejado (E02) |
| 0005 | PostgreSQL como persistencia; por que nao NoSQL | planejado (E02) |
| 0006 | Sem broker no caminho de escrita; Outbox como evolucao | planejado (E02) |
| 0007 | Ordenacao do ledger: global agora, sequencia por conta como opcao | planejado (E02) |
| 0008 | Resiliencia e degradacao declarada | planejado (E02) |
| 0009 | Protecao de dados sensiveis e LGPD sobre ledger imutavel | planejado (E02) |
| 0010 | Observabilidade e metricas de negocio | planejado (E02) |
| [0011](0011-biblioteca-de-assercao.md) | Shouldly em lugar de FluentAssertions | aceito |

0011 ja esta escrito porque a decisao foi tomada durante o scaffold (E03), ao
escolher as dependencias de teste. Os demais sao escritos em E02, antes da
implementacao do dominio.
