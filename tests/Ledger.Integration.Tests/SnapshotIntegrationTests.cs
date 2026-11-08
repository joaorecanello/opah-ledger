using Ledger.Application.Abstractions;
using Ledger.Application.Balances;
using Ledger.Application.Movements;
using Ledger.Application.Security;
using Ledger.Domain;
using Ledger.Infrastructure;
using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Integration.Tests;

/// <summary>
/// O cache de saldo contra PostgreSQL real.
/// </summary>
/// <remarks>
/// A lógica de consolidação e reconciliação é verificada nos testes de
/// aplicação. O que está aqui é o que o falso em memória não prova: que os dois
/// caminhos de cálculo devolvem <b>exatamente</b> o mesmo número sobre dados
/// reais, e que a invalidação por retroatividade é atômica com a gravação do
/// fato.
/// </remarks>
[Collection(PostgresDatabase.Name)]
public sealed class SnapshotIntegrationTests(PostgresFixture fixture)
{
    private static string NovaChave() => $"chave-{Guid.CreateVersion7():N}";

    private static RegisterMovementHandler Registro(LedgerDbContext contexto) =>
        new(
            new LedgerRepository(contexto),
            FixedCallerAccess.Unrestricted,
            new SystemClock(),
            // O histórico dos testes é de meses atrás; a política padrão de
            // retroatividade é de 30 dias.
            new RegisterMovementOptions { MaxBackdating = TimeSpan.FromDays(3650) });

    private static GetBalanceHandler Consulta(
        LedgerDbContext contexto,
        GetBalanceOptions? opcoes = null) =>
        new(new LedgerRepository(contexto), FixedCallerAccess.Unrestricted, new SnapshotStore(contexto), new SystemClock(), opcoes);

    private static ConsolidateSnapshotsHandler Consolidacao(LedgerDbContext contexto) =>
        new(new LedgerRepository(contexto), new SnapshotStore(contexto), new SystemClock());

    private static ReconcileSnapshotsHandler Reconciliacao(
        LedgerDbContext contexto,
        ReconcileSnapshotsOptions? opcoes = null) =>
        new(new LedgerRepository(contexto), new SnapshotStore(contexto), opcoes);

    private static async Task RegistrarAsync(
        PostgresFixture fixture,
        Account conta,
        decimal valor,
        MovementType tipo,
        DateTimeOffset ocorridaEm)
    {
        await using var contexto = fixture.CreateContext();

        var resultado = await Registro(contexto).HandleAsync(
            new RegisterMovementCommand(
                conta.Id.Value, tipo, valor, conta.Currency.Code, ocorridaEm,
                NovaChave(), $"corr-{Guid.CreateVersion7():N}"),
            TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.Registered);
    }

    [Fact]
    public async Task Os_dois_caminhos_de_calculo_devolvem_o_mesmo_numero()
    {
        // ESTE é o teste que torna o cache aceitável em dados financeiros.
        //
        // 300 movimentações espalhadas por 60 dias, consolidadas, e depois o
        // saldo consultado em 25 instantes diferentes pelos dois caminhos: o
        // rápido, que parte do fechamento de um dia encerrado, e o direto, que
        // soma o histórico. Qualquer discrepância — numa fronteira de dia, num
        // arredondamento, num intervalo meio aberto — aparece aqui.
        //
        // É o mesmo papel do oráculo que já compara o SQL com o cálculo do
        // domínio, um nível acima.
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var aleatorio = new Random(20261108);
        var inicio = DateTimeOffset.UtcNow.AddDays(-60);

        for (var i = 0; i < 300; i++)
        {
            await RegistrarAsync(
                fixture,
                conta,
                Math.Round((decimal)(aleatorio.NextDouble() * 500) + 0.0001m, 4),
                aleatorio.Next(3) == 0 ? MovementType.Debit : MovementType.Credit,
                inicio.AddMinutes(aleatorio.Next(0, 60 * 24 * 58)));
        }

        await using (var contexto = fixture.CreateContext())
        {
            var relatorio = await Consolidacao(contexto)
                .ConsolidarContaAsync(conta.Id, TestContext.Current.CancellationToken);
            relatorio.ShouldBeGreaterThan(0, "a consolidação precisa ter gravado algo");
        }

        await using var leitura = fixture.CreateContext();
        var comSnapshot = Consulta(leitura);
        var direto = Consulta(leitura, new GetBalanceOptions { UseSnapshots = false });

        var usouSnapshotAoMenosUmaVez = false;

        for (var dias = 59; dias >= 0; dias -= 2)
        {
            var corte = DateTimeOffset.UtcNow.AddDays(-dias);

            var rapido = await comSnapshot.HandleAsync(
                new GetBalanceQuery(conta.Id.Value, corte), TestContext.Current.CancellationToken);
            var lento = await direto.HandleAsync(
                new GetBalanceQuery(conta.Id.Value, corte), TestContext.Current.CancellationToken);

            rapido.Balance.ShouldBe(lento.Balance, $"divergência no corte de {corte:O}");
            rapido.MovementCount.ShouldBe(lento.MovementCount, $"contagem difere em {corte:O}");

            if (rapido.Source == BalanceSource.SnapshotPlusDelta)
            {
                usouSnapshotAoMenosUmaVez = true;
            }
        }

        usouSnapshotAoMenosUmaVez.ShouldBeTrue(
            "se nenhum corte usou o caminho rápido, o teste não comparou nada");
    }

