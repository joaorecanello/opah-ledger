# ADR-0016 — Autenticação por JWT, escopos e titularidade da conta

- **Status:** aceito
- **Data:** 2026-11-08
- **Requisitos relacionados:** RNF-05 (proteção de dados sensíveis)
- **Refina:** [ADR-0009](0009-protecao-de-dados-sensiveis.md) (a decisão de desenho; este registra o que foi implementado e onde se desviou)

## Problema

Até esta etapa a API não autenticava nada: qualquer requisição registrava
movimentação em qualquer conta. As perguntas são três, e são diferentes:

1. **Quem está chamando?** (autenticação)
2. **O que essa credencial pode fazer?** (escopo: ler, escrever)
3. **Em que contas?** (titularidade)

A terceira é a que costuma faltar. Escopo responde "pode ler saldos?"; não
responde "pode ler o saldo **desta** conta?". Sem ela, qualquer chamador com
`ledger:read` troca o identificador na URL e lê a conta de outro — o IDOR
(*insecure direct object reference*), a falha de autorização mais comum em API.

## Decisão

**JWT Bearer, validado localmente**, com:

| Aspecto | Decisão |
|---------|---------|
| Algoritmo | só HS256 (`ValidAlgorithms`), emissor, destinatário, expiração e assinatura obrigatórios |
| Escopos | claim `scope` (espaços): `ledger:read`, `ledger:write` |
| Alcance | claim `customer_id` restringe o token às contas daquele cliente; escopo `ledger:cross-customer` concede acesso a qualquer conta |
| Padrão | **negar**: política *fallback* exige token válido; rotas públicas (`/health/*`) declaram `AllowAnonymous`; toda rota de `/api` declara política de escopo (verificado por teste) |
| Titularidade | decidida **no caso de uso** (`AccessScope`), não no endpoint |
| Configuração | `Ledger:Auth:{Issuer,Audience,SigningKey}`; **sem chave válida a aplicação não sobe** |

### Titularidade no caso de uso

`RegisterMovementHandler`, `GetBalanceHandler`, `GetMovementHandler` e
`OpenAccountHandler` recebem um `ICallerAccess` **sem valor padrão** no construtor.
A verificação vale para qualquer porta de entrada futura (lote, fila, job), e não
só para a que alguém lembrou de proteger. Um contexto que não informe o chamador
não compila nem é resolvido pelo contêiner: negar é o padrão por construção.

### Conta alheia responde como conta inexistente

Para leituras e registros, uma conta de outro titular responde **exatamente** como
uma conta que não existe (404, mesmo `type`, mesmo `detail` exceto o identificador
pedido). Diferenciar (403 × 404) transformaria a API num oráculo para descobrir
quais identificadores existem. Um teste compara as duas respostas campo a campo.

A exceção é a abertura de conta para outro cliente: aí o `403` não revela nada, porque
o chamador **informou** o cliente em vez de descobri-lo.

### Token sem alcance definido é recusado, não tratado como irrestrito

Um token com `ledger:read` e sem `customer_id` nem `ledger:cross-customer` recebe
`403`. O contrário — ausência de restrição virando permissão total — transformaria
um esquecimento do emissor em acesso a todas as contas. Se o token traz as duas
coisas, vale a mais restritiva.

### Idempotência e titularidade

A chave de idempotência é global. Se Maria descobrir a chave de João e a repetir
contra a conta **dele**, a resposta original (valor, identificador) não pode
vazar. A titularidade é verificada **antes** de qualquer consulta à chave, e um
teste cobre exatamente esse cenário.

## Alternativas consideradas

| Alternativa | Por que não |
|-------------|-------------|
| Chave de API estática | Não carrega escopo nem cliente, não expira, e rotacionar significa redeploy. |
| Servidor de identidade no `docker-compose` (Keycloak etc.) | Um serviço pesado só para o avaliador subir a solução, e uma superfície de configuração que não é o assunto do desafio. |
| JWT assimétrico (RS256/ES256) com JWKS | **É o desenho de produção.** Não foi implementado: sem um emissor real, haveria uma chave privada no repositório ou um emissor falso que ninguém usaria. Ver "Concessão" abaixo. |
| Autorização só no endpoint | Protege a porta que alguém lembrou de proteger. A próxima entrada nasce aberta. |
| 403 para conta de outro titular | Permite enumerar contas existentes. |

## Concessão consciente: chave simétrica

A API valida com **chave simétrica** (HS256). Quem valida também poderia emitir:
vazar a chave da API significa poder forjar tokens. Em produção o emissor é o
provedor de identidade corporativo, a API conhece só a chave **pública** (JWKS) e
o vazamento da API não permite emitir nada. Foi escolhido assim para que quem avalia
o desafio suba tudo com um comando, e está registrado aqui e em
[LIMITATIONS.md](../../LIMITATIONS.md).

A chave padrão do `docker-compose.yml` e do `appsettings.Development.json` é
**pública** (está no repositório) e serve a ambiente local, e a nada mais.
`tools/Ledger.DevToken` emite tokens com ela.

## O que a medição achou

- Os **testes de rejeição** cobrem token ausente, malformado, assinado com outra
  chave, expirado, de outro emissor, de outro destinatário, **sem assinatura**
  (`alg: none`) e assinado com a mesma chave por outro algoritmo (HS512) — todos `401`.
- O **teste de mutação** da regra de titularidade (trocar a comparação por uma
  que sempre permite) derruba 9 testes, nos dois níveis (caso de uso e HTTP).
  Uma primeira tentativa não compilou, e os testes rodaram o binário antigo e
  passaram — armadilha já conhecida; a mutação só vale com o build confirmado.
- O token **não aparece em nenhuma linha de log**, nem partes dele, num fluxo de
  falha de validação.

## Consequências

- Toda chamada de negócio exige `Authorization: Bearer`. Os exemplos do README
  passam a incluí-lo.
- O ledger **não registra quem** fez cada movimentação — só o `correlation_id`. A
  identidade do chamador (`sub`) está no token e fora do fato gravado.
  Ver [LIMITATIONS.md](../../LIMITATIONS.md).
- Sem revogação: um token vale até expirar. Mitigação: validades curtas.

## Gatilho de revisão

- **Qualquer implantação real:** trocar a chave simétrica por validação de JWKS do
  provedor de identidade (`Authority`), sem alterar as políticas nem os casos de uso.
- Se o consumidor passar a ser o **cliente final** (e não sistemas internos, [PR-08](../premissas.md#pr-08)),
  o fluxo muda para OIDC com *authorization code*, e a titularidade deixa de ser um
  claim de cliente e passa a ser uma consulta ao contexto de Clientes.
- Se for exigido saber **quem** registrou cada movimentação, gravar o `sub` junto
  do `correlation_id`.
