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
}
