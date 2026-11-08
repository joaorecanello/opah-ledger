using Ledger.Application.Abstractions;
using Ledger.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ledger.Infrastructure.Persistence;

/// <summary>
/// Acesso ao razão sobre PostgreSQL.
/// </summary>
/// <remarks>
/// Este adaptador é o único lugar do sistema que sabe que a garantia de
/// não-duplicidade vive num unique constraint do PostgreSQL, e em qual tabela.
/// O caso de uso recebe apenas "gravou" ou "a chave já existia".
/// </remarks>
public sealed class LedgerRepository(LedgerDbContext context) : ILedgerRepository
{
    /// <summary>
    /// Nome do constraint que garante a unicidade da chave de idempotência.
    /// </summary>
    /// <remarks>
    /// A verificação é pelo nome, e não apenas pelo código <c>23505</c>. Qualquer
    /// unique violation tem o mesmo código, e tratar uma colisão de chave
    /// primária de <c>movements</c> como "chave de idempotência repetida" faria o
    /// sistema responder <c>200</c> a um pedido que não foi registrado.
    /// </remarks>
    public const string IdempotencyConstraint = "pk_idempotency_records";

    /// <inheritdoc/>
    public async Task<Account?> FindAccountAsync(
        AccountId accountId,
        CancellationToken cancellationToken)
    {
        var registro = await context.Accounts
            .AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == accountId.Value, cancellationToken)
            .ConfigureAwait(false);

        return registro is null ? null : PersistenceMapper.ToDomain(registro);
    }

    /// <inheritdoc/>
    public async Task<LedgerWriteOutcome> TryRegisterAsync(
        Movement movement,
        string requestFingerprint,
        DateTimeOffset idempotencyExpiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(movement);

        context.Movements.Add(PersistenceMapper.ToRecord(movement));
        context.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Key = movement.IdempotencyKey.Value,
            AccountId = movement.AccountId.Value,
            MovementId = movement.Id.Value,
            RequestFingerprint = requestFingerprint,

            // O instante de criação do registro É o PostedAt da movimentação:
            // as duas linhas nascem da mesma transação e do mesmo relógio. Isso
            // permite responder ao replay com o instante original sem uma
            // segunda leitura.
            CreatedAt = movement.PostedAt.ToUniversalTime(),
            ExpiresAt = idempotencyExpiresAt.ToUniversalTime(),
        });

        try
        {
            // Um único SaveChanges é uma transação: ou as duas linhas entram, ou
            // nenhuma entra. Se fossem separadas, haveria uma janela em que a
            // movimentação existe sem a chave — e o retry nessa janela
            // duplicaria o lançamento.
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return LedgerWriteOutcome.Persisted;
        }
        catch (DbUpdateException erro) when (EhChaveDeIdempotenciaRepetida(erro))
        {
            // O rastreador ainda tem as duas entidades em estado "Added". Sem
            // limpar, um SaveChanges posterior no mesmo contexto tentaria
            // gravá-las de novo.
            context.ChangeTracker.Clear();
            return LedgerWriteOutcome.DuplicateIdempotencyKey;
        }
    }

    /// <inheritdoc/>
    public async Task<IdempotentRegistration?> FindRegistrationAsync(
        IdempotencyKey key,
        CancellationToken cancellationToken)
    {
        var registro = await context.IdempotencyRecords
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.Key == key.Value, cancellationToken)
            .ConfigureAwait(false);

        return registro is null
            ? null
            : new IdempotentRegistration(
                registro.MovementId,
                registro.RequestFingerprint,
                registro.CreatedAt);
    }

    private static bool EhChaveDeIdempotenciaRepetida(DbUpdateException erro) =>
        erro.InnerException is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(
            postgres.ConstraintName,
            IdempotencyConstraint,
            StringComparison.Ordinal);
}
