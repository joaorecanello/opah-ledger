using Ledger.Domain;

namespace Ledger.Application.Abstractions;

/// <summary>Resultado de uma tentativa de registro no razão.</summary>
public enum LedgerWriteOutcome
{
    /// <summary>Valor não atribuído. Nunca deve ocorrer.</summary>
    Unspecified = 0,

    /// <summary>A movimentação e a chave de idempotência foram gravadas.</summary>
    Persisted,

    /// <summary>
    /// A chave de idempotência já existia. <b>Nada foi gravado.</b>
    /// </summary>
    /// <remarks>
    /// A movimentação e a chave entram na mesma transação, logo a violação do
    /// constraint desfaz as duas. Sem isso, a tentativa duplicada gravaria a
    /// movimentação e só depois falharia na chave — deixando uma duplicata no
    /// razão com a aparência de ter sido recusada.
    /// </remarks>
    DuplicateIdempotencyKey,
}

/// <summary>
/// O que ficou registrado de uma tentativa anterior com a mesma chave.
/// </summary>
/// <param name="MovementId">Identificador da movimentação original.</param>
/// <param name="RequestFingerprint">Impressão do conteúdo da requisição original.</param>
/// <param name="RegisteredAt">
/// Instante em que a movimentação original foi registrada. É o mesmo
/// <c>PostedAt</c> da movimentação: as duas linhas entram na mesma transação, a
/// partir do mesmo relógio, e guardar o valor aqui evita uma segunda leitura no
/// caminho de replay.
/// </param>
public sealed record IdempotentRegistration(
    Guid MovementId,
    string RequestFingerprint,
    DateTimeOffset RegisteredAt);

/// <summary>
/// Soma das movimentações de uma conta até um instante.
/// </summary>
/// <param name="SignedAmount">
/// Soma com o sinal aplicado: créditos positivos, débitos negativos. Sem moeda,
/// porque a moeda é da conta e o caso de uso já a conhece — devolvê-la aqui
/// permitiria que o adaptador discordasse da conta.
/// </param>
/// <param name="MovementCount">Quantas movimentações entraram na soma.</param>
/// <remarks>
/// A soma é feita <b>no banco</b>, não em memória. Carregar o histórico para
/// somar na aplicação seria inviável no volume assumido em PR-02, e é
/// exatamente a varredura que o índice por conta e data existe para evitar.
/// <para>
/// O preço é que a convenção de sinal passa a existir em dois lugares: em
/// <c>Movement.SignedAmount</c> e no SQL. O que impede os dois de divergirem é um
/// teste de equivalência: ele gera movimentações, soma pelo domínio e pelo
/// banco, e exige o mesmo número.
/// </para>
/// </remarks>
public sealed record LedgerSum(decimal SignedAmount, long MovementCount);

/// <summary>
/// Acesso ao razão. Porta declarada aqui, implementada na infraestrutura.
/// </summary>
/// <remarks>
/// A interface é deliberadamente estreita e fala em termos de negócio. Ela não
/// expõe transação, unidade de trabalho nem código de erro de banco: a
/// atomicidade entre a movimentação e a chave de idempotência é
/// <b>responsabilidade do adaptador</b>, não algo que o caso de uso orquestra.
/// <para>
/// Essa escolha importa. Se o caso de uso abrisse a transação, ele precisaria
/// saber que a garantia de não-duplicidade vive num unique constraint, e em qual
/// tabela — que é exatamente o detalhe que o ADR-0012 mostrou ser sutil e
/// dependente do PostgreSQL.
/// </para>
/// </remarks>
public interface ILedgerRepository
{
    /// <summary>Busca a conta, ou <c>null</c> se ela não existe.</summary>
    Task<Account?> FindAccountAsync(AccountId accountId, CancellationToken cancellationToken);

    /// <summary>Busca uma movimentação pelo identificador, ou <c>null</c>.</summary>
    /// <remarks>
    /// Leitura pontual, sem regra. Existe para que o <c>201 Created</c> do
    /// registro possa apontar para um recurso que de fato responde — um
    /// <c>Location</c> para endpoint inexistente é contrato quebrado.
    /// </remarks>
    Task<Movement?> FindMovementAsync(MovementId movementId, CancellationToken cancellationToken);

    /// <summary>Persiste uma conta nova.</summary>
    /// <remarks>
    /// O cadastro de contas pertence a outro contexto delimitado
    /// ([PR-07](../../../docs/premissas.md)); isto existe para que o projeto seja
    /// executável e testável sem depender desse outro sistema.
    /// </remarks>
    Task AddAccountAsync(Account account, CancellationToken cancellationToken);

    /// <summary>
    /// Grava a movimentação e a chave de idempotência atomicamente.
    /// </summary>
    /// <param name="movement">Movimentação já validada pelo domínio.</param>
    /// <param name="requestFingerprint">Impressão do conteúdo da requisição.</param>
    /// <param name="idempotencyExpiresAt">Quando a chave pode ser descartada.</param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    /// <returns>
    /// <see cref="LedgerWriteOutcome.Persisted"/> ou
    /// <see cref="LedgerWriteOutcome.DuplicateIdempotencyKey"/>.
    /// </returns>
    Task<LedgerWriteOutcome> TryRegisterAsync(
        Movement movement,
        string requestFingerprint,
        DateTimeOffset idempotencyExpiresAt,
        CancellationToken cancellationToken);

    /// <summary>
    /// Busca o registro de idempotência de uma chave, ou <c>null</c>.
    /// </summary>
    Task<IdempotentRegistration?> FindRegistrationAsync(
        IdempotencyKey key,
        CancellationToken cancellationToken);

    /// <summary>
    /// Soma as movimentações da conta ocorridas <b>até</b> o instante informado.
    /// </summary>
    /// <param name="accountId">Conta.</param>
    /// <param name="asOf">
    /// Instante de corte, <b>inclusivo</b>: uma movimentação cujo
    /// <c>OccurredAt</c> é exatamente este instante entra na soma.
    /// </param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    /// <returns>
    /// A soma e a contagem. Conta sem movimentação devolve zero e zero — não
    /// erro, porque saldo zero é a resposta correta para uma conta sem
    /// lançamentos.
    /// </returns>
    /// <remarks>
    /// O corte usa <c>OccurredAt</c>, o tempo de negócio: "qual era meu saldo na
    /// sexta" pergunta sobre fatos, não sobre o que o sistema já havia
    /// registrado. Ver PR-04.
    /// </remarks>
    Task<LedgerSum> SumUpToAsync(
        AccountId accountId,
        DateTimeOffset asOf,
        CancellationToken cancellationToken);
}
