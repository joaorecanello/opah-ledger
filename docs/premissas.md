# Premissas

Nada nesta página está no enunciado do desafio. Cada item é uma lacuna real que
precisei fechar para poder decidir, com a premissa que adotei, o impacto se ela
estiver errada e a pergunta que eu levaria ao banco.

Registrar isso explicitamente é parte da entrega. Premissa escondida dentro do
código vira requisito fantasma: alguém a herda sem saber que foi uma escolha.

A premissa mais arriscada de todas é a [PR-03](#pr-03). Se ela cair, o desenho
de concorrência muda materialmente — e é por isso que ela está escrita aqui e
não enterrada numa classe.

---

## PR-01 — Prazo não informado

**Lacuna.** O enunciado não estabelece prazo.

**Premissa.** 6 dias corridos, cerca de 26 horas efetivas.

**Impacto.** Define onde o corte acontece. O plano fecha o escopo obrigatório no
quarto dia de seis, deixando um terço de reserva — deliberado, porque em desafio
técnico o risco maior não é escopo de menos, é entrega que não roda na máquina
de quem avalia na véspera.

**Se o prazo real fosse 48 horas.** Entregaria o fluxo principal mais esta
documentação, e o restante como roadmap justificado. A documentação de decisão é
obrigatória no meu plano justamente porque não depende do volume de código.

---

## PR-02 — Volume e escala não informados

**Lacuna.** Nenhum número de clientes, movimentações por segundo ou retenção.

**Premissa.** 10 milhões de contas; pico de 2.000 movimentações/s e 10.000
consultas/s; retenção de 5 anos por obrigação regulatória.

**Impacto.** É o que dimensiona o snapshot de saldo e o particionamento. Sem
ordem de grandeza, "escalável" não quer dizer nada.

**Se a escala real fosse dez vezes menor.** O snapshot de
[ADR-0004](adr/0004-snapshot-de-saldo.md) passaria a ser complexidade sem
retorno, e o cálculo direto bastaria. É exatamente por isso que o cálculo direto
é o que entra primeiro, e o snapshot vem depois, com teste provando que não
mudou nenhum número.

**Pergunta ao banco.** Qual o pico histórico de movimentações por segundo, e
qual a distribuição entre escrita e consulta? Qual a obrigação de retenção?

---

## PR-03 — O ledger valida saldo negativo?

**Lacuna.** O enunciado não menciona limite, cheque especial nem recusa de
débito a descoberto.

**Premissa.** **O ledger não valida saldo.** Ele é sistema de registro, não de
autorização: a decisão de autorizar o débito pertence a quem origina a
movimentação.

**Por que esta leitura.** O enunciado descreve um sistema que "registra as
movimentações" e "fornece a posição consolidada". É vocabulário de razão
contábil, não de autorizador de pagamento. E é como a maioria das arquiteturas
bancárias separa as responsabilidades: quem autoriza decide, o razão registra o
fato consumado.

**Impacto se estiver errada.** Grande. Se o ledger tiver que recusar débito a
descoberto, surge um invariante por conta — "a soma não pode ficar negativa" — e
invariante exige fronteira de consistência. Isso significa serializar escritas
por conta, via concorrência otimística com versão ou reserva prévia de saldo, e
reintroduz a contenção por conta que [ADR-0002](adr/0002-ledger-append-only.md)
eliminou.

**Mitigação adotada.** O campo de versão por conta já está previsto no modelo,
para que habilitar o invariante seja mudança localizada e não redesenho. A
premissa está documentada, não presumida.

**Pergunta ao banco.** O registro da movimentação pode recusar por saldo
insuficiente, ou essa decisão já foi tomada antes de chegar aqui? Existe
exigência regulatória de impedir saldo negativo no razão?

---

## PR-04 — "Em um determinado momento" é tempo de negócio ou de registro?

**Lacuna.** RF-02 pede saldo "em um determinado momento", sem dizer qual
relógio.

**Premissa.** Bitemporal. A movimentação guarda os dois instantes:

- `occurredAt` — quando o fato aconteceu no mundo (tempo de negócio);
- `postedAt` — quando o sistema o registrou (tempo de sistema), definido pelo
  servidor e nunca pelo cliente.

A consulta `asOf` usa `occurredAt` por padrão. O limite é **inclusivo**: uma
movimentação cujo `occurredAt` é exatamente o instante consultado entra no
saldo.

**Por que os dois.** São perguntas diferentes e ambas legítimas. "Qual era meu
saldo na sexta à noite" é tempo de negócio. "O que o sistema sabia na sexta à
noite" é tempo de sistema, e é a pergunta de auditoria e de investigação de
incidente. Guardar só um fecha metade das perguntas para sempre.

**Impacto.** Movimentação retroativa — `occurredAt` anterior a snapshots já
calculados — torna esses snapshots obsoletos. É a razão de o snapshot precisar
ser invalidável, tratado em [ADR-0004](adr/0004-snapshot-de-saldo.md). É a parte
mais sutil do sistema e a que eu mais testaria antes de produção, porque um erro
aqui produz saldo histórico errado sem erro visível.

**Pergunta ao banco.** Movimentação retroativa existe? Com que atraso máximo?
Quando o extrato do cliente diverge do fechamento contábil, qual dos dois é a
referência?

---

## PR-05 — Multimoeda

**Lacuna.** Não mencionada.

**Premissa.** Moeda única, BRL. Mas o valor monetário é um objeto de valor que
carrega a moeda, e operação entre moedas diferentes é rejeitada no domínio.

**Por que carregar a moeda desde já.** É barato manter correto do início e caro
fazer retrofit: adicionar moeda depois significa reinterpretar todo o histórico
já gravado, e não há como saber retroativamente em que moeda um número estava.

---

## PR-06 — Estorno e cancelamento

**Lacuna.** Não mencionados.

**Premissa.** Existem, pela natureza do domínio — mas **nunca por mutação**.
Estorno é movimentação compensatória que referencia a original.

**Por quê.** Alterar ou apagar uma linha de razão destrói a trilha de auditoria,
que é o ativo principal de [ADR-0002](adr/0002-ledger-append-only.md). O custo é
mais cerimônia na correção de erro operacional; o ganho é que o histórico nunca
mente.

---

## PR-07 — Cadastro de conta e cliente está no escopo?

**Lacuna.** O enunciado fala de contas de clientes, sem pedir o cadastro delas.

**Premissa.** Fora de escopo. Contas são pré-existentes, de outro contexto
delimitado. Exponho um endpoint mínimo de provisionamento apenas para o projeto
ser executável e testável.

**Impacto.** Evita inflar a entrega com CRUD que não é avaliado e não demonstra
nada sobre o problema real.

---

## PR-08 — Quem consome a API?

**Lacuna.** Não dito.

**Premissa.** Sistemas internos do banco, comunicação servidor a servidor. Não é
o aplicativo do cliente final.

**Impacto.** Define autenticação como OAuth2 *client credentials* com JWT por
escopo, e não login de usuário com sessão. Ver
[ADR-0009](adr/0009-protecao-de-dados-sensiveis.md).

---

## PR-09 — Existe provedor de identidade corporativo?

**Lacuna.** Não dito.

**Premissa.** Sim, mas para execução local uso JWT com chave simétrica e um
token de teste documentado no README.

**Por quê.** Quem avalia precisa conseguir chamar a API autenticada sem
provisionar um Keycloak. Exigir isso transformaria o requisito obrigatório de
"README com instruções claras" num obstáculo.

---

## PR-10 — Cloud alvo e infraestrutura como código

**Lacuna.** Não exigidos.

**Premissa.** Entrego `Dockerfile` e `docker compose`; o desenho de implantação
fica documentado, sem Terraform.

**Por quê.** IaC que não é executada e validada não é avaliável, e consumiria
tempo que vale mais no fluxo principal.

---

## PR-11 — Migração do sistema legado

**Lacuna.** O enunciado pede para repensar a solução do zero, sem falar de
migração.

**Premissa.** Fora de escopo de implementação, mas a estratégia está
documentada, porque "do zero" num banco real nunca significa desligar o legado
numa sexta-feira.

**Estratégia.** Coexistência: o novo sistema passa a receber escrita em
paralelo; o histórico entra por saldo inicial mais replay das movimentações; e
um período de *shadow read* compara os dois saldos com métrica de divergência.
O legado só sai depois de divergência zero sustentada.

---

## PR-12 — "Posição consolidada ao longo do tempo" é ponto ou série?

**Lacuna.** RF-02 pede saldo "em um determinado momento", mas o contexto do
enunciado fala de posição "ao longo do tempo".

**Premissa.** A leitura literal de RF-02 é ponto no tempo, e é o que entra
primeiro. A série histórica diária entra como diferencial, depois.

**Impacto.** Baixo: com o snapshot diário de
[ADR-0004](adr/0004-snapshot-de-saldo.md), a série é praticamente de graça —
ela é a própria tabela de snapshots.

---

## PR-13 — O que a situação da conta impede

**Lacuna.** O enunciado não fala de conta bloqueada ou encerrada, mas conta sem
situação não existe num banco real.

**Premissa.** São duas situações com efeitos diferentes sobre o registro:

| Situação | Aceita registro? |
|----------|------------------|
| `Active` | sim |
| `Blocked` | **sim** |
| `Closed` | não |

**Por que bloqueio aceita registro.** Bloqueio impede **originar** operação, não
**registrar** fato que já aconteceu. Um estorno de operação anterior, ou um
lançamento em trânsito no momento em que o bloqueio entrou em vigor, precisa
entrar no razão — recusá-lo faria a contabilidade divergir da realidade, que é
exatamente o tipo de inconsistência que RNF-04 proíbe.

É a mesma lógica de [PR-03](#pr-03): este sistema registra o fato consumado. Quem
decide se a operação pode acontecer é quem a origina.

**Por que encerramento recusa.** Encerramento é definitivo: a conta não existe
mais para fins operacionais, e um lançamento nela indica erro de roteamento na
origem — falhar alto é mais útil que registrar silenciosamente num lugar errado.

**Impacto se estiver errada.** Localizado. É uma condição em
`Account.Register`, e há teste de domínio para cada uma das três situações. Se o
banco disser que bloqueio também recusa, muda uma linha e um teste.

**Pergunta ao banco.** Conta bloqueada deve recusar registro de movimentação, ou
apenas impedir que novas operações sejam originadas? Há diferença entre bloqueio
judicial e bloqueio operacional?

---

## PR-14 — Limites temporais do registro

**Lacuna.** O enunciado não diz quão retroativa uma movimentação pode ser, nem o
que fazer com uma data no futuro.

**Premissa.** Dois limites, ambos configuráveis:

| Limite | Padrão | Por quê |
|--------|--------|---------|
| Tolerância para o futuro | 1 minuto | Relógios de servidores divergem por alguns segundos mesmo com NTP. Recusar com tolerância zero rejeitaria movimentação legítima enviada "agora" por um sistema cujo relógio está um pouco à frente. O limite impede data absurda — ano 2099 —, não desvio normal |
| Retroatividade máxima | 30 dias | Retroatividade é legítima ([PR-04](#pr-04)). O limite barra erro de digitação ou de conversão de data — ano 1900 — que entraria no razão e invalidaria snapshots de anos inteiros antes de alguém notar |

**Por que isto é política e não invariante de domínio.** `Movement` não valida
nenhum dos dois, de propósito. A verificação correta depende de uma
**tolerância**, e tolerância precisa ser ajustável por quem opera o sistema —
logo pertence à camada que pode configurá-la, não ao tipo que representa o fato.

**Impacto se estiver errada.** Baixo e localizado: são dois valores em
`RegisterMovementOptions`, com teste que demonstra o ajuste.

**Pergunta ao banco.** Qual o atraso máximo real de uma movimentação retroativa?
Existe processo que envie lançamentos com meses de atraso — conciliação de
arquivo, por exemplo?

---

## Contradição aparente no enunciado

O enunciado pede uma solução "robusta, apta a operar em um ambiente financeiro
real" e, no mesmo documento, diz: "Você não precisa esgotar todas as
possibilidades técnicas do problema".

Não são instruções conflitantes, são instruções sobre coisas diferentes. A regra
que apliquei para resolver:

> **Implemento o que protege a corretude do dinheiro. Documento o que protege a
> escala.**

Imutabilidade, idempotência e precisão decimal são código, porque sem eles o
sistema está errado em qualquer volume. Multi-região, sharding e mensageria são
ADR, porque só passam a importar num volume que ninguém mediu ainda.
