using Ledger.Domain;

namespace Ledger.Infrastructure.Persistence;

/// <summary>
/// Converte entre o modelo de domínio e o modelo de persistência.
/// </summary>
/// <remarks>
/// <para>
/// Mapeamento explícito, e não por convenção de ORM. O custo é esta classe; o
/// ganho é que o domínio não precisa ceder nada — nem construtor sem argumentos,
/// nem propriedade com <c>set</c>, nem objeto de valor achatado — e o esquema
/// pode evoluir sem arrastar o domínio. Ver ADR-0013.
/// </para>
/// <para>
/// A reconstrução usa <c>Rehydrate</c>, não a fábrica de criação: um fato que já
/// está no razão é histórico, e histórico não se revalida. Se a regra mudar
/// amanhã, os lançamentos de ontem continuam sendo o que foram.
/// </para>
/// </remarks>
public static class PersistenceMapper
{
    /// <summary>Converte a conta de domínio para linha de tabela.</summary>
    public static AccountRecord ToRecord(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);

        return new AccountRecord
        {
            Id = account.Id.Value,
            CustomerId = account.CustomerId.Value,
            Currency = account.Currency.Code,
            Status = account.Status,
            Version = 0L,
        };
    }

    /// <summary>Reconstrói a conta de domínio a partir da linha.</summary>
    public static Account ToDomain(AccountRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return Account.Rehydrate(
            AccountId.From(record.Id),
            CustomerId.From(record.CustomerId),
            Currency.Of(record.Currency),
            record.Status);
    }

    /// <summary>Converte a movimentação de domínio para linha de tabela.</summary>
    /// <remarks>
    /// Os dois instantes são normalizados para UTC. O Npgsql mapeia
    /// <see cref="DateTimeOffset"/> para <c>timestamptz</c>, que guarda o
    /// instante e descarta o deslocamento — e recusa gravar um valor cujo
    /// deslocamento não seja zero. Normalizar aqui, na fronteira, evita que a
    /// exceção apareça no meio de um <c>SaveChanges</c>.
    /// <para>
    /// O deslocamento original não é preservado, e isso é aceitável: o que o
    /// razão precisa saber é o instante, não em que fuso quem registrou estava.
    /// </para>
    /// </remarks>
    public static MovementRecord ToRecord(Movement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);

        return new MovementRecord
        {
            Id = movement.Id.Value,
            AccountId = movement.AccountId.Value,
            Type = movement.Type,
            Amount = movement.Amount.Amount,
            Currency = movement.Amount.Currency.Code,
            OccurredAt = movement.OccurredAt.ToUniversalTime(),
            PostedAt = movement.PostedAt.ToUniversalTime(),
            IdempotencyKey = movement.IdempotencyKey.Value,
            ExternalReference = movement.ExternalReference,
            CorrelationId = movement.CorrelationId,
        };
    }

    /// <summary>Reconstrói a movimentação de domínio a partir da linha.</summary>
    public static Movement ToDomain(MovementRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return Movement.Rehydrate(
            MovementId.From(record.Id),
            AccountId.From(record.AccountId),
            record.Type,
            Money.Of(record.Amount, Currency.Of(record.Currency)),
            record.OccurredAt,
            record.PostedAt,
            IdempotencyKey.Of(record.IdempotencyKey),
            record.CorrelationId,
            record.ExternalReference);
    }

    /// <summary>Converte o snapshot de domínio para linha de tabela.</summary>
    public static BalanceSnapshotRecord ToRecord(BalanceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new BalanceSnapshotRecord
        {
            AccountId = snapshot.AccountId.Value,
            AsOfDate = snapshot.AsOfDate,
            ClosingBalance = snapshot.ClosingBalance.Amount,
            Currency = snapshot.ClosingBalance.Currency.Code,
            MovementCount = snapshot.MovementCount,
            IsStale = snapshot.IsStale,
        };
    }

    /// <summary>Reconstrói o snapshot de domínio a partir da linha.</summary>
    public static BalanceSnapshot ToDomain(BalanceSnapshotRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return BalanceSnapshot.Rehydrate(
            AccountId.From(record.AccountId),
            record.AsOfDate,
            Money.Of(record.ClosingBalance, Currency.Of(record.Currency)),
            record.MovementCount,
            record.IsStale);
    }
}
