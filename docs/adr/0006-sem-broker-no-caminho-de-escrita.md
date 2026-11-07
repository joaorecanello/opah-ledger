# ADR-0006 — Sem broker no caminho de escrita; Outbox como caminho de evolução

- **Status:** aceito
- **Data:** 2026-11-07
- **Requisitos relacionados:** RNF-01 (instabilidade), RNF-02 (alta demanda), RNF-03 (falhas parciais), RNF-04 (consistência)

## Problema

O enunciado pede comportamento adequado sob alta demanda e falha parcial.
Mensageria é a resposta reflexa. Ela resolve o problema certo aqui?

## Alternativas consideradas

1. **Broker na frente da escrita** (Kafka ou RabbitMQ): a API publica e responde
   `202 Accepted`; um consumidor persiste depois.
2. **Sem broker:** escrita síncrona e durável.
3. **Escrita síncrona mais Outbox transacional** para propagação assíncrona.

## Decisão

Alternativa 2 agora. Alternativa 3 quando houver consumidor.

Alternativa 1 está rejeitada — não por volume, mas por contrato.

## Justificativa

### Por que a alternativa 1 está errada aqui

Responder `202 Accepted` significa dizer ao chamador: *"recebi, talvez eu
registre"*.

As consequências não são pequenas:

- O cliente fica sem saber se a movimentação existe. Para resolver isso ele
  precisa de um endpoint de consulta de status, e de uma máquina de estados do
  lado dele.
- Entre o aceite e o registro há uma janela em que a consulta de saldo responde
  um valor que já está desatualizado em relação ao que o banco aceitou — e
  RNF-04 diz que qualquer inconsistência impacta o cliente diretamente.
- Falha no consumidor passa a ser um problema de operação, não de chamador: a
  movimentação está "em algum lugar", e ninguém que importa foi avisado.

E o ponto que fecha a questão: **trocar certeza de registro por throughput é uma
decisão de negócio, não técnica.** Pode ser a decisão certa — há sistemas de
ingestão que operam assim deliberadamente. Mas ela muda o contrato com quem
chama, e eu a levaria ao banco em vez de embutir num desafio técnico.

### Por que a alternativa 2 basta

O caminho de escrita é um `INSERT` indexado sem contenção — consequência direta
de [ADR-0002](0002-ledger-append-only.md). Um PostgreSQL modesto sustenta isso
na casa de milhares por segundo, acima do pico assumido em
[PR-02](../premissas.md#pr-02).

Mais importante: **o gargalho do legado quase certamente não era o `INSERT`.**
Era a contenção da coluna de saldo, que ADR-0002 já eliminou. Colocar um broker
na frente de um gargalho que não existe adiciona um componente a operar, uma
fonte de falha e uma garantia perdida, em troca de nada.

Esse é o raciocínio que eu considero o ponto desta decisão: identificar *qual*
problema a mensageria resolve, e constatar que não é o que eu tenho.

### Onde o broker entra de verdade: propagação

Mensageria é a resposta certa quando existe **alguém para ouvir**: notificar
outros sistemas, alimentar antifraude, disparar extrato, acionar um motor de
limites.

Aí surge o problema clássico do *dual write*: gravar no banco e publicar no
broker são duas operações que podem falhar independentemente. Sem cuidado, o
resultado é movimentação registrada sem evento publicado (sistemas a jusante
desatualizados) ou evento publicado sem movimentação registrada (pior ainda).

O **Outbox transacional** resolve: o evento é gravado na *mesma transação* do
fato, numa tabela de saída, e um despachante no worker a varre e publica. Ou as
duas coisas acontecem, ou nenhuma.

O que o Outbox dá é entrega **at-least-once**, não exactly-once — o despachante
pode publicar e falhar antes de marcar como enviado. Por isso consumidores
precisam ser idempotentes, e é também por isso que
[ADR-0003](0003-idempotencia-como-contrato.md) importa além do seu endpoint: ele
estabelece idempotência como padrão da casa antes de haver um consumidor para
precisar dela.

## Trade-offs aceitos

| Custo | Avaliação |
|-------|-----------|
| Pico extremo derruba escrita em vez de enfileirar | é a escolha de RNF-04: falha visível é melhor que aceite incerto. Ver [ADR-0008](0008-resiliencia-e-degradacao-declarada.md) |
| Sem desacoplamento temporal entre API e banco | o banco é a fronteira de durabilidade; desacoplar dele é desacoplar da verdade |
| Outbox adiciona uma tabela e um despachante | só quando houver consumidor; antes disso seria infraestrutura sem propósito |

## Consequências

- A API responde `201` apenas depois de o fato estar durável. Quem recebe `201`
  sabe que a movimentação existe.
- Não há componente de mensageria para operar, monitorar ou dimensionar no
  escopo atual.
- Quando o Outbox entrar, o consumidor idempotente já é o padrão estabelecido.

## Gatilho de revisão

**Para o Outbox (alternativa 3):** no momento em que existir o primeiro
consumidor de eventos de movimentação. É evolução prevista, não mudança de
rumo.

**Para o broker no caminho de escrita (alternativa 1):** se o banco decidir, com
conhecimento do trade-off, que absorver pico vale mais que confirmação
imediata. Nesse caso o desenho muda de verdade: contrato `202` com endpoint de
status, identificador de requisição rastreável, e política explícita sobre o que
a consulta de saldo responde durante a janela de processamento. Não é um
parâmetro a girar; é outro produto.
