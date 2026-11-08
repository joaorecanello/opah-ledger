# ADR-0009 — Proteção de dados sensíveis, e LGPD sobre um ledger imutável

- **Status:** aceito
- **Data:** 2026-11-07
- **Requisitos relacionados:** RNF-05 (proteção de dados sensíveis), RNF-04

## Problema

O enunciado destaca que se trata de dados financeiros sensíveis e pede
explicitamente como protegê-los.

Há uma segunda pergunta embutida, que só aparece depois de
[ADR-0002](0002-ledger-append-only.md): **um ledger imutável é compatível com o
direito de eliminação da LGPD?**

## Decisão: minimização antes de criptografia

A primeira linha de defesa não é controle de acesso nem criptografia. É **não
ter o dado.**

| O ledger guarda | O ledger não guarda |
|-----------------|---------------------|
| identificador de conta | CPF |
| identificador de cliente (opaco) | nome, endereço, telefone, e-mail |
| valor, tipo, instantes | data de nascimento |
| referência externa, descrição | qualquer documento de identificação |

Dado pessoal vive no contexto de clientes; o razão referencia por identificador.

Isso é decisão arquitetural, não configuração: um vazamento da base do ledger
expõe movimentações associadas a identificadores opacos, não dossiês de pessoas.
E reduz a superfície de obrigação de LGPD sobre este sistema, em vez de exigir
controles mais caros sobre dado que não precisava estar aqui.

## Camadas de proteção

| Camada | Decisão |
|--------|---------|
| Transporte | TLS obrigatório. Localmente a API sobe em HTTP puro por conveniência de quem avalia; em produção o TLS termina antes da aplicação |
| Autenticação | (implementada no [ADR-0016](0016-autenticacao-e-titularidade.md)) OAuth2 *client credentials* com JWT. O consumidor é sistema interno ([PR-08](../premissas.md#pr-08)), não usuário final |
| Autorização | escopos `ledger:write` e `ledger:read`, **mais** verificação de titularidade da conta no caso de uso |
| Repouso | criptografia no volume de dados e em backup |
| Observabilidade | redação por política: valor, descrição e identificadores mascarados por padrão |
| Auditoria | o próprio ledger é a trilha dos fatos; acesso a dados é logado separadamente |

### Por que autorização no caso de uso e não só no endpoint

Escopo no endpoint responde "este cliente pode ler saldos?". Não responde "este
cliente pode ler o saldo **desta** conta?".

A segunda é a pergunta que impede acesso lateral, e ela depende de dado de
domínio — a titularidade. Verificá-la no caso de uso significa que ela vale para
qualquer entrada: endpoint HTTP novo, consumo em lote, job interno. Autorização
que vive só na borda é autorização que a próxima porta de entrada esquece.

### Por que a descrição livre é tratada como dado sensível

É o vetor mais provável de PII acidental neste sistema. O campo existe para o
operador descrever a movimentação, e alguém inevitavelmente vai escrever
"transferência para João da Silva, CPF 123..." nele.

Não posso impedir pelo esquema sem perder a utilidade do campo. Então ele é
tratado como se contivesse PII: criptografado em repouso, redigido em log,
nunca em query string. E nunca em URL, porque query string vai para log de
proxy, de CDN e de histórico de navegador — lugares onde o controle de acesso do
banco de dados não alcança.

## O ponto difícil: imutabilidade contra o direito de eliminação

A LGPD dá ao titular o direito de eliminação (art. 18, VI). O ledger é
append-only por decisão arquitetural. As duas coisas parecem incompatíveis.

**Elas não são, e a resolução não é apagar.**

O art. 16 da LGPD prevê conservação de dados para **cumprimento de obrigação
legal ou regulatória** — e registro contábil de instituição financeira é
exatamente isso, com prazo de retenção definido em norma. A obrigação legal de
retenção prevalece sobre o pedido de eliminação.

O que se faz, então:

1. **Anonimizar o vínculo.** O identificador de conta deixa de resolver para uma
   pessoa, no contexto de clientes.
2. **Preservar o fato financeiro.** O lançamento continua no razão, íntegro, com
   o identificador agora não-identificante.
3. **Registrar o atendimento** do pedido.

O resultado é que a contabilidade fecha e a pessoa não é mais identificável a
partir deste sistema.

**Apagar uma linha de ledger não é opção** — e saber *por que* não é a parte que
importa: não é teimosia técnica, é que o registro contábil não é dado do
titular que a instituição guarda por conveniência, é obrigação dela perante o
regulador.

## Trade-offs aceitos

| Custo | Avaliação |
|-------|-----------|
| Consulta precisa cruzar contextos para exibir nome do cliente | é o preço da minimização, e é barato: uma chamada ao contexto de clientes |
| Criptografia em repouso tem custo de I/O | irrelevante frente ao requisito |
| Redação de log dificulta depuração | mitigado por correlation id: dá para seguir o fluxo inteiro sem ver o valor |
| JWT local com chave simétrica não é produção | documentado; existe para quem avalia conseguir chamar a API ([PR-09](../premissas.md#pr-09)) |

A terceira linha é um trade-off que vale explicitar: num incidente, alguém vai
querer ver o valor da movimentação no log. A resposta correta é consultar a base
com credencial auditada, não ter o valor espalhado em arquivo de log replicado
para um agregador.

## Gatilho de revisão

- Se o ledger passar a receber dado pessoal direto — por exigência de integração
  —, a minimização quebra e passa a ser necessário *tokenization* ou
  criptografia em nível de campo com chave por titular.
- Se o consumidor passar a incluir aplicativo de cliente final
  ([PR-08](../premissas.md#pr-08)), a autenticação muda de *client credentials*
  para fluxo com usuário, e a autorização por titularidade passa a ser a defesa
  principal e não a secundária.
