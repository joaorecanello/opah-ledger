# ADR-0001 — Monólito modular em lugar de microsserviços

- **Status:** aceito
- **Data:** 2026-11-07
- **Requisitos relacionados:** RNF-02 (alta demanda), RNF-04 (consistência), RNF-06 (manutenibilidade)

## Problema

Como estruturar a solução — uma aplicação ou várias, quais camadas, quais
fronteiras — para atender escala e manutenibilidade sem inviabilizar a
corretude?

O enunciado concede essa liberdade explicitamente e, num processo seletivo, há
uma tentação óbvia: propor microsserviços porque demonstra conhecimento. Essa é
a decisão errada por motivo errado.

## Alternativas consideradas

1. **Microsserviços**, separando o serviço de escrita (registro de
   movimentação) do serviço de leitura (consulta de saldo).
2. **Funções serverless** por caso de uso.
3. **Monólito modular** com fronteiras internas explícitas e verificadas, em
   dois processos: API e worker.

## Decisão

Alternativa 3. Monólito modular em quatro camadas — `Domain`, `Application`,
`Infrastructure` e os hosts —, implantado como dois processos.

## Justificativa

**Existe um único contexto delimitado aqui.** RF-01 e RF-02 operam sobre os
mesmos dados, com o mesmo invariante. Não são dois domínios que por acaso
conversam; são duas operações sobre um razão contábil.

**Separá-los não remove acoplamento, transfere-o para a rede.** E na rede o
acoplamento passa a custar consistência distribuída: *sagas*, compensação,
reconciliação entre serviços. Em troca de quê? A escala neste domínio se
resolve por particionamento de dados e réplicas de leitura, não por fronteira
de processo. O gargalho do legado quase certamente não é CPU de aplicação — é
contenção de dados, que microsserviço nenhum conserta.

**O requisito dominante é RNF-04, consistência.** Microsserviços a tornam mais
difícil, não mais fácil. Escolher uma arquitetura que piora o requisito mais
importante para ganhar escalabilidade que eu não preciso é o oposto de decisão
arquitetural.

**Há um requisito prático que costuma ser esquecido.** RO-04 exige README com
instruções claras de execução local, e o teste é desconsiderado se não for
atendido. Uma malha de serviços com descoberta, broker e banco por serviço não
sobe com um comando na máquina de quem avalia.

### Por que dois processos, e não um

API e worker são processos separados de propósito. A API serve tráfego síncrono
com latência previsível; o worker faz consolidação de snapshot e varredura de
outbox, que é trabalho em lote.

Juntá-los significa que um job de consolidação pesado come o *thread pool* que
atende consultas de saldo — e esse é um mecanismo plausível para a "lentidão em
horários de pico" descrita no enunciado. Separá-los também permite escalar os
dois independentemente: muitas instâncias de API, uma de worker.

Eles compartilham banco e assemblies de domínio. Isso é intencional: a fronteira
que importa aqui é de perfil de carga, não de domínio.

## Trade-offs aceitos

| Perco | Ganho |
|-------|-------|
| Deploy independente por módulo | transações locais ACID, sem consistência distribuída |
| Isolamento de falha em nível de processo por módulo | depuração de ponta a ponta num único *stack trace* |
| Escala independente por módulo | `docker compose up` que roda na máquina de quem avalia |
| Liberdade de stack por serviço | um único conjunto de dependências para manter |

O isolamento de falha está **parcialmente** recuperado pela separação
API/worker: um worker travado não derruba o atendimento de consultas.

## Consequências

As dependências apontam só para dentro:
`Api`/`Worker` → `Infrastructure` → `Application` → `Domain`.

`Ledger.Domain` não tem uma única `PackageReference`. Ele não conhece banco,
HTTP, serialização nem injeção de dependência. Se um conceito precisa de algum
deles, está na camada errada.

**E isso é verificado no build**, não combinado em reunião. `Ledger.Architecture.Tests`
falha se o domínio ganhar dependência de outra camada, de EF Core, de ASP.NET,
de Npgsql, ou de qualquer pacote externo.

Essa verificação é o que diferencia esta decisão de um monólito comum. Sem ela,
"modular" é uma intenção que não sobrevive à primeira sprint sob pressão,
porque nada impede um `using` conveniente — e ninguém percebe até a extração
de um serviço ficar caro. Com ela, a erosão quebra o build no dia em que
acontece.

## Gatilho de revisão

Extrairia um serviço quando **uma destas duas coisas** acontecer — e nenhuma
delas é volume por si:

1. **Razão organizacional.** Times distintos passam a ter cadência de release
   conflitante sobre o mesmo código. Microsserviço resolve problema de
   organização com mais frequência do que problema técnico, e isso é legítimo.
2. **Perfil de carga divergente.** Por exemplo, ingestão em lote noturna
   competindo por recursos com consultas interativas de forma que a separação
   API/worker já não absorve.

Quando o gatilho vier, a extração é mecânica: as fronteiras já existem e estão
testadas. É esse o investimento que esta decisão faz — não adiar a escolha, mas
manter o custo de mudá-la baixo.
