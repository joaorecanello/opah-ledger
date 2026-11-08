using System.Data.Common;
using Ledger.Application.Movements;
using Ledger.Domain;
using Ledger.Infrastructure;
using Ledger.Infrastructure.Persistence;
using Ledger.Infrastructure.Resilience;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Ledger.Integration.Tests;

/// <summary>
/// Em que momento a falha de rede acontece em relação ao commit.
/// </summary>
public enum FaultMoment
{
    /// <summary>A conexão cai antes do commit: nada foi gravado.</summary>
    BeforeCommit,

    /// <summary>
    /// O commit acontece, e a confirmação se perde: o banco gravou e o cliente
    /// acha que não.
    /// </summary>
    AfterCommit,
}

/// <summary>
/// Injeta uma falha de rede transitória no commit, uma única vez.
/// </summary>
/// <remarks>
/// A injeção é dentro da transação do EF, e não simulada em volta do caso de uso.
/// Isso importa: o que se quer exercitar é o retry do acesso a dados de verdade,
/// com o banco de verdade, no instante exato em que a ambiguidade existe.
/// </remarks>
internal sealed class CommitFault(FaultMoment moment) : DbTransactionInterceptor
{
    private int _chamadas;
    private int _falhas;

    /// <summary>Quantas vezes a falha foi de fato lançada.</summary>
    public int Falhas => _falhas;

    /// <summary>
    /// Quantas vezes o ponto de injeção foi alcançado, com ou sem falha.
    /// </summary>
    /// <remarks>
    /// Diferente de <see cref="Falhas"/> de propósito. Antes do commit, a segunda
    /// tentativa também chega ao commit e dessa vez ele dá certo: são duas
    /// chamadas e uma falha. Contar chamadas como falhas fez o teste afirmar o
    /// número errado — e o valor 2 era, na verdade, a prova de que o retry rodou.
    /// </remarks>
    public int Chamadas => _chamadas;

    /// <inheritdoc/>
    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction,
        TransactionEventData eventData,
        InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        if (moment == FaultMoment.BeforeCommit)
        {
            Disparar();
        }

        return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
    }

    /// <inheritdoc/>
    public override Task TransactionCommittedAsync(
        DbTransaction transaction,
        TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (moment == FaultMoment.AfterCommit)
        {
            Disparar();
        }

        return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }

    private void Disparar()
    {
        if (Interlocked.Increment(ref _chamadas) == 1)
        {
            Interlocked.Increment(ref _falhas);
            throw new NpgsqlException("conexão perdida", new IOException("connection reset by peer"));
        }
    }
}

/// <summary>
/// A repetição automática depois de uma falha de rede no meio do commit.
/// </summary>
/// <remarks>
/// <para>
/// Este é o cenário que justifica ter retry <b>e</b> idempotência juntos. Uma
/// queda de rede entre o commit e a resposta deixa o cliente sem saber se o
/// lançamento existe. Repetir sem proteção duplicaria dinheiro; não repetir
/// perderia o lançamento quando a falha foi antes do commit.
/// </para>
/// <para>
/// A resposta correta depende de <i>quando</i> a falha ocorreu, e o sistema não
/// tem como saber — por isso os dois momentos são testados, nos dois caminhos de
/// escrita. Em ambos, o resultado tem que ser <b>um lançamento, nunca zero e
/// nunca dois</b>.
/// </para>
/// </remarks>
[Collection(PostgresDatabase.Name)]
public sealed class AmbiguousCommitTests(PostgresFixture fixture)
{
    private static string NovaChave() => $"chave-{Guid.CreateVersion7():N}";

    private LedgerDbContext ContextoComFalha(CommitFault falha)
    {
        fixture.SkipIfUnavailable();

        var construtor = new DbContextOptionsBuilder<LedgerDbContext>();

        LedgerDbContextOptions.Apply(
            construtor,
            fixture.ConnectionString!,
            new LedgerResilienceOptions
            {
                MaxRetries = 2,
                MaxRetryDelay = TimeSpan.FromMilliseconds(10),
                RetryBudget = TimeSpan.FromSeconds(10),
            });

        construtor.AddInterceptors(falha);

        return new LedgerDbContext(construtor.Options);
    }

    public static TheoryData<bool, FaultMoment> Cenarios =>
        new()
        {
            { false, FaultMoment.BeforeCommit },
            { false, FaultMoment.AfterCommit },
            { true, FaultMoment.BeforeCommit },
            { true, FaultMoment.AfterCommit },
        };

