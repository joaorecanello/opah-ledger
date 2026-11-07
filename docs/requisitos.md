# Requisitos

Decomposição do enunciado do desafio. A coluna **Fonte** cita o texto original;
onde não há citação, o item é recomendação minha e está marcado como tal.

A separação importa: confundir boa prática desejável com exigência obrigatória
é o jeito mais rápido de gastar o prazo no lugar errado.

## Requisitos funcionais

| ID | Requisito | Fonte (enunciado) |
|----|-----------|-------------------|
| RF-01 | Registrar movimentações financeiras dos clientes (créditos e débitos) em suas contas | "Registre as movimentações financeiras dos clientes (créditos e débitos) em suas contas" |
| RF-02 | Consultar a posição consolidada (saldo) de um cliente em um determinado momento | "Permita consultar a posição consolidada (saldo) de um cliente em um determinado momento" |

São apenas dois, e isso é informação. O enunciado diz explicitamente: "use seu
tempo para demonstrar como você pensa e prioriza, não para implementar tudo".
A avaliação não premia quantidade de funcionalidade.

## Requisitos não funcionais

| ID | Requisito | Fonte (enunciado) |
|----|-----------|-------------------|
| RNF-01 | Operar de forma confiável sob instabilidade | "Continue operando de forma confiável mesmo em cenários de instabilidade" |
| RNF-02 | Operar sob alta demanda | "...alta demanda..." |
| RNF-03 | Tolerar falhas parciais | "...ou falhas parciais" |
| RNF-04 | Consistência de dados — qualquer inconsistência gera impacto direto ao cliente e à operação | "considerando que se trata de dados financeiros sensíveis e que qualquer inconsistência gera impacto direto ao cliente e à operação do banco" |
| RNF-05 | Proteger dados sensíveis dos clientes | "Como proteger dados sensíveis dos clientes" |
| RNF-06 | Ser compreensível e evoluível por outra pessoa do time | "o que você consideraria essencial documentar para que outra pessoa do time entenda e evolua sua solução" |
| RNF-07 | Tratar consistência, concorrência e cenários de falha | "Como tratar consistência de dados, concorrência e cenários de falha" |

### RNF-04 é o requisito dominante

Quando consistência e disponibilidade entrarem em conflito no caminho de
escrita, **consistência ganha**. Isso não é posição ideológica: o enunciado diz
que qualquer inconsistência tem impacto direto no cliente, e num razão contábil
um saldo errado é pior que uma escrita recusada — a escrita recusada o chamador
percebe e repete, o saldo errado ninguém percebe até a conciliação.

A consequência prática está em [ADR-0008](adr/0008-resiliencia-e-degradacao-declarada.md):
sob indisponibilidade do banco de dados, a escrita falha rápido com `503` em vez
de enfileirar localmente.

## Restrições obrigatórias

O enunciado é explícito: "Caso os requisitos obrigatórios acima não sejam
minimamente atendidos, o teste será desconsiderado." São eliminatórias.

