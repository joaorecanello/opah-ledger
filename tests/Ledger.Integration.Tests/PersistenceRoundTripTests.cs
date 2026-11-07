using Ledger.Domain;
using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ledger.Integration.Tests;

/// <summary>
/// Verifica que o que entra no banco é o que sai.
/// </summary>
/// <remarks>
/// Precisão monetária e instante correto são exatamente o tipo de coisa que o
/// mapeamento pode corromper em silêncio: um tipo de coluna errado arredonda, um
/// fuso perdido desloca o lançamento de um dia. Nenhum dos dois lança exceção.
/// </remarks>
[Collection(PostgresDatabase.Name)]
public sealed class PersistenceRoundTripTests(PostgresFixture fixture)
{
    private static string NovaChave() => $"chave-{Guid.CreateVersion7():N}";

    [Theory]
    [InlineData("0.0001")]
    [InlineData("0.0099")]
    [InlineData("1234.5678")]
    [InlineData("999999999999.9999")]
    [InlineData("0.1")]
    public async Task Valor_monetario_volta_exatamente_igual(string valorTexto)
    {
        // 0.1 está na lista de propósito: é o valor que ponto flutuante binário
        // não representa. Em numeric/decimal, ida e volta é exata.
        fixture.SkipIfUnavailable();
        var valor = decimal.Parse(valorTexto, System.Globalization.CultureInfo.InvariantCulture);
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var movimentacao = LedgerTestData.NovaMovimentacao(conta, valor, NovaChave());

        await using (var contexto = fixture.CreateContext())
        {
            await LedgerTestData.RegistrarAsync(contexto, movimentacao);
        }

        await using var leitura = fixture.CreateContext();
        var lido = await leitura.Movements.SingleAsync(m => m.Id == movimentacao.Id.Value, TestContext.Current.CancellationToken);

        lido.Amount.ShouldBe(valor);
        PersistenceMapper.ToDomain(lido).Amount.Amount.ShouldBe(valor);
    }

    [Fact]
    public async Task Soma_de_mil_centavos_no_banco_nao_acumula_erro()
    {
        // O mesmo cenário do teste de domínio, agora com a soma feita pelo
        // PostgreSQL: se o tipo da coluna estivesse errado, aqui apareceria.
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);

        await using (var contexto = fixture.CreateContext())
        {
            for (var i = 0; i < 1_000; i++)
            {
                contexto.Movements.Add(PersistenceMapper.ToRecord(
                    LedgerTestData.NovaMovimentacao(conta, 0.01m, NovaChave())));
            }

            await contexto.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var leitura = fixture.CreateContext();
        var soma = await leitura.Movements
            .Where(m => m.AccountId == conta.Id.Value)
            .SumAsync(m => m.Amount, TestContext.Current.CancellationToken);

        soma.ShouldBe(10m);
    }

    [Fact]
    public async Task Instante_sobrevive_a_ida_e_volta_mesmo_com_fuso_de_origem()
    {
        // O chamador pode enviar o instante em qualquer deslocamento. O Npgsql
        // mapeia DateTimeOffset para timestamptz, que guarda o instante e
        // descarta o deslocamento — e recusa gravar deslocamento diferente de
        // zero, daí a normalização no mapeamento.
        //
        // O deslocamento original não é preservado, e isso é aceitável: o razão
        // precisa saber o instante, não em que fuso quem registrou estava.
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var emBrasilia = new DateTimeOffset(2026, 11, 5, 21, 30, 0, TimeSpan.FromHours(-3));
        var movimentacao = LedgerTestData.NovaMovimentacao(
            conta, 10m, NovaChave(), ocorridaEm: emBrasilia);

        await using (var contexto = fixture.CreateContext())
        {
            await LedgerTestData.RegistrarAsync(contexto, movimentacao);
        }

        await using var leitura = fixture.CreateContext();
        var lido = await leitura.Movements.SingleAsync(m => m.Id == movimentacao.Id.Value, TestContext.Current.CancellationToken);

        lido.OccurredAt.ToUniversalTime().ShouldBe(emBrasilia.ToUniversalTime());
        lido.OccurredAt.UtcDateTime.Hour.ShouldBe(0, "21h30 em -03:00 é 00h30 UTC do dia seguinte");
        lido.OccurredAt.UtcDateTime.Day.ShouldBe(6);
    }

