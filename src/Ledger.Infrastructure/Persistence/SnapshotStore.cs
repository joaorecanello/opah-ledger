using Ledger.Application.Abstractions;
using Ledger.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Infrastructure.Persistence;

/// <summary>Cache de saldo de fechamento sobre PostgreSQL.</summary>
public sealed class SnapshotStore(LedgerDbContext context) : ISnapshotStore
{
    /// <inheritdoc/>
    public async Task<SnapshotLookup> FindForBalanceAsync(
        AccountId accountId,
        DateOnly beforeDate,
        CancellationToken cancellationToken)
    {
        // Primeiro o mais recente, obsoleto ou não. No caso comum ele serve, e a
        // consulta termina aqui com uma ida ao banco.
        var maisRecente = await Anteriores(accountId, beforeDate)
            .OrderByDescending(s => s.AsOfDate)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (maisRecente is null)
        {
            return new SnapshotLookup(null, SkippedStale: false);
        }

        if (!maisRecente.IsStale)
        {
            return new SnapshotLookup(PersistenceMapper.ToDomain(maisRecente), SkippedStale: false);
        }

        // Caminho raro: houve movimentação retroativa. Em lugar de desistir do
        // cache e somar o histórico inteiro, recua para o fechamento mais
        // recente que ainda vale — a degradação é proporcional ao quanto a
        // retroatividade alcançou, não total.
        var utilizavel = await Anteriores(accountId, beforeDate)
            .Where(s => !s.IsStale)
            .OrderByDescending(s => s.AsOfDate)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new SnapshotLookup(
            utilizavel is null ? null : PersistenceMapper.ToDomain(utilizavel),
            SkippedStale: true);
    }

    /// <inheritdoc/>
    public async Task SaveAsync(BalanceSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Rastreado de propósito: substituir é o caso normal, porque a
        // consolidação recalcula dias invalidados por movimentação retroativa.
        var existente = await context.BalanceSnapshots
            .AsTracking()
            .FirstOrDefaultAsync(
                s => s.AccountId == snapshot.AccountId.Value && s.AsOfDate == snapshot.AsOfDate,
                cancellationToken)
            .ConfigureAwait(false);

        var novo = PersistenceMapper.ToRecord(snapshot);

        if (existente is null)
        {
            context.BalanceSnapshots.Add(novo);
        }
        else
        {
            existente.ClosingBalance = novo.ClosingBalance;
            existente.Currency = novo.Currency;
            existente.MovementCount = novo.MovementCount;
            existente.IsStale = novo.IsStale;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.ChangeTracker.Clear();
    }

    /// <inheritdoc/>
    public async Task<int> MarkStaleFromAsync(
        AccountId accountId,
        DateOnly fromDate,
        CancellationToken cancellationToken) =>
        await context.BalanceSnapshots
            .Where(s => s.AccountId == accountId.Value
                     && s.AsOfDate >= fromDate
                     && !s.IsStale)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.IsStale, true),
                cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BalanceSnapshot>> ListAsync(
        AccountId accountId,
        CancellationToken cancellationToken)
    {
        var registros = await context.BalanceSnapshots
            .AsNoTracking()
            .Where(s => s.AccountId == accountId.Value)
            .OrderBy(s => s.AsOfDate)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. registros.Select(PersistenceMapper.ToDomain)];
    }

    /// <inheritdoc/>
    public async Task<DateOnly?> FindLatestDateAsync(
        AccountId accountId,
        CancellationToken cancellationToken) =>
        await context.BalanceSnapshots
            .AsNoTracking()
            .Where(s => s.AccountId == accountId.Value)
            .Select(s => (DateOnly?)s.AsOfDate)
            .MaxAsync(cancellationToken)
            .ConfigureAwait(false);

    private IQueryable<BalanceSnapshotRecord> Anteriores(AccountId accountId, DateOnly beforeDate) =>
        context.BalanceSnapshots
            .AsNoTracking()
            .Where(s => s.AccountId == accountId.Value && s.AsOfDate < beforeDate);
}
