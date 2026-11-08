# ADR-0003 — Idempotência como contrato de primeira classe

- **Status:** aceito
- **Data:** 2026-11-07
- **Requisitos relacionados:** RF-01, RNF-01 (instabilidade), RNF-03 (falhas parciais), RNF-04 (consistência)

## Problema

Um sistema interno envia uma movimentação. A rede cai antes da resposta chegar.
O chamador não sabe se o registro aconteceu, e reenvia.

Como garantir que o dinheiro não seja contabilizado duas vezes?

Isso não é cenário excepcional. É o comportamento normal de qualquer cliente que
tenha timeout configurado — ou seja, de todo cliente correto. Um sistema
financeiro que não responde a essa pergunta na arquitetura vai responder a ela
na conciliação.

## Alternativas consideradas

1. **Nada.** Confiar que o chamador não repete.
2. **Deduplicação por janela de tempo** mais hash do conteúdo.
3. **`Idempotency-Key` obrigatória**, fornecida pelo cliente, com *unique
   constraint* no banco e a resposta original persistida.

## Decisão

Alternativa 3, com a chave **obrigatória** no registro de movimentação.

Semântica:

| Situação | Resposta |
|----------|----------|
| Chave nova | `201 Created`, movimentação registrada |
| Mesma chave, mesmo conteúdo | `200 OK` com a resposta original persistida |
| Mesma chave, conteúdo diferente | `409 Conflict`, nada registrado |
| Chave ausente | `400 Bad Request` |

## Justificativa

**Alternativa 1 é negligência,** não simplicidade. Duplicata silenciosa é a pior
falha possível deste domínio — pior que indisponibilidade. Indisponibilidade o
chamador percebe e trata; duplicata é invisível até alguém conferir o extrato.

**Alternativa 2 é frágil nos dois sentidos.** Janela curta deixa passar retry
tardio — e retry tardio é precisamente o caso de uma rede instável, que é o
cenário do requisito. Janela longa bloqueia movimentação legítima e idêntica:
duas transferências de R$ 50,00 para o mesmo destino no mesmo minuto são
plenamente válidas, e só a chave distingue uma da outra. Nenhum ajuste da janela
resolve os dois lados, porque o problema não é de calibragem.

**Alternativa 3 move a garantia para o único lugar que não mente sob
concorrência: o índice único do banco.** Duas requisições simultâneas com a
mesma chave não disputam lógica de aplicação — uma delas viola a constraint e o
caso de uso trata essa violação como "já registrado", devolvendo a resposta
original.

A garantia não depende de ordem de execução, de janela de tempo nem de cache.
Depende de uma propriedade transacional do banco.

### Por que a chave é obrigatória, e não opcional

Chave opcional é chave ausente na hora em que importa. Se o contrato a aceita
como opcional, algum cliente vai omiti-la, e o primeiro incidente de rede vai
duplicar lançamentos daquele cliente.

Tornar obrigatório empurra responsabilidade para quem chama — gerar e persistir
a chave antes de enviar. É custo real, e é o custo certo: só o chamador sabe se
esta tentativa é a primeira ou a segunda da *mesma* intenção de negócio.

### Por que `409` e não "tentar acertar"

Mesma chave com conteúdo diferente significa bug no chamador: ele reusou uma
chave, ou mudou o valor entre tentativas. As duas interpretações possíveis —
"registrar o novo valor" ou "ignorar e devolver o antigo" — podem estar erradas,
e ambas movimentam dinheiro.

Adivinhar intenção em sistema financeiro é inaceitável. `409` devolve o problema
a quem tem a informação para resolvê-lo.

### Por que `200` e não `201` no replay

`201 Created` afirmaria que algo foi criado nesta requisição, o que é falso. O
replay devolve **a resposta original persistida**, não um reprocessamento: o
identificador da movimentação e o instante de registro são os da primeira vez.

Isso importa porque o chamador pode estar usando o identificador devolvido para
conciliar. Devolver um identificador novo para o mesmo fato criaria a divergência
que a idempotência existia para evitar.

## Trade-offs aceitos

| Custo | Mitigação |
|-------|-----------|
| Responsabilidade adicional para o chamador | documentado no README com exemplo; é a parte que só ele pode cumprir |
| Tabela de idempotência cresce | TTL de 7 dias com job de limpeza; a janela cobre folgadamente qualquer retry plausível |
| Uma escrita a mais por movimentação | irrelevante frente a `INSERT` indexado |

## Consequências

- Há *unique constraint* em banco sobre a chave de idempotência. A garantia vive
  no esquema, não no código de aplicação — então ela sobrevive a um caso de uso
  reescrito.
  > **Onde exatamente ela vive** está em
  > [ADR-0012](0012-idempotencia-em-tabela-propria.md). A leitura natural deste
  > ADR era uma coluna única em `movements`, e ela não funciona: com a tabela
  > particionada por data, o PostgreSQL exigiria a data no constraint, o que
  > permitiria a mesma chave em dias diferentes — a duplicata que a chave existe
  > para impedir.
- Se o commit falhar, nada foi registrado e o retry é seguro **por construção**:
  como o ledger é append-only ([ADR-0002](0002-ledger-append-only.md)), não há
  update parcial a desfazer.
- Idempotência passa a ser o padrão da casa, e não um detalhe deste endpoint.
  Isso é pré-requisito do Outbox de [ADR-0006](0006-sem-broker-no-caminho-de-escrita.md):
  entrega *at-least-once* só é tolerável se o consumidor for idempotente.
- Há métrica `ledger_idempotent_replays_total`. Alta de replays não é erro do
  ledger — é sinal de que algum cliente está com timeout mal calibrado, e é uma
  informação operacional útil que sai de graça.
- **O relógio do sistema é truncado ao microssegundo.** A resposta do replay vem
  do banco; a do registro original vem do relógio em memória. O
  `DateTimeOffset.UtcNow` tem resolução de 100 ns e o `timestamptz` guarda
  microssegundos, então sem truncar os dois carimbos diferem em até 999 ns — e o
  replay deixa de ser *idêntico* ao original, que é precisamente a garantia deste
  ADR. Foi um teste de integração que encontrou isso.
- **Retries simultâneos do mesmo fato resultam em replay, não em conflito.** Uma
  tentativa registra e as demais recebem a resposta original. Recusar as
  perdedoras com `409` seria punir o chamador por uma falha de rede — e é o erro
  que uma leitura apressada deste ADR produziria.

## Testes que verificam esta decisão

São os dois primeiros da suíte de integração, por ordem de risco financeiro:

1. mesma chave enviada duas vezes resulta em **uma** movimentação, com resposta
   idêntica;
2. mesma chave com conteúdo diferente resulta em `409` e **nada** persistido.

## Gatilho de revisão

Se o banco exigir que o ledger aceite movimentação sem chave — por exemplo,
numa ingestão em lote de sistema legado que não a produz —, a alternativa é
derivar a chave de uma combinação natural do próprio lote (identificador do
arquivo mais número de linha). Isso preserva a garantia sem exigir mudança no
produtor, e é melhor que tornar o campo opcional.
