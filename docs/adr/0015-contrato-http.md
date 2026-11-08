# ADR-0015 — Contrato HTTP: desfecho para status, ProblemDetails e validação

- **Status:** aceito
- **Data:** 2026-11-08
- **Requisitos relacionados:** RF-01, RF-02, RNF-04 (consistência), RNF-06 (manutenibilidade)

## Problema

Os casos de uso devolvem desfechos de negócio — `Registered`, `Replayed`,
`ConflictingPayload`, `AccountNotFound`, `Rejected`. A API precisa traduzi-los
para HTTP. Onde essa tradução vive, e como os erros são representados?

## Decisões

### A tradução vive na borda, não nos casos de uso

Os casos de uso desconhecem HTTP: nenhum deles menciona status, cabeçalho ou
ProblemDetails. A tradução está em `LedgerProblems` e nos endpoints.

**Por quê.** É o que permite que a mesma regra sirva a outra porta de entrada
sem reescrita: ingestão em lote de arquivo, consumidor de fila, job interno de
correção. Se o caso de uso devolvesse `IResult`, qualquer um desses teria que
fingir ser uma requisição HTTP.

O custo é um `switch` por endpoint. Pequeno e explícito.

### `type` em URN, não em URL

```
urn:ledger:problem:idempotency-conflict
```

A convenção favorece uma URL que documente o erro. Não tenho um site de
documentação, e **link morto em contrato de API é pior que identificador
opaco**: quem integra clica, não encontra nada, e perde a confiança no resto do
contrato. A RFC 9457 exige uma referência de URI, não que ela resolva.

Se houver portal de documentação, a troca é mecânica — e aí a URL passa a valer
mais que a URN, porque leva a algum lugar.

### O código do domínio aparece no contrato

Uma recusa por regra devolve `urn:ledger:problem:amount-not-positive`, derivado
do `DomainErrorCode`.

Expor um conceito interno no contrato é normalmente um cheiro. Aqui é
deliberado: **quem integra precisa distinguir os casos sem interpretar texto
livre.** Tratar "valor não positivo" diferente de "moeda divergente" é decisão
de programa, e mensagem em prosa muda — inclusive de idioma. O código é o
contrato estável.

A consequência aceita: renomear um valor do `DomainErrorCode` passa a ser
mudança de contrato público.

### Sem biblioteca de validação

Não há FluentValidation nem DataAnnotations. **O domínio é o validador**, e o
caso de uso devolve o código do invariante violado.

Uma segunda camada de validação significaria duas definições da mesma regra, em
dois lugares, com dois formatos de erro — e a garantia de que uma delas ficaria
desatualizada. O que a borda faz é o que só ela pode fazer: ler o cabeçalho
obrigatório, converter o texto do tipo de movimentação e converter o instante
da consulta.

### A resposta é montada do que foi gravado, não do pedido

Este foi um defeito real, encontrado por um teste que comparava o corpo do
registro com o corpo do replay byte a byte:

| | `201` montado do pedido | `200` lido do banco |
|---|---|---|
| valor | `150.75` | `150.7500` |
| instante | `...55.2611014+00:00` | `...55.261101+00:00` |

O `numeric(19,4)` devolve sempre quatro casas, e o `timestamptz` guarda
microssegundos. **O registro estava afirmando valores que o razão não tinha.**

A correção não foi no contrato, foi na fronteira: `Money` normaliza para a
escala canônica e o instante de negócio é truncado ao microssegundo antes de
virar fato. Agora o objeto em memória, a linha gravada e as duas respostas
concordam — e o caso de uso devolve a movimentação criada, para que o endpoint
renderize o que foi gravado em lugar do que foi pedido.

É a mesma classe de problema do truncamento do relógio em
[ADR-0003](0003-idempotencia-como-contrato.md): valor que atravessa a fronteira
de armazenamento tem que ser normalizado **antes**, nunca depois.

### `201 Created` aponta para um recurso que responde