    [Fact]
    public async Task Movimentacao_de_dominio_sobrevive_a_ida_e_volta_completa()
    {
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var original = conta.Register(
            MovementType.Debit,
            Money.Of(4_321.8765m, conta.Currency),
            new DateTimeOffset(2026, 10, 1, 8, 15, 30, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 8, 15, 31, TimeSpan.Zero),
            IdempotencyKey.Of(NovaChave()),
            "corr-ida-e-volta",
            externalReference: "PIX-E3012-ABC");

        await using (var contexto = fixture.CreateContext())
        {
            await LedgerTestData.RegistrarAsync(contexto, original);
        }

        await using var leitura = fixture.CreateContext();
        var reconstruida = PersistenceMapper.ToDomain(
            await leitura.Movements.SingleAsync(m => m.Id == original.Id.Value, TestContext.Current.CancellationToken));

        reconstruida.Id.ShouldBe(original.Id);
        reconstruida.AccountId.ShouldBe(original.AccountId);
        reconstruida.Type.ShouldBe(original.Type);
        reconstruida.Amount.ShouldBe(original.Amount);
        reconstruida.OccurredAt.ShouldBe(original.OccurredAt);
        reconstruida.PostedAt.ShouldBe(original.PostedAt);
        reconstruida.IdempotencyKey.ShouldBe(original.IdempotencyKey);
        reconstruida.CorrelationId.ShouldBe(original.CorrelationId);
        reconstruida.ExternalReference.ShouldBe(original.ExternalReference);
        reconstruida.SignedAmount.Amount.ShouldBe(-4_321.8765m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Esquema_recusa_valor_nao_positivo_mesmo_contornando_o_dominio(decimal valor)
    {
        // Defesa em profundidade. O domínio já recusa, mas um caminho de código
        // que escreva direto no record — importação em lote, correção manual,
        // um bug — ainda é barrado pelo banco.
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);

        await using var contexto = fixture.CreateContext();
        contexto.Movements.Add(new MovementRecord
        {
            Id = Guid.CreateVersion7(),
            AccountId = conta.Id.Value,
            Type = MovementType.Credit,
            Amount = valor,
            Currency = conta.Currency.Code,
            OccurredAt = DateTimeOffset.UtcNow,
            PostedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = NovaChave(),
            CorrelationId = "corr-contorno",
        });

        var erro = await Should.ThrowAsync<DbUpdateException>(() => contexto.SaveChangesAsync(TestContext.Current.CancellationToken));

        // 23514 = check_violation
        erro.InnerException.ShouldBeOfType<PostgresException>()
            .SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Esquema_recusa_movimentacao_em_conta_inexistente()
    {
        fixture.SkipIfUnavailable();

        await using var contexto = fixture.CreateContext();
        contexto.Movements.Add(new MovementRecord
        {
            Id = Guid.CreateVersion7(),
            AccountId = Guid.CreateVersion7(),
            Type = MovementType.Credit,
            Amount = 10m,
            Currency = "BRL",
            OccurredAt = DateTimeOffset.UtcNow,
            PostedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = NovaChave(),
            CorrelationId = "corr-conta-inexistente",
        });

        var erro = await Should.ThrowAsync<DbUpdateException>(() => contexto.SaveChangesAsync(TestContext.Current.CancellationToken));

        erro.InnerException.ShouldBeOfType<PostgresException>()
            .SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Snapshot_de_saldo_sobrevive_a_ida_e_volta()
    {
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var original = BalanceSnapshot.Create(
            conta.Id,
            new DateOnly(2026, 11, 4),
            Money.Of(12_500.4321m, conta.Currency),
            movementCount: 1_842);

        await using (var contexto = fixture.CreateContext())
        {
            contexto.BalanceSnapshots.Add(PersistenceMapper.ToRecord(original));
            await contexto.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var leitura = fixture.CreateContext();
        var reconstruido = PersistenceMapper.ToDomain(
            await leitura.BalanceSnapshots.SingleAsync(
                s => s.AccountId == conta.Id.Value && s.AsOfDate == original.AsOfDate, TestContext.Current.CancellationToken));

        reconstruido.ClosingBalance.ShouldBe(original.ClosingBalance);
        reconstruido.MovementCount.ShouldBe(original.MovementCount);
        reconstruido.IsStale.ShouldBeFalse();
    }

    [Fact]
    public async Task Conta_sobrevive_a_ida_e_volta_com_a_situacao()
    {
        fixture.SkipIfUnavailable();
        var bloqueada = await LedgerTestData.CriarContaAsync(fixture, AccountStatus.Blocked);

        await using var leitura = fixture.CreateContext();
        var reconstruida = PersistenceMapper.ToDomain(
            await leitura.Accounts.SingleAsync(c => c.Id == bloqueada.Id.Value, TestContext.Current.CancellationToken));

        reconstruida.Status.ShouldBe(AccountStatus.Blocked);
        reconstruida.Currency.ShouldBe(bloqueada.Currency);
        reconstruida.CustomerId.ShouldBe(bloqueada.CustomerId);
    }
}
