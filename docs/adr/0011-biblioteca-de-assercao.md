# ADR-0011 — Biblioteca de asserção: Shouldly em lugar de FluentAssertions

- **Status:** aceito
- **Data:** 2026-11-07
- **Contexto da decisão:** montagem da estrutura do projeto, ao definir as dependências de teste

## Problema

Os testes precisam de asserções legíveis, com mensagem de falha que explique o
que deu errado sem obrigar quem lê o relatório a abrir o código. No ecossistema
.NET, o padrão de fato é o FluentAssertions.

## Alternativas consideradas

1. **FluentAssertions 8.x** — padrão de mercado, API mais rica, mais familiar
   para a maioria dos times .NET.
2. **FluentAssertions 7.x** — última versão sob Apache-2.0.
3. **Shouldly 4.x** — BSD-3-Clause, API fluente equivalente para o que este
   projeto precisa.
4. **`Assert` nativo do xUnit** — dependência zero.

## Decisão

Shouldly 4.3.0.

## Justificativa

A partir da versão 8, o FluentAssertions passou a ser licenciado pela Xceed e
**exige licença paga para uso comercial**.

Este código é um desafio técnico para um banco. Se a solução fosse adotada, ela
entraria num contexto comercial, e a dependência viraria uma obrigação de compra
herdada de uma escolha que ninguém revisou. Isso não é trade-off técnico, é
passivo jurídico — e é o tipo de coisa que só aparece quando o jurídico faz
auditoria de dependências, muito depois de a decisão ter sido tomada.

**Alternativa 2** resolveria a licença, mas me deixaria numa versão que não
recebe mais correção: dívida técnica com data marcada.

**Alternativa 4** seria aceitável, mas as mensagens de falha são pobres o
suficiente para que, num teste de concorrência com mil movimentações, a saída
não diga o que divergiu — e esse é justamente o teste em que eu mais preciso de
uma boa mensagem.

Shouldly é permissivo, mantido e cobre o que este projeto usa.

## Consequências

- Quem conhece FluentAssertions precisa de alguns minutos de adaptação:
  `valor.Should().Be(x)` passa a ser `valor.ShouldBe(x)`. Custo baixo e único.
- Menos recursos avançados de comparação de grafo de objetos. Se um teste futuro
  precisar, a comparação explícita é aceitável e costuma ser mais legível.
- Nenhuma obrigação de licença comercial no repositório.

## Gatilho de revisão

Se o banco já tiver licença corporativa do FluentAssertions, a decisão se
inverte: a familiaridade do time passa a valer mais que a diferença de licença,
e a migração é mecânica.
