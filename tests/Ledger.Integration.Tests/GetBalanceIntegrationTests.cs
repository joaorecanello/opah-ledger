using Ledger.Application.Balances;
using Ledger.Application.Movements;
using Ledger.Domain;
using Ledger.Infrastructure;
using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Integration.Tests;

/// <summary>
/// A consulta de saldo contra PostgreSQL real.
/// </summary>
/// <remarks>
/// A decisão do caso de uso é verificada nos testes de aplicação. O que está
/// aqui é a <b>aritmética do banco</b>: a soma acontece em SQL, com um
/// <c>CASE WHEN</c> que repete a convenção de sinal definida em
/// <c>Movement.SignedAmount</c>.
/// <para>
/// Essa duplicação é consciente — somar em memória seria inviável no volume
/// real. O que impede as duas de divergirem é o teste de equivalência deste
/// arquivo.
/// </para>
/// </remarks>
[Collection(PostgresDatabase.Name)]
public sealed class GetBalanceIntegrationTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Base = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static string NovaChave() => $"chave-{Guid.CreateVersion7():N}";

    private static GetBalanceHandler Consulta(LedgerDbContext contexto) =>
        new(new LedgerRepository(contexto), new SystemClock());

    private static async Task RegistrarAsync(
        PostgresFixture fixture,
        Account conta,
        decimal valor,
        MovementType tipo,
        DateTimeOffset ocorridaEm)
    {
        await using var contexto = fixture.CreateContext();
        var handler = new RegisterMovementHandler(
            new LedgerRepository(contexto),
            new SystemClock(),
            // O histórico dos testes é de meses atrás; a política de
            // retroatividade padrão é de 30 dias.
            new RegisterMovementOptions { MaxBackdating = TimeSpan.FromDays(3650) });

        var resultado = await handler.HandleAsync(
            new RegisterMovementCommand(
                conta.Id.Value, tipo, valor, conta.Currency.Code, ocorridaEm,
                NovaChave(), $"corr-{Guid.CreateVersion7():N}"),
            TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.Registered);
    }

    [Fact]
    public async Task Conta_sem_movimentacao_tem_saldo_zero_e_nao_erro()
    {
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);

        await using var contexto = fixture.CreateContext();
        var resultado = await Consulta(contexto).HandleAsync(
            new GetBalanceQuery(conta.Id.Value), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(GetBalanceStatus.Found);
        resultado.Balance.ShouldBe(0m);
        resultado.MovementCount.ShouldBe(0L);
    }

    [Fact]
    public async Task Saldo_em_tres_instantes_distintos_acompanha_o_historico()
    {
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var primeira = Base;
        var segunda = Base.AddDays(5);

        await RegistrarAsync(fixture, conta, 1_500m, MovementType.Credit, primeira);
        await RegistrarAsync(fixture, conta, 400.25m, MovementType.Debit, segunda);

        await using var contexto = fixture.CreateContext();
        var handler = Consulta(contexto);

        var antes = await handler.HandleAsync(
            new GetBalanceQuery(conta.Id.Value, primeira.AddSeconds(-1)),
            TestContext.Current.CancellationToken);
        var meio = await handler.HandleAsync(
            new GetBalanceQuery(conta.Id.Value, segunda.AddSeconds(-1)),
            TestContext.Current.CancellationToken);
        var depois = await handler.HandleAsync(
            new GetBalanceQuery(conta.Id.Value, segunda.AddDays(1)),
            TestContext.Current.CancellationToken);

        antes.Balance.ShouldBe(0m);
        meio.Balance.ShouldBe(1_500m);
        depois.Balance.ShouldBe(1_099.75m);
        depois.MovementCount.ShouldBe(2L);
    }

    [Fact]
    public async Task Corte_e_inclusivo_no_instante_exato_da_movimentacao()
    {
        // O instante é alinhado ao segundo de propósito: o timestamptz guarda
        // microssegundos, e testar a fronteira com precisão de tick compararia
        // contra um valor que o banco não consegue representar.
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var instante = Base.AddHours(7);

        await RegistrarAsync(fixture, conta, 999.99m, MovementType.Credit, instante);

        await using var contexto = fixture.CreateContext();
        var handler = Consulta(contexto);

        var noInstante = await handler.HandleAsync(
            new GetBalanceQuery(conta.Id.Value, instante),
            TestContext.Current.CancellationToken);
        var umMicrossegundoAntes = await handler.HandleAsync(
            new GetBalanceQuery(conta.Id.Value, instante.AddTicks(-SystemClock.TicksPerMicrosecond)),
            TestContext.Current.CancellationToken);

        noInstante.Balance.ShouldBe(999.99m);
        umMicrossegundoAntes.Balance.ShouldBe(0m);
    }

    [Fact]
    public async Task Soma_do_banco_equivale_ao_calculo_do_dominio()
    {
        // ESTE é o teste que torna a duplicação da convenção de sinal aceitável.
        //
        // A soma acontece em SQL, com um CASE WHEN que repete o que
        // Movement.SignedAmount define em C#. Se alguém mudar um dos dois — ou
        // adicionar um terceiro tipo de movimentação, que o CASE WHEN trataria
        // silenciosamente como débito — os números divergem e este teste falha.
        //
        // É a mesma ideia do oráculo que vai verificar o snapshot de saldo, um
        // nível antes.
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var aleatorio = new Random(20261108);

        for (var i = 0; i < 120; i++)
        {
            await RegistrarAsync(
                fixture,
                conta,
                Math.Round((decimal)(aleatorio.NextDouble() * 900) + 0.0001m, 4),
                aleatorio.Next(3) == 0 ? MovementType.Debit : MovementType.Credit,
                Base.AddMinutes(aleatorio.Next(0, 60 * 24 * 30)));
        }

        var corte = Base.AddDays(40);

        await using var contexto = fixture.CreateContext();

        var peloBanco = await Consulta(contexto).HandleAsync(
            new GetBalanceQuery(conta.Id.Value, corte), TestContext.Current.CancellationToken);

        // Lado do domínio: carrega os fatos e soma com BalanceCalculator.
        var movimentacoes = await contexto.Movements
            .Where(m => m.AccountId == conta.Id.Value && m.OccurredAt <= corte)
            .ToListAsync(TestContext.Current.CancellationToken);
        var peloDominio = BalanceCalculator.From(
            conta.Currency,
            movimentacoes.Select(PersistenceMapper.ToDomain));

        peloBanco.Balance.ShouldBe(peloDominio.Amount);
        peloBanco.MovementCount.ShouldBe(movimentacoes.Count);
    }

    [Fact]
    public async Task Soma_do_banco_preserva_as_quatro_casas_decimais()
    {
        // Se o tipo da coluna ou a agregação perdessem precisão, apareceria
        // aqui: 2.000 somas de fração de centavo têm que fechar exatamente.
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);

        await using (var escrita = fixture.CreateContext())
        {
            for (var i = 0; i < 2_000; i++)
            {
                escrita.Movements.Add(PersistenceMapper.ToRecord(
                    LedgerTestData.NovaMovimentacao(conta, 0.0001m, NovaChave(),
                        ocorridaEm: Base.AddSeconds(i))));
            }

            await escrita.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var contexto = fixture.CreateContext();
        var resultado = await Consulta(contexto).HandleAsync(
            new GetBalanceQuery(conta.Id.Value, Base.AddDays(1)),
            TestContext.Current.CancellationToken);

        resultado.Balance.ShouldBe(0.2m);
        resultado.MovementCount.ShouldBe(2_000L);
    }

    [Fact]
    public async Task Debito_sem_lastro_produz_saldo_negativo()
    {
        // PR-03 ponta a ponta: o razão registra o fato consumado e o saldo
        // negativo é resultado possível, não erro deste sistema.
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);

        await RegistrarAsync(fixture, conta, 100m, MovementType.Credit, Base);
        await RegistrarAsync(fixture, conta, 450m, MovementType.Debit, Base.AddHours(1));

        await using var contexto = fixture.CreateContext();
        var resultado = await Consulta(contexto).HandleAsync(
            new GetBalanceQuery(conta.Id.Value, Base.AddDays(1)),
            TestContext.Current.CancellationToken);

        resultado.Balance.ShouldBe(-350m);
    }

    [Fact]
    public async Task Saldo_de_uma_conta_nao_vaza_para_outra()
    {
        fixture.SkipIfUnavailable();
        var minha = await LedgerTestData.CriarContaAsync(fixture);
        var outra = await LedgerTestData.CriarContaAsync(fixture);

        await RegistrarAsync(fixture, minha, 77m, MovementType.Credit, Base);
        await RegistrarAsync(fixture, outra, 5_000m, MovementType.Credit, Base);

        await using var contexto = fixture.CreateContext();
        var resultado = await Consulta(contexto).HandleAsync(
            new GetBalanceQuery(minha.Id.Value, Base.AddDays(1)),
            TestContext.Current.CancellationToken);

        resultado.Balance.ShouldBe(77m);
        resultado.MovementCount.ShouldBe(1L);
    }

    [Fact]
    public async Task Conta_inexistente_nao_devolve_saldo_zero()
    {
        fixture.SkipIfUnavailable();

        await using var contexto = fixture.CreateContext();
        var resultado = await Consulta(contexto).HandleAsync(
            new GetBalanceQuery(Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(GetBalanceStatus.AccountNotFound);
        resultado.Balance.ShouldBeNull();
    }

    [Fact]
    public async Task A_soma_acontece_no_banco_e_nao_em_memoria()
    {
        // O ponto do ledger append-only é não varrer o histórico a cada
        // consulta. Se o EF trouxesse as linhas para somar em memória, o
        // resultado seria o mesmo e o desempenho seria o do sistema que este
        // desenho existe para substituir — ou seja, o defeito passaria por todos
        // os outros testes.
        //
        // Daí verificar a tradução, e não só o número: o SQL precisa conter a
        // agregação e o filtro.
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        await RegistrarAsync(fixture, conta, 10m, MovementType.Credit, Base);

        var comandos = new List<string>();
        await using var contexto = fixture.CreateContext(comandos.Add);

        await Consulta(contexto).HandleAsync(
            new GetBalanceQuery(conta.Id.Value, Base.AddDays(1)),
            TestContext.Current.CancellationToken);

        var consultaDeSoma = comandos
            .FirstOrDefault(c => c.Contains("sum(", StringComparison.OrdinalIgnoreCase));

        consultaDeSoma.ShouldNotBeNull(
            "a soma precisa ser uma agregação no banco. Comandos emitidos:\n"
            + string.Join("\n---\n", comandos));

        // A convenção de sinal vira um CASE WHEN no SQL. É a duplicação que o
        // teste de equivalência protege.
        consultaDeSoma.ShouldContain("CASE", Case.Insensitive);

        // O filtro por conta e instante é o que usa o índice
        // ix_movements_account_id_occurred_at.
        consultaDeSoma.ShouldContain("account_id", Case.Insensitive);
        consultaDeSoma.ShouldContain("occurred_at", Case.Insensitive);

        // E nenhuma consulta devolveu as linhas de movimentação em si.
        comandos.ShouldNotContain(
            c => c.Contains("m.correlation_id", StringComparison.OrdinalIgnoreCase),
            "nenhuma consulta deve materializar as movimentações para somar");
    }
}
