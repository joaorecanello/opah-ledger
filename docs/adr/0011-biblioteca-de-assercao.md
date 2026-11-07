# ADR-0011 - Biblioteca de assercao: Shouldly em lugar de FluentAssertions

- **Status:** aceito
- **Data:** 2026-11-07
- **Contexto da decisao:** etapa E03 (scaffold), ao definir as dependencias de teste

## Problema

Os testes precisam de assercoes legiveis, com mensagem de falha que explique o
que deu errado sem obrigar quem le o relatorio a abrir o codigo. No ecossistema
.NET o padrao de fato e o FluentAssertions.

## Alternativas consideradas

1. **FluentAssertions 8.x** - padrao de mercado, API mais rica, mais familiar
   para a maioria dos times .NET.
2. **FluentAssertions 7.x** - ultima versao sob Apache-2.0.
3. **Shouldly 4.x** - BSD-3-Clause, API fluente equivalente para o que este
   projeto precisa.
4. **Assert nativo do xUnit** - zero dependencia.

## Decisao

Shouldly 4.3.0.

## Justificativa

A partir da versao 8, o FluentAssertions passou a ser licenciado pela Xceed e
**exige licenca paga para uso comercial**. Este codigo e um desafio tecnico para
um banco: se a solucao fosse adotada, ela entraria num contexto comercial, e a
dependencia viraria uma obrigacao de compra herdada de uma escolha feita sem
que ninguem percebesse. Isso nao e trade-off tecnico, e passivo juridico.

Ficar na versao 7 (alternativa 2) resolveria a licenca, mas me deixaria numa
versao que nao recebe mais correcao - divida tecnica com data marcada.

O Assert nativo (alternativa 4) seria aceitavel, mas as mensagens de falha sao
pobres o suficiente para que, num teste de concorrencia com mil movimentacoes,
a saida nao diga o que divergiu.

Shouldly e permissivo, mantido, e cobre o que este projeto usa.

## Consequencias

- Quem conhece FluentAssertions precisa de alguns minutos de adaptacao:
  `valor.Should().Be(x)` passa a ser `valor.ShouldBe(x)`. Custo baixo e unico.
- Menos recursos avancados de comparacao de grafo de objetos. Se um teste
  futuro precisar, a comparacao explicita e aceitavel e mais legivel.
- Nenhuma obrigacao de licenca comercial no repositorio.

## Gatilho de revisao

Se o banco ja tiver licenca corporativa do FluentAssertions, a decisao se
inverte: a familiaridade do time passa a valer mais que a diferenca de licenca,
e a migracao e mecanica.