O `Location` do registro aponta para
`GET /api/v1/accounts/{accountId}/movements/{movementId}`, que existe e devolve
a movimentação. `Location` para endpoint inexistente é contrato quebrado, e é um
erro comum justamente porque ninguém testa o cabeçalho — há teste que segue o
`Location` e compara o corpo.

A leitura verifica que a movimentação pertence à conta do caminho. A verificação
está no caso de uso, e não no endpoint, para valer em qualquer porta de entrada:
adivinhar um identificador não deve dar acesso lateral.

## O achado que custou mais caro

Com **194 testes verdes**, o primeiro `curl` à mão quebrou a API:

```
GET /balance?asOf=2026-11-06T13:00:00+00:00   →   500
```

O `+` cru numa URL é decodificado como espaço, a data fica inválida, e o
ASP.NET lança `BadHttpRequestException`. Essa exceção **já carrega status
400** — mas o middleware de tratamento de exceções não olha esse status e
responde erro de servidor.

Os testes não pegaram porque montavam a URL em código, com
`Uri.EscapeDataString`. Sempre bem formada. É o tipo de defeito que só aparece
quando alguém digita.

**O que fiz, e o que a medição mudou.** Minha primeira hipótese foi um
`IExceptionHandler` que traduzisse `BadHttpRequestException` para o status que
ela carrega. Implementei, e também passei a converter o `asOf` explicitamente
para dar uma mensagem útil — a que diz para codificar o `+` como `%2B`.

Depois escrevi um teste do **requisito**, não do mecanismo: quatro formas de
pedido malformado precisam responder 4xx em ProblemDetails. Com o tratador
desligado, todos os quatro continuaram passando. O tratador havia se tornado
código morto: o corpo JSON inválido já era tratado pelo pipeline de binding, e o
`asOf` deixou de lançar quando passou a ser convertido à mão.

Removi o tratador. Manter código que não consigo provar necessário, num
repositório que seria entregue a um banco, é exatamente o que eu apontaria numa
revisão — e o teste que ficou protege o comportamento, não a minha primeira
hipótese sobre como obtê-lo.

## Trade-offs aceitos

| Custo | Avaliação |
|-------|-----------|
| Um `switch` de tradução por endpoint | explícito e local; o alternativo acopla o caso de uso a HTTP |
| `DomainErrorCode` virou contrato público | renomear um valor é mudança incompatível; em troca, quem integra trata casos sem parsear texto |
| Leitura extra no caminho de replay | só no caminho raro, e é o que torna a resposta idêntica à original |
| URN que não resolve | melhor que URL que promete documentação inexistente |

## Gatilho de revisão

- **Portal de documentação de erros**: as URNs passam a URLs.
- **Mais de um consumidor com contratos divergentes**: aí vale versionar o
  contrato (já há `/api/v1`) em lugar de acomodar os dois no mesmo corpo.
- **Validação que o domínio não pode fazer** — regra que depende de
  configuração por cliente, por exemplo — justificaria uma camada de validação
  na borda. Hoje não existe nenhuma.

## Documentação interativa (Swagger)

O documento OpenAPI continua gerado pelo `Microsoft.AspNetCore.OpenApi`, e o
`Swashbuckle.AspNetCore.SwaggerUI` só o apresenta. Adotar o Swashbuckle inteiro
traria um segundo gerador de documento e uma segunda fonte de verdade sobre o contrato.

- **Somente em desenvolvimento**, testado: em outro ambiente, `/swagger` e
  `/openapi/v1.json` não existem.
- **O documento é público** nesse ambiente (`AllowAnonymous`). Sem isso a política
  padrão da autenticação ([ADR-0016](0016-autenticacao-e-titularidade.md)) o
  responderia com `401`, e uma documentação que exige o token que ensina a obter não
  serve a ninguém. Isso foi uma regressão real da E13, não detectada porque nenhum teste
  cobria o documento.
- **O esquema Bearer e o `Idempotency-Key` são declarados por transformadores**: o
  cabeçalho é lido do `HttpContext` e, sem isso, a interface não teria onde
  preenchê-lo. Um teste confere que toda operação documentada declara o requisito de
  segurança.