    [Theory]
    [MemberData(nameof(Cenarios))]
    public async Task Falha_no_commit_resulta_em_exatamente_um_lancamento(
        bool retroativa,
        FaultMoment momento)
    {
        // retroativa: false -> caminho comum (transação implícita do SaveChanges)
        // retroativa: true  -> caminho com transação explícita e invalidação de
        //                      fechamentos, sob a estratégia de execução
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var chave = NovaChave();
        var ocorridaEm = retroativa
            ? DateTimeOffset.UtcNow.AddDays(-5)
            : DateTimeOffset.UtcNow.AddMinutes(-1);

        // Fechamentos prévios, para a invalidação ter o que invalidar.
        if (retroativa)
        {
            await using var preparacao = fixture.CreateContext();
            var registro = new RegisterMovementHandler(
                new LedgerRepository(preparacao),
                new SystemClock(),
                new RegisterMovementOptions { MaxBackdating = TimeSpan.FromDays(3650) });

            await registro.HandleAsync(
                new RegisterMovementCommand(
                    conta.Id.Value, MovementType.Credit, 500m, "BRL",
                    DateTimeOffset.UtcNow.AddDays(-20), NovaChave(), "preparacao"),
                TestContext.Current.CancellationToken);

            await using var consolidacao = fixture.CreateContext();
            await new ConsolidateSnapshotsHandlerFactory(consolidacao)
                .ConsolidarAsync(conta.Id, TestContext.Current.CancellationToken);
        }

        var falha = new CommitFault(momento);
        await using var contexto = ContextoComFalha(falha);

        var handler = new RegisterMovementHandler(
            new LedgerRepository(contexto),
            new SystemClock(),
            new RegisterMovementOptions { MaxBackdating = TimeSpan.FromDays(3650) });

        var resultado = await handler.HandleAsync(
            new RegisterMovementCommand(
                conta.Id.Value, MovementType.Credit, 100m, "BRL",
                ocorridaEm, chave, "corr-falha"),
            TestContext.Current.CancellationToken);

        falha.Falhas.ShouldBe(1, "a falha precisa ter sido de fato injetada, ou o teste é vazio");

        // Antes do commit a repetição também chega ao commit, e dessa vez ele dá
        // certo. Depois do commit a repetição bate na chave única e nunca chega a
        // fazer commit. As chamadas contam essa história.
        falha.Chamadas.ShouldBe(
            momento == FaultMoment.BeforeCommit ? 2 : 1,
            "o número de commits tentados mostra se a repetição aconteceu e como terminou");

        // ANTES do commit: nada foi gravado, a repetição grava -> Registered.
        // DEPOIS do commit: o banco já tinha gravado, a repetição bate na chave
        // única e o caso de uso devolve a resposta original -> Replayed.
        //
        // Os dois desfechos entregam o mesmo ao chamador: o lançamento existe, com
        // o mesmo identificador. A diferença é só o status, e é por isso que o
        // retry é seguro sem o chamador precisar saber em que momento caiu.
        resultado.Status.ShouldBe(
            momento == FaultMoment.BeforeCommit
                ? RegisterMovementStatus.Registered
                : RegisterMovementStatus.Replayed);

        await using var leitura = fixture.CreateContext();
        var gravadas = await leitura.Movements
            .Where(m => m.IdempotencyKey == chave)
            .ToListAsync(TestContext.Current.CancellationToken);

        gravadas.Count.ShouldBe(1, "um lançamento: nunca zero, nunca dois");
        resultado.MovementId.ShouldBe(gravadas[0].Id);
        gravadas[0].Amount.ShouldBe(100m);

        var chaves = await leitura.IdempotencyRecords
            .CountAsync(r => r.Key == chave, TestContext.Current.CancellationToken);
        chaves.ShouldBe(1);

        if (retroativa)
        {
            // A invalidação acontece junto com o fato, nos dois momentos: antes do
            // commit, na tentativa que deu certo; depois, na que já havia gravado.
            var obsoletos = await leitura.BalanceSnapshots
                .CountAsync(
                    s => s.AccountId == conta.Id.Value && s.IsStale,
                    TestContext.Current.CancellationToken);

            obsoletos.ShouldBeGreaterThan(
                0,
                "um fechamento que deveria estar inválido não pode continuar sendo usado");
        }
    }

    [Fact]
    public async Task Repeticao_depois_de_commit_perdido_nao_duplica_o_saldo()
    {
        // O mesmo cenário medido pelo que o cliente enxerga: o saldo. Dois
        // lançamentos de 100 dariam 200; zero daria 0.
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var chave = NovaChave();
        var falha = new CommitFault(FaultMoment.AfterCommit);

        await using (var contexto = ContextoComFalha(falha))
        {
            await new RegisterMovementHandler(
                new LedgerRepository(contexto), new SystemClock())
                .HandleAsync(
                    new RegisterMovementCommand(
                        conta.Id.Value, MovementType.Credit, 100m, "BRL",
                        DateTimeOffset.UtcNow.AddMinutes(-1), chave, "corr"),
                    TestContext.Current.CancellationToken);
        }

        await using var leitura = fixture.CreateContext();
        var saldo = await new Ledger.Application.Balances.GetBalanceHandler(
            new LedgerRepository(leitura),
            new SnapshotStore(leitura),
            new SystemClock())
            .HandleAsync(
                new Ledger.Application.Balances.GetBalanceQuery(conta.Id.Value),
                TestContext.Current.CancellationToken);

        saldo.Balance.ShouldBe(100m);
    }
}

/// <summary>
/// Atalho para consolidar uma conta nos testes de commit ambíguo.
/// </summary>
internal sealed class ConsolidateSnapshotsHandlerFactory(LedgerDbContext contexto)
{
    public Task<int> ConsolidarAsync(AccountId conta, CancellationToken cancellationToken) =>
        new Ledger.Application.Balances.ConsolidateSnapshotsHandler(
            new LedgerRepository(contexto),
            new SnapshotStore(contexto),
            new SystemClock())
            .ConsolidarContaAsync(conta, cancellationToken);
}