    [Fact]
    public async Task Retroatividade_invalida_fechamentos_e_a_consulta_segue_correta()
    {
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var vinteDiasAtras = DateTimeOffset.UtcNow.AddDays(-20);

        await RegistrarAsync(fixture, conta, 1_000m, MovementType.Credit, vinteDiasAtras);

        await using (var contexto = fixture.CreateContext())
        {
            await Consolidacao(contexto)
                .ConsolidarContaAsync(conta.Id, TestContext.Current.CancellationToken);
        }

        // Chega um lançamento datado de dez dias atrás, depois de a
        // consolidação já ter fechado aquele período.
        await RegistrarAsync(
            fixture, conta, 400m, MovementType.Credit, DateTimeOffset.UtcNow.AddDays(-10));

        await using var leitura = fixture.CreateContext();

        var obsoletos = await leitura.BalanceSnapshots
            .Where(s => s.AccountId == conta.Id.Value && s.IsStale)
            .CountAsync(TestContext.Current.CancellationToken);

        obsoletos.ShouldBeGreaterThan(0, "a retroatividade tem que invalidar fechamentos");

        var resultado = await Consulta(leitura).HandleAsync(
            new GetBalanceQuery(conta.Id.Value), TestContext.Current.CancellationToken);

        resultado.Balance.ShouldBe(1_400m, "o número continua correto");
        resultado.Stale.ShouldBe(true, "a resposta sinaliza a degradação");

        var direto = await Consulta(leitura, new GetBalanceOptions { UseSnapshots = false })
            .HandleAsync(
                new GetBalanceQuery(conta.Id.Value), TestContext.Current.CancellationToken);
        resultado.Balance.ShouldBe(direto.Balance);
    }

    [Fact]
    public async Task Tentativa_duplicada_retroativa_nao_invalida_fechamento_nenhum()
    {
        // A atomicidade entre gravar o fato e invalidar fechamentos, verificada
        // pelo lado que importa: se a gravação é recusada, a invalidação não
        // pode ter acontecido.
        //
        // Sem a transação explícita, a tentativa duplicada marcaria fechamentos
        // como obsoletos sem registrar nada — degradando a consulta de uma conta
        // por causa de um retry que não mudou o razão.
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var quinzeDiasAtras = DateTimeOffset.UtcNow.AddDays(-15);
        var chave = NovaChave();

        await RegistrarAsync(fixture, conta, 500m, MovementType.Credit, quinzeDiasAtras.AddDays(-5));

        await using (var contexto = fixture.CreateContext())
        {
            await Consolidacao(contexto)
                .ConsolidarContaAsync(conta.Id, TestContext.Current.CancellationToken);
        }

        // Primeira tentativa retroativa: registra e invalida.
        await using (var contexto = fixture.CreateContext())
        {
            var primeira = await Registro(contexto).HandleAsync(
                new RegisterMovementCommand(
                    conta.Id.Value, MovementType.Credit, 100m, "BRL",
                    quinzeDiasAtras, chave, "corr"),
                TestContext.Current.CancellationToken);
            primeira.Status.ShouldBe(RegisterMovementStatus.Registered);
        }

        // Reconsolida, para que não haja nada obsoleto antes da segunda
        // tentativa.
        await using (var contexto = fixture.CreateContext())
        {
            await Consolidacao(contexto)
                .ConsolidarContaAsync(conta.Id, TestContext.Current.CancellationToken);
        }

        await using (var verificacao = fixture.CreateContext())
        {
            (await verificacao.BalanceSnapshots
                .CountAsync(s => s.AccountId == conta.Id.Value && s.IsStale,
                    TestContext.Current.CancellationToken))
                .ShouldBe(0, "a reconsolidação precisa ter limpado tudo");
        }

        // Segunda tentativa, mesma chave: tem que ser replay e não pode tocar
        // nos fechamentos.
        await using (var contexto = fixture.CreateContext())
        {
            var segunda = await Registro(contexto).HandleAsync(
                new RegisterMovementCommand(
                    conta.Id.Value, MovementType.Credit, 100m, "BRL",
                    quinzeDiasAtras, chave, "corr-outra"),
                TestContext.Current.CancellationToken);
            segunda.Status.ShouldBe(RegisterMovementStatus.Replayed);
        }

        await using var leitura = fixture.CreateContext();
        var obsoletos = await leitura.BalanceSnapshots
            .CountAsync(s => s.AccountId == conta.Id.Value && s.IsStale,
                TestContext.Current.CancellationToken);

        obsoletos.ShouldBe(0, "um retry recusado não degrada o cache");
    }

