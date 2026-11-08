using System.Collections.Concurrent;
using Ledger.Application.Abstractions;
using Ledger.Domain;

namespace Ledger.Application.Tests;

/// <summary>Relógio fixo, controlado pelo teste.</summary>
/// <remarks>
/// O instante de registro entra no identificador da movimentação e na janela de
/// validação temporal, e o dia corrente define até onde a consolidação pode ir.
/// Com o relógio real, um teste de "datada 31 dias atrás é recusada" mudaria de
/// comportamento conforme o dia em que roda.
/// </remarks>
internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

/// <summary>
/// Razão em memória, com a mesma semântica de unicidade do banco.
/// </summary>
/// <remarks>
/// <para>
/// Reproduz <b>uma</b> propriedade do PostgreSQL: a chave de idempotência é
/// única, e a tentativa duplicada não grava nada. É o suficiente para verificar
/// a decisão dos casos de uso.
/// </para>
/// <para>
/// O que ela <b>não</b> reproduz é concorrência real nem a atomicidade entre
/// gravar o fato e invalidar snapshots — e por isso esses cenários vivem nos
/// testes de integração, contra PostgreSQL de verdade. Um falso que fingisse
/// reproduzi-los daria falso-positivo no cenário de maior risco do sistema.
/// </para>
/// </remarks>
internal sealed class FakeLedgerRepository(FakeSnapshotStore? snapshots = null) : ILedgerRepository
{
    private readonly ConcurrentDictionary<Guid, Account> _contas = new();
    private readonly ConcurrentDictionary<string, IdempotentRegistration> _chaves = new();
    private readonly ConcurrentDictionary<Guid, Movement> _movimentacoes = new();

    /// <summary>Movimentações efetivamente gravadas.</summary>
    public IReadOnlyCollection<Movement> Movimentacoes => [.. _movimentacoes.Values];

    /// <summary>Simula a chave desaparecer entre o INSERT e a leitura.</summary>
    /// <remarks>
    /// Cobre o caminho em que a limpeza das chaves expiradas passa entre as duas
    /// operações. É improvável e precisa de resposta definida: repetir
    /// duplicaria dinheiro.
    /// </remarks>
    public bool EsquecerRegistroAposConflito { get; set; }

    public void Adicionar(Account conta) => _contas[conta.Id.Value] = conta;

    public Task<Account?> FindAccountAsync(AccountId accountId, CancellationToken cancellationToken) =>
        Task.FromResult(_contas.TryGetValue(accountId.Value, out var conta) ? conta : null);

    public Task<Movement?> FindMovementAsync(
        MovementId movementId,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            _movimentacoes.TryGetValue(movementId.Value, out var movimentacao)
                ? movimentacao
                : null);

    public Task AddAccountAsync(Account account, CancellationToken cancellationToken)
    {
        Adicionar(account);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AccountId>> ListAccountIdsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AccountId>>([.. _contas.Values.Select(c => c.Id)]);

    public Task<DateOnly?> FindFirstMovementDateAsync(
        AccountId accountId,
        CancellationToken cancellationToken)
    {
        var instantes = _movimentacoes.Values
            .Where(m => m.AccountId == accountId)
            .Select(m => m.OccurredAt)
            .ToArray();

        return Task.FromResult(
            instantes.Length == 0
                ? null
                : (DateOnly?)DateOnly.FromDateTime(instantes.Min().UtcDateTime));
    }

    public Task<LedgerWriteOutcome> TryRegisterAsync(
        Movement movement,
        string requestFingerprint,
        DateTimeOffset idempotencyExpiresAt,
        CancellationToken cancellationToken)
    {
        var registro = new IdempotentRegistration(
            movement.Id.Value,
            requestFingerprint,
            movement.PostedAt);

        if (!_chaves.TryAdd(movement.IdempotencyKey.Value, registro))
        {
            // Nada é gravado: a movimentação e a chave entrariam na mesma
            // transação, logo a violação desfaz as duas.
            return Task.FromResult(LedgerWriteOutcome.DuplicateIdempotencyKey);
        }

        _movimentacoes[movement.Id.Value] = movement;

        // Invalida fechamentos como o adaptador real faz — na mesma operação.
        // No PostgreSQL isso é uma transação explícita; aqui é apenas sequencial,
        // e é justamente a diferença que os testes de integração cobrem.
        if (movement.OccurredAt.UtcDateTime.Date < movement.PostedAt.UtcDateTime.Date)
        {
            snapshots?.MarcarObsoletosDe(
                movement.AccountId,
                DateOnly.FromDateTime(movement.OccurredAt.UtcDateTime));
        }

        return Task.FromResult(LedgerWriteOutcome.Persisted);
    }

    public Task<LedgerSum> SumRangeAsync(
        AccountId accountId,
        DateTimeOffset? inclusiveFrom,
        DateTimeOffset inclusiveTo,
        CancellationToken cancellationToken)
    {
        // Usa SignedAmount do domínio de propósito: o que estes testes verificam
        // é a decisão dos casos de uso, não a aritmética. A equivalência entre a
        // soma do domínio e a do SQL é verificada nos testes de integração, que
        // é onde o SQL existe.
        var consideradas = _movimentacoes.Values
            .Where(m => m.AccountId == accountId
                     && m.OccurredAt <= inclusiveTo
                     && (inclusiveFrom is null || m.OccurredAt >= inclusiveFrom))
            .ToArray();

        var soma = consideradas.Aggregate(
            0m,
            (acumulado, movimentacao) => acumulado + movimentacao.SignedAmount.Amount);

        return Task.FromResult(new LedgerSum(soma, consideradas.Length));
    }

    public Task<IdempotentRegistration?> FindRegistrationAsync(
        IdempotencyKey key,
        CancellationToken cancellationToken)
    {
        if (EsquecerRegistroAposConflito)
        {
            return Task.FromResult<IdempotentRegistration?>(null);
        }

        return Task.FromResult(
            _chaves.TryGetValue(key.Value, out var registro) ? registro : null);
    }
}

/// <summary>Cache de fechamento em memória.</summary>
internal sealed class FakeSnapshotStore : ISnapshotStore
{
    private readonly ConcurrentDictionary<(Guid Conta, DateOnly Dia), BalanceSnapshot> _snapshots = new();

