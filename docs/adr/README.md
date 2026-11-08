# Decisões arquiteturais (ADRs)

Cada ADR registra: o problema, as alternativas consideradas, a decisão, a
justificativa técnica e de negócio, os trade-offs aceitos e o **gatilho concreto**
que levaria a revisar a decisão.

O gatilho é a parte que costuma faltar. Sem ele, um ADR registra o que foi
escolhido mas não ajuda quem vier depois a saber se a escolha ainda vale — e é
exatamente essa pessoa que o enunciado do desafio tem em mente quando pergunta o
que seria essencial documentar para que outra pessoa do time evolua a solução.

## Índice

| ADR | Decisão | Status |
|-----|---------|--------|
| [0001](0001-monolito-modular.md) | Monólito modular em lugar de microsserviços | aceito |
| [0002](0002-ledger-append-only.md) | Ledger append-only: saldo derivado, nunca estado mutável | aceito |
| [0003](0003-idempotencia-como-contrato.md) | Idempotência como contrato de primeira classe | aceito |
| [0004](0004-snapshot-de-saldo.md) | Saldo consolidado por snapshot de período fechado mais delta | aceito |
| [0005](0005-persistencia-postgresql.md) | PostgreSQL como persistência | aceito |
| [0006](0006-sem-broker-no-caminho-de-escrita.md) | Sem broker no caminho de escrita; Outbox como evolução | aceito |
| [0007](0007-ordenacao-do-ledger.md) | Ordenação do ledger: global agora, sequência por conta como opção | aceito |
| [0008](0008-resiliencia-e-degradacao-declarada.md) | Resiliência e degradação declarada | aceito |
| [0009](0009-protecao-de-dados-sensiveis.md) | Proteção de dados sensíveis, e LGPD sobre ledger imutável | aceito |
| [0010](0010-observabilidade.md) | Observabilidade com métricas de negócio | aceito |
| [0011](0011-biblioteca-de-assercao.md) | Shouldly em lugar de FluentAssertions | aceito |
| [0012](0012-idempotencia-em-tabela-propria.md) | Chave de idempotência em tabela própria, não em coluna de `movements` | aceito |
| [0013](0013-modelo-de-persistencia-separado.md) | Modelo de persistência separado do modelo de domínio | aceito |
| [0014](0014-xunit-v3-e-testing-platform.md) | xUnit v3 e Microsoft.Testing.Platform | aceito |
| [0015](0015-contrato-http.md) | Contrato HTTP: desfecho para status, ProblemDetails e validação | aceito |

## Por onde começar

Se você tem cinco minutos, leia **[0002](0002-ledger-append-only.md)** e
**[0003](0003-idempotencia-como-contrato.md)**. São as duas decisões das quais
todas as outras decorrem:

- **0002** explica por que o saldo não é uma coluna — e por que essa ausência é
  provavelmente a resposta ao problema de performance e estabilidade descrito no
  enunciado.
- **0003** explica por que idempotência é contrato e não detalhe de
  implementação, num ambiente onde retry é o comportamento normal de todo
  cliente correto.

Depois disso, **[0004](0004-snapshot-de-saldo.md)** é o ADR que paga a dívida que
0002 contraiu de propósito, e **[0006](0006-sem-broker-no-caminho-de-escrita.md)**
e **[0001](0001-monolito-modular.md)** são os dois onde a decisão foi **não**
adotar algo — que é o tipo de decisão que o enunciado pede para justificar.

## As que nasceram da implementação

A 0011, a 0012, a 0013 e a 0014 não estavam planejadas. Surgiram ao implementar,
e cada uma registra uma colisão real entre decisões anteriores ou entre uma
decisão e a ferramenta:

- **[0012](0012-idempotencia-em-tabela-propria.md)** é a mais importante das
  quatro, e revisa a [0003](0003-idempotencia-como-contrato.md): o PostgreSQL
  exige que o unique constraint de tabela particionada inclua a coluna de
  particionamento, o que tornaria o constraint de idempotência **inútil sem
  parecer inútil**.
- **[0013](0013-modelo-de-persistencia-separado.md)** resolve o atrito entre um
  domínio estritamente imutável e um ORM que precisa materializar objetos.
- **[0014](0014-xunit-v3-e-testing-platform.md)** documenta uma descoberta sobre
  a plataforma de testes, e uma lição: um teste que verifica uma constante não
  verifica um comportamento.
- **[0015](0015-contrato-http.md)** registra o contrato da API e dois defeitos
  que só apareceram ao exercitar a aplicação à mão, com 194 testes verdes — mais
  a medição que me fez remover a minha primeira correção por ter virado código
  morto.

## Numeração

As ADRs são identificadas pela ordem em que foram **decididas**, não pela ordem
em que convém ler. A 0011 tem número mais alto que a 0010 e foi escrita antes de
todas, porque a decisão surgiu ao montar a estrutura do projeto — registrá-la na
hora era mais honesto que renumerar depois.