    [Fact]
    public async Task Fechamento_gravado_corresponde_ao_razao()
    {
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var dez = DateTimeOffset.UtcNow.AddDays(-10);

        await RegistrarAsync(fixture, conta, 700m, MovementType.Credit, dez);
        await RegistrarAsync(fixture, conta, 200.25m, MovementType.Debit, dez.AddDays(1));

        await using (var contexto = fixture.CreateContext())
        {
            await Consolidacao(contexto)
                .ConsolidarContaAsync(conta.Id, TestContext.Current.CancellationToken);
        }

        await using var leitura = fixture.CreateContext();
        var ultimo = await leitura.BalanceSnapshots
            .Where(s => s.AccountId == conta.Id.Value)
            .OrderByDescending(s => s.AsOfDate)
            .FirstAsync(TestContext.Current.CancellationToken);

        ultimo.ClosingBalance.ShouldBe(499.75m);
        ultimo.MovementCount.ShouldBe(2L);
        ultimo.IsStale.ShouldBeFalse();
    }

    [Fact]
    public async Task Reconciliacao_pega_fechamento_corrompido_direto_no_banco()
    {
        // Corrompe por SQL, contornando o domínio e o caso de uso — que é
        // exatamente como a corrupção aconteceria de verdade: migração mal
        // aplicada, correção manual em produção, defeito num caminho novo.
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);

        await RegistrarAsync(
            fixture, conta, 800m, MovementType.Credit, DateTimeOffset.UtcNow.AddDays(-8));

        await using (var contexto = fixture.CreateContext())
        {
            await Consolidacao(contexto)
                .ConsolidarContaAsync(conta.Id, TestContext.Current.CancellationToken);
        }

        DateOnly diaCorrompido;

        await using (var corrupcao = fixture.CreateContext())
        {
            diaCorrompido = await corrupcao.BalanceSnapshots
                .Where(s => s.AccountId == conta.Id.Value)
                .OrderBy(s => s.AsOfDate)
                .Select(s => s.AsOfDate)
                .FirstAsync(TestContext.Current.CancellationToken);

            await corrupcao.BalanceSnapshots
                .Where(s => s.AccountId == conta.Id.Value && s.AsOfDate == diaCorrompido)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(x => x.ClosingBalance, 123.4567m),
                    TestContext.Current.CancellationToken);
        }

        await using (var contexto = fixture.CreateContext())
        {
            var relatorio = await Reconciliacao(contexto)
                .ReconciliarContaAsync(conta.Id, TestContext.Current.CancellationToken);

            relatorio.IsClean.ShouldBeFalse();
            relatorio.Divergences.ShouldContain(d =>
                d.AsOfDate == diaCorrompido && d.StoredBalance == 123.4567m);
            relatorio.MarkedStale.ShouldBeGreaterThan(0);
        }

        // O saldo continua correto durante a divergência: a consulta recua para
        // um fechamento válido ou soma o histórico.
        await using var leitura = fixture.CreateContext();
        var resultado = await Consulta(leitura).HandleAsync(
            new GetBalanceQuery(conta.Id.Value), TestContext.Current.CancellationToken);

        resultado.Balance.ShouldBe(800m);

        // E a consolidação seguinte cura o cache.
        await using (var contexto = fixture.CreateContext())
        {
            await Consolidacao(contexto)
                .ConsolidarContaAsync(conta.Id, TestContext.Current.CancellationToken);
        }

        await using var depois = fixture.CreateContext();
        var limpo = await Reconciliacao(depois)
            .ReconciliarContaAsync(conta.Id, TestContext.Current.CancellationToken);

        limpo.IsClean.ShouldBeTrue();
    }

    [Fact]
    public async Task Truncar_o_cache_nao_muda_nenhum_saldo()
    {
        // A propriedade que torna a otimização aceitável: o cache é descartável.
        // Na dúvida sobre um número, joga fora e recalcula.
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var trinta = DateTimeOffset.UtcNow.AddDays(-30);

        for (var i = 0; i < 20; i++)
        {
            await RegistrarAsync(
                fixture, conta, 50m + i, MovementType.Credit, trinta.AddDays(i));
        }

        await using (var contexto = fixture.CreateContext())
        {
            await Consolidacao(contexto)
                .ConsolidarContaAsync(conta.Id, TestContext.Current.CancellationToken);
        }

        decimal comCache;

        await using (var leitura = fixture.CreateContext())
        {
            comCache = (await Consulta(leitura).HandleAsync(
                new GetBalanceQuery(conta.Id.Value), TestContext.Current.CancellationToken))
                .Balance!.Value;
        }

        await using (var limpeza = fixture.CreateContext())
        {
            await limpeza.BalanceSnapshots
                .Where(s => s.AccountId == conta.Id.Value)
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }

        await using var depois = fixture.CreateContext();
        var semCache = await Consulta(depois).HandleAsync(
            new GetBalanceQuery(conta.Id.Value), TestContext.Current.CancellationToken);

        semCache.Balance.ShouldBe(comCache);
        semCache.Source.ShouldBe(BalanceSource.DirectSum);
    }
}