    /// <summary>Quantos fechamentos estão guardados.</summary>
    public int Count => _snapshots.Count;

    /// <summary>Permite ao teste corromper um fechamento, para exercitar a reconciliação.</summary>
    public void Corromper(AccountId conta, DateOnly dia, Money saldoErrado)
    {
        if (_snapshots.TryGetValue((conta.Value, dia), out var atual))
        {
            _snapshots[(conta.Value, dia)] = BalanceSnapshot.Rehydrate(
                conta, dia, saldoErrado, atual.MovementCount, isStale: false);
        }
    }

    /// <summary>Marca obsoletos a partir de um dia, como o adaptador real.</summary>
    public void MarcarObsoletosDe(AccountId conta, DateOnly dia)
    {
        foreach (var chave in _snapshots.Keys.Where(k => k.Conta == conta.Value && k.Dia >= dia))
        {
            _snapshots[chave] = _snapshots[chave].MarkStale();
        }
    }

    public Task<SnapshotLookup> FindForBalanceAsync(
        AccountId accountId,
        DateOnly beforeDate,
        CancellationToken cancellationToken)
    {
        var anteriores = _snapshots.Values
            .Where(s => s.AccountId == accountId && s.AsOfDate < beforeDate)
            .OrderByDescending(s => s.AsOfDate)
            .ToArray();

        if (anteriores.Length == 0)
        {
            return Task.FromResult(new SnapshotLookup(null, SkippedStale: false));
        }

        if (!anteriores[0].IsStale)
        {
            return Task.FromResult(new SnapshotLookup(anteriores[0], SkippedStale: false));
        }

        var utilizavel = anteriores.FirstOrDefault(s => !s.IsStale);
        return Task.FromResult(new SnapshotLookup(utilizavel, SkippedStale: true));
    }

    public Task SaveAsync(BalanceSnapshot snapshot, CancellationToken cancellationToken)
    {
        _snapshots[(snapshot.AccountId.Value, snapshot.AsOfDate)] = snapshot;
        return Task.CompletedTask;
    }

    public Task<int> MarkStaleFromAsync(
        AccountId accountId,
        DateOnly fromDate,
        CancellationToken cancellationToken)
    {
        var alvos = _snapshots.Keys
            .Where(k => k.Conta == accountId.Value && k.Dia >= fromDate)
            .Where(k => !_snapshots[k].IsStale)
            .ToArray();

        foreach (var chave in alvos)
        {
            _snapshots[chave] = _snapshots[chave].MarkStale();
        }

        return Task.FromResult(alvos.Length);
    }

    public Task<IReadOnlyList<BalanceSnapshot>> ListAsync(
        AccountId accountId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BalanceSnapshot>>(
            [.. _snapshots.Values
                .Where(s => s.AccountId == accountId)
                .OrderBy(s => s.AsOfDate)]);

    public Task<DateOnly?> FindLatestDateAsync(
        AccountId accountId,
        CancellationToken cancellationToken)
    {
        var dias = _snapshots.Values
            .Where(s => s.AccountId == accountId)
            .Select(s => s.AsOfDate)
            .ToArray();

        return Task.FromResult(dias.Length == 0 ? null : (DateOnly?)dias.Max());
    }
}
