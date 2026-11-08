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

    /// <inheritdoc/>
    public async Task<Movement?> FindMovementAsync(
        MovementId movementId,
        CancellationToken cancellationToken)
    {
        var registro = await context.Movements
            .AsNoTracking()
            .SingleOrDefaultAsync(m => m.Id == movementId.Value, cancellationToken)
            .ConfigureAwait(false);

        return registro is null ? null : PersistenceMapper.ToDomain(registro);
    }

    /// <inheritdoc/>
    public async Task AddAccountAsync(Account account, CancellationToken cancellationToken)
    {
        context.Accounts.Add(PersistenceMapper.ToRecord(account));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<LedgerSum> SumUpToAsync(
        AccountId accountId,
        DateTimeOffset asOf,
        CancellationToken cancellationToken)
    {
        // A soma e a contagem saem numa ida só ao banco. O GroupBy sobre uma
        // constante é o jeito de pedir uma agregação sem GROUP BY de verdade:
        // o EF emite SUM(CASE WHEN type = 'Credit' THEN amount ELSE -amount END)
        // e COUNT(*) na mesma consulta.
        //
        // O CASE WHEN repete a convenção de sinal que Movement.SignedAmount
        // define. É duplicação consciente — somar em memória seria inviável no
        // volume real —, e o que impede as duas de divergirem é o teste de
        // equivalência entre o cálculo do domínio e o do banco.
        var agregado = await context.Movements
            .AsNoTracking()
            .Where(m => m.AccountId == accountId.Value && m.OccurredAt <= asOf)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Soma = g.Sum(m => m.Type == MovementType.Credit ? m.Amount : -m.Amount),
                Contagem = g.LongCount(),
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // Conjunto vazio: conta sem movimentação até o corte. Saldo zero é a
        // resposta correta, não ausência de resposta.
        return agregado is null
            ? new LedgerSum(0m, 0L)
            : new LedgerSum(agregado.Soma, agregado.Contagem);
    }

    private static bool EhChaveDeIdempotenciaRepetida(DbUpdateException erro) =>
        erro.InnerException is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(
            postgres.ConstraintName,
            IdempotencyConstraint,
            StringComparison.Ordinal);
}
