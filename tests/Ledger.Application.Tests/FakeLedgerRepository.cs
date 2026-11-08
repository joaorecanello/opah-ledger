using System.Collections.Concurrent;
using Ledger.Application.Abstractions;
using Ledger.Domain;

namespace Ledger.Application.Tests;

/// <summary>Relógio fixo, controlado pelo teste.</summary>
/// <remarks>
/// O instante de registro entra no identificador da movimentação e na janela de
/// validação temporal. Com o relógio real, um teste de "datada 31 dias atrás é
/// recusada" mudaria de comportamento conforme o dia em que roda.
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
/// Esta fábrica reproduz <b>uma</b> propriedade do PostgreSQL: a chave de
/// idempotência é única, e a tentativa duplicada não grava nada. É o suficiente
/// para verificar a decisão do caso de uso.
/// </para>
/// <para>
/// O que ela <b>não</b> reproduz é o comportamento sob concorrência real — e por
/// isso os testes de concorrência e de atomicidade vivem nos testes de
/// integração, contra PostgreSQL de verdade. Um falso que fingisse reproduzir
/// isso daria falso-positivo no cenário de maior risco do sistema.
/// </para>
/// </remarks>
internal sealed class FakeLedgerRepository : ILedgerRepository
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
        return Task.FromResult(LedgerWriteOutcome.Persisted);
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
