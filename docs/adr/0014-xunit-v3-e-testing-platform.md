# ADR-0014 — xUnit v3 e Microsoft.Testing.Platform

- **Status:** aceito
- **Data:** 2026-11-07
- **Requisitos relacionados:** RO-02 (testes automatizados), RO-04 (README que roda localmente)

## Problema

Os testes de integração precisam de PostgreSQL. Numa máquina sem Docker eles não
podem rodar — e, por RO-04, `dotnet test` **não pode falhar por isso**: quem
avalia o desafio pode não ter Docker, e 25 falhas vermelhas na primeira execução
sugerem um projeto quebrado.

A solução correta é ignorar esses testes com mensagem explícita. Isso exige
**skip dinâmico**: a decisão depende do ambiente, não é constante de compilação.

## O que eu descobri tentando

O projeto começou no xUnit v2.9.3, e o caminho parecia simples.

**Primeira tentativa: `Assert.Skip`.** Não compila — `Xunit.Assert` do v2 não
tem nenhum método `Skip*`. Confirmei por reflexão no assembly.

**Segunda tentativa: o protocolo interno do v2.** O v2 tem
`Xunit.Sdk.DynamicSkipToken`, um prefixo de mensagem (`$XunitDynamicSkip$`) que
o motor de execução reconhece para marcar um teste como ignorado. É o mecanismo
que o pacote `Xunit.SkippableFact` usa por dentro. O tipo é interno, então usei
a constante literal e escrevi um teste-sentinela que a comparava com o valor
real por reflexão.

Compilou. O sentinela passou. **E não funcionou:** rodando sem banco, os 25
testes apareceram como *falhas*, com o token cru na mensagem, e exit code 1.

O sentinela verificou a constante. Não verificou o comportamento — e só rodar o
cenário de verdade revelou a diferença. É a lição mais útil desta etapa.

## Alternativas consideradas

1. **`Xunit.SkippableFact`** — resolve no v2, com `[SkippableFact]` em cada
   teste. Pacote de terceiro, sem atualização há anos.
2. **Exigir banco** e documentar que `dotnet test` precisa dele.
3. **xUnit v3**, que tem `Assert.Skip` de primeira classe.

## Decisão

xUnit v3 (4.0.2), com Microsoft.Testing.Platform.

## Justificativa

**Alternativa 2 quebra RO-04**, que é requisito obrigatório e eliminatório.

**Alternativa 1 funcionaria**, e seria a escolha se migrar fosse caro. Mas
adicionar uma dependência dormente a um código que seria entregue a um banco,
para ganhar um método de três linhas, é dívida com data de vencimento — o mesmo
raciocínio de [ADR-0011](0011-biblioteca-de-assercao.md).

**Alternativa 3 é a linha suportada**, e `Assert.Skip` resolve o problema sem
truque. Num projeto novo, começar na versão anterior para evitar meia hora de
migração é a escolha errada.

## A descoberta que tornou a decisão obrigatória

A migração ia manter o VSTest no comando, para não mudar runner e CI de uma vez.
Não é possível:

> Testing with VSTest target is no longer supported by Microsoft.Testing.Platform
> on .NET 10 SDK and later.

A partir do SDK .NET 10 o VSTest não coexiste mais com o Microsoft.Testing
Platform, que o xUnit v3 traz. Não há escape por propriedade MSBuild — tentei
`UseMicrosoftTestingPlatformRunner=false` e o build falha com esse erro.

Então a decisão deixou de ser "xUnit v3 ou v2" e passou a ser "MTP agora". O
opt-in fica no `global.json`:

```json
{ "test": { "runner": "Microsoft.Testing.Platform" } }
```

Não em `dotnet.config`, que foi minha primeira tentativa e não é lido para isso.

## Consequências

| Antes (VSTest) | Depois (MTP) |
|----------------|--------------|
| `xunit` + `xunit.runner.visualstudio` + `Microsoft.NET.Test.Sdk` | `xunit.v3` apenas |
| `coverlet.collector` | removido; cobertura seria `Microsoft.Testing.Extensions.CodeCoverage` |
| projeto de teste é biblioteca | projeto de teste é executável, com o runner embutido |
| relatório com parâmetro de logger `trx` | relatório do próprio app de teste, depois do separador de argumentos |
| — | opt-in obrigatório no `global.json` |

O CI mudou junto: o diretório de resultados é opção do `dotnet test`, e o
relatório (CTRF, que o xUnit v3 traz nativamente) vem depois do `--`. Deixei de
usar TRX para não adicionar o pacote de extensão só por causa do formato.

O analisador do v3 trouxe `xUnit1051`, que exige passar
`TestContext.Current.CancellationToken` às chamadas assíncronas. Apliquei nas 17
chamadas em lugar de suprimir: um teste de integração pendurado numa conexão que
não responde travaria o job do CI até o tempo limite, e a regra existe para
evitar isso.

## Gatilho de revisão

Nenhum previsto. Se a cobertura de código passar a ser exigida, a extensão de
cobertura do MTP entra; é adicionar um pacote, não mudar de plataforma.