| ID | Restrição | Como está atendida |
|----|-----------|--------------------|
| RO-01 | Implementação em C# | .NET 10 / C# 14 |
| RO-02 | Testes automatizados | xUnit; ver [testes.md](testes.md) |
| RO-03 | Código em repositório público no GitHub | [joaorecanello/opah-ledger](https://github.com/joaorecanello/opah-ledger) |
| RO-04 | README com instruções claras de execução local | [README.md](../README.md), validado em ambiente limpo |
| RO-05 | Toda a documentação no próprio repositório | esta pasta `docs/`; nenhuma dependência externa |
| RO-06 | Código fonte deve compilar sem erros e warnings | tratado por construção; ver abaixo |

### RO-06 merece engenharia, não disciplina

É a restrição mais fácil de violar por acidente — um SDK diferente, um
analisador novo, um `nullable` esquecido — e a mais objetivamente verificável
por quem avalia. Depender de eu lembrar de conferir na véspera é o pior desenho
possível para um requisito eliminatório.

| Mecanismo | O que garante |
|-----------|---------------|
| `TreatWarningsAsErrors` em `Directory.Build.props` | o build recusa compilar fora do padrão |
| `AnalysisLevel` fixado em `10.0`, não em `latest` | congela o conjunto de regras: um SDK mais novo na máquina de quem avalia não traz regras novas que virariam erro |
| `global.json` com `rollForward: latestFeature` | qualquer SDK .NET 10 compila o projeto |
| `Directory.Packages.props` | versões de pacote centralizadas; divergência entre projetos é impossível |
| `dotnet format --verify-no-changes` no CI | nem toda regra do `.editorconfig` é reportada por `dotnet build`; sem este passo, build local e CI discordariam |
| CI em runner limpo | evidência pública e auditável, não a palavra do meu terminal |

A combinação de SDK tolerante com conjunto de regras congelado é deliberada e
parece contraditória à primeira leitura. É o ponto: quem avalia compila com o
SDK que tiver, e as regras que decidem o que é warning não mudam com ele.

## Liberdades concedidas explicitamente

O enunciado autoriza definir seis coisas. Cada uma tem um documento
correspondente, para que quem procurar o tema encontre:

| Liberdade concedida (texto do enunciado) | Onde está respondida |
|------------------------------------------|----------------------|
| "Como estruturar a solução (uma ou mais aplicações, camadas, módulos, etc.)" | [arquitetura.md](arquitetura.md), [ADR-0001](adr/0001-monolito-modular.md) |
| "Quais tecnologias, frameworks e padrões utilizar e por quê" | [adr/](adr/) |
| "Como tratar consistência de dados, concorrência e cenários de falha" | [ADR-0002](adr/0002-ledger-append-only.md), [ADR-0003](adr/0003-idempotencia-como-contrato.md), [ADR-0007](adr/0007-ordenacao-do-ledger.md) |
| "Como a solução se comportaria sob alta demanda ou indisponibilidade parcial" | [ADR-0004](adr/0004-snapshot-de-saldo.md), [ADR-0008](adr/0008-resiliencia-e-degradacao-declarada.md) |
| "Como proteger dados sensíveis dos clientes" | [ADR-0009](adr/0009-protecao-de-dados-sensiveis.md) |
| "O que você consideraria essencial documentar..." | esta pasta inteira; critério no fim deste documento |

## Rastreabilidade: requisito → decisão → verificação

| Requisito | Decisão que o endereça | Como é verificado |
|-----------|------------------------|-------------------|
| RF-01 | [ADR-0002](adr/0002-ledger-append-only.md) ledger append-only | testes de registro de movimentação (E06) |
| RF-02 | [ADR-0004](adr/0004-snapshot-de-saldo.md) snapshot + delta | teste de equivalência snapshot ≡ cálculo direto (E10) |
| RNF-01 | [ADR-0008](adr/0008-resiliencia-e-degradacao-declarada.md) | teste de readiness degradando com banco fora (E11) |
| RNF-02 | [ADR-0004](adr/0004-snapshot-de-saldo.md), [ADR-0007](adr/0007-ordenacao-do-ledger.md) | teste de carga leve (E14) |
| RNF-03 | [ADR-0008](adr/0008-resiliencia-e-degradacao-declarada.md) | teste de falha de commit (E06) |
| RNF-04 | [ADR-0002](adr/0002-ledger-append-only.md), [ADR-0003](adr/0003-idempotencia-como-contrato.md) | teste de 1.000 escritas concorrentes com soma exata (E14) |
| RNF-05 | [ADR-0009](adr/0009-protecao-de-dados-sensiveis.md) | testes de autenticação e de redação de log (E13) |
| RNF-06 | esta documentação, [ADR-0001](adr/0001-monolito-modular.md) | testes de arquitetura, que já rodam |
| RNF-07 | [ADR-0002](adr/0002-ledger-append-only.md), [ADR-0003](adr/0003-idempotencia-como-contrato.md), [ADR-0007](adr/0007-ordenacao-do-ledger.md) | testes de concorrência e idempotência |
| RO-06 | higiene de build acima | CI verde em runner limpo |

A coluna da direita é a que importa. Decisão arquitetural sem verificação
automática é intenção, não garantia — e intenção não sobrevive à primeira
sprint sob pressão.

## O que ficou fora de escopo, por decisão

Nenhum destes itens aparece no enunciado. Estão listados porque omissão
justificada é decisão, enquanto omissão silenciosa parece lacuna.

| Fora de escopo | Por quê |
|----------------|---------|
| Cadastro de clientes e contas | outro contexto delimitado; ver [PR-07](premissas.md#pr-07) |
| Autenticação de usuário final | o consumidor é sistema interno; ver [PR-08](premissas.md#pr-08) |
| Front-end | o enunciado pede uma solução de registro e consulta, não interface |
| Infraestrutura como código | não exigido, e IaC que não roda não é avaliável; ver [PR-10](premissas.md#pr-10) |
| Migração do sistema legado | ver [PR-11](premissas.md#pr-11): estratégia documentada, não implementada |
| Dupla entrada contábil completa | evolução registrada; o enunciado pede saldo por conta, não balanço entre contas |

## Critério de documentação

O enunciado pergunta o que seria essencial documentar para outra pessoa
entender e evoluir a solução (RNF-06). O critério que apliquei:

**Documento o que não se descobre lendo o código.** A estrutura das camadas se
lê no código. Já o motivo de o saldo não ser uma coluna, as alternativas que
rejeitei, e o gatilho concreto que me faria mudar de ideia — isso não está em
lugar nenhum a não ser num ADR.

É também por isso que cada ADR aqui tem uma seção de **gatilho de revisão**.
Sem ela, um ADR registra a escolha mas não ajuda quem vier depois a saber se
ela ainda vale.
