using Ledger.Application.Balances;
using Ledger.Application.Movements;
using Ledger.Domain;

namespace Ledger.Application.Tests;

/// <summary>
/// Consolidação, invalidação por retroatividade e reconciliação.
/// </summary>
/// <remarks>
/// Aqui se verifica a <b>lógica</b>: até onde consolidar, de onde recomeçar
/// quando há obsoleto, e o que a reconciliação faz ao achar divergência. A
/// equivalência numérica contra SQL real e a atomicidade entre gravar o fato e
/// invalidar fechamentos ficam nos testes de integração.
/// </remarks>
public sealed class SnapshotHandlersTests
{
    // Meio-dia de 8 de novembro: o último dia encerrado é 7 de novembro.
    private static readonly DateTimeOffset Agora = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly UltimoDiaFechado = new(2026, 11, 7);

    private readonly FakeSnapshotStore _snapshots = new();
    private readonly FakeLedgerRepository _repositorio;
    private readonly FixedClock _relogio = new(Agora);
    private readonly Account _conta =
        Account.Open(AccountId.New(), CustomerId.New(), Currency.Brl);

    public SnapshotHandlersTests()
    {
        _repositorio = new FakeLedgerRepository(_snapshots);
        _repositorio.Adicionar(_conta);
    }

    private ConsolidateSnapshotsHandler Consolidacao(ConsolidateSnapshotsOptions? opcoes = null) =>
        new(_repositorio, _snapshots, _relogio, opcoes);

    private ReconcileSnapshotsHandler Reconciliacao(ReconcileSnapshotsOptions? opcoes = null) =>
        new(_repositorio, _snapshots, opcoes);

    private GetBalanceHandler Consulta(GetBalanceOptions? opcoes = null) =>
        new(_repositorio, _snapshots, _relogio, opcoes);

    private async Task Registrar(decimal valor, MovementType tipo, DateTimeOffset ocorridaEm)
    {
        var handler = new RegisterMovementHandler(
            _repositorio,
            _relogio,
            new RegisterMovementOptions { MaxBackdating = TimeSpan.FromDays(3650) });

        var resultado = await handler.HandleAsync(
            new RegisterMovementCommand(
                _conta.Id.Value, tipo, valor, "BRL", ocorridaEm,
                $"chave-{Guid.CreateVersion7():N}", "corr"),
            TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.Registered);
    }

    private static DateTimeOffset Dia(int diaDeNovembro, int hora = 10) =>
        new(2026, 11, diaDeNovembro, hora, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Consolida_apenas_dias_encerrados()
    {
        // O dia corrente nunca é consolidado: ele ainda pode receber
        // movimentação, e um fechamento parcial seria um número errado com
        // aparência de definitivo.
        await Registrar(100m, MovementType.Credit, Dia(5));
        await Registrar(50m, MovementType.Credit, Dia(8));

        await Consolidacao().HandleAsync(TestContext.Current.CancellationToken);

        var gravados = await _snapshots.ListAsync(
            _conta.Id, TestContext.Current.CancellationToken);

        gravados.ShouldNotBeEmpty();
        gravados.Max(s => s.AsOfDate).ShouldBe(UltimoDiaFechado);
        gravados.ShouldNotContain(s => s.AsOfDate >= new DateOnly(2026, 11, 8));
    }

    [Fact]
    public async Task Fechamento_acumula_o_saldo_dia_a_dia()
    {
        await Registrar(1_000m, MovementType.Credit, Dia(5));
        await Registrar(300m, MovementType.Debit, Dia(6));
        await Registrar(50m, MovementType.Credit, Dia(7));

        await Consolidacao().HandleAsync(TestContext.Current.CancellationToken);

        var porDia = (await _snapshots.ListAsync(_conta.Id, TestContext.Current.CancellationToken))
            .ToDictionary(s => s.AsOfDate, s => s.ClosingBalance.Amount);

        porDia[new DateOnly(2026, 11, 5)].ShouldBe(1_000m);
        porDia[new DateOnly(2026, 11, 6)].ShouldBe(700m);
        porDia[new DateOnly(2026, 11, 7)].ShouldBe(750m);
    }

    [Fact]
    public async Task Segunda_passada_nao_regrava_o_que_ja_esta_consolidado()
    {
        await Registrar(100m, MovementType.Credit, Dia(5));
        await Consolidacao().HandleAsync(TestContext.Current.CancellationToken);

        var segunda = await Consolidacao().HandleAsync(TestContext.Current.CancellationToken);

        segunda.SnapshotsWritten.ShouldBe(0, "uma passada sem trabalho a fazer não grava nada");
    }

    [Fact]
    public async Task Consulta_usa_o_fechamento_quando_existe()
    {
        await Registrar(1_000m, MovementType.Credit, Dia(5));
        await Registrar(250m, MovementType.Debit, Dia(8, hora: 9));
        await Consolidacao().HandleAsync(TestContext.Current.CancellationToken);

        var resultado = await Consulta().HandleAsync(
            new GetBalanceQuery(_conta.Id.Value), TestContext.Current.CancellationToken);

        resultado.Balance.ShouldBe(750m);
        resultado.Source.ShouldBe(BalanceSource.SnapshotPlusDelta);
        resultado.SnapshotDate.ShouldBe(UltimoDiaFechado);
        resultado.Stale.ShouldBe(false);
    }

    [Fact]
    public async Task Desligar_o_snapshot_forca_o_calculo_direto_e_o_mesmo_numero()
    {
        // O mesmo número pelos dois caminhos. Em miniatura aqui; com banco e
        // datas aleatórias, nos testes de integração.
        await Registrar(1_000m, MovementType.Credit, Dia(5));
        await Registrar(250m, MovementType.Debit, Dia(6));
        await Consolidacao().HandleAsync(TestContext.Current.CancellationToken);

        var comSnapshot = await Consulta().HandleAsync(
            new GetBalanceQuery(_conta.Id.Value), TestContext.Current.CancellationToken);
        var direto = await Consulta(new GetBalanceOptions { UseSnapshots = false }).HandleAsync(
            new GetBalanceQuery(_conta.Id.Value), TestContext.Current.CancellationToken);

        comSnapshot.Source.ShouldBe(BalanceSource.SnapshotPlusDelta);
        direto.Source.ShouldBe(BalanceSource.DirectSum);
        comSnapshot.Balance.ShouldBe(direto.Balance);
        comSnapshot.MovementCount.ShouldBe(direto.MovementCount);
    }

    [Fact]
    public async Task Movimentacao_retroativa_invalida_os_fechamentos_a_partir_do_dia_do_fato()
    {
        await Registrar(1_000m, MovementType.Credit, Dia(3));
        await Consolidacao().HandleAsync(TestContext.Current.CancellationToken);

        // Chega agora um lançamento datado de 5 de novembro.
        await Registrar(400m, MovementType.Credit, Dia(5));

        var depois = await _snapshots.ListAsync(_conta.Id, TestContext.Current.CancellationToken);

        depois.Where(s => s.AsOfDate < new DateOnly(2026, 11, 5))
            .ShouldAllBe(s => !s.IsStale, "dias anteriores ao fato continuam válidos");
        depois.Where(s => s.AsOfDate >= new DateOnly(2026, 11, 5))
            .ShouldAllBe(s => s.IsStale, "o dia do fato e os posteriores foram invalidados");
    }

    [Fact]
    public async Task Consulta_recua_para_o_fechamento_valido_e_sinaliza_a_degradacao()
    {
        // A degradação é proporcional ao quanto a retroatividade alcançou, não
        // total: em lugar de desistir do cache, a consulta recua para o último
        // fechamento que ainda vale.
        await Registrar(1_000m, MovementType.Credit, Dia(3));
        await Consolidacao().HandleAsync(TestContext.Current.CancellationToken);
        await Registrar(400m, MovementType.Credit, Dia(5));

        var resultado = await Consulta().HandleAsync(
            new GetBalanceQuery(_conta.Id.Value), TestContext.Current.CancellationToken);

        resultado.Balance.ShouldBe(1_400m, "o número continua correto");
        resultado.Stale.ShouldBe(true, "a resposta diz que custou mais caro");
        resultado.SnapshotDate.ShouldBe(new DateOnly(2026, 11, 4),
            "recuou para o último dia que a retroatividade não alcançou");
    }

    [Fact]
    public async Task Consolidacao_seguinte_recalcula_o_que_foi_invalidado()
    {
        await Registrar(1_000m, MovementType.Credit, Dia(3));
        await Consolidacao().HandleAsync(TestContext.Current.CancellationToken);
        await Registrar(400m, MovementType.Credit, Dia(5));

        var relatorio = await Consolidacao().HandleAsync(TestContext.Current.CancellationToken);

        relatorio.SnapshotsWritten.ShouldBeGreaterThan(0);

        var depois = await _snapshots.ListAsync(_conta.Id, TestContext.Current.CancellationToken);
        depois.ShouldAllBe(s => !s.IsStale, "nada ficou obsoleto");
        depois.Single(s => s.AsOfDate == UltimoDiaFechado)
            .ClosingBalance.Amount.ShouldBe(1_400m);

        var resultado = await Consulta().HandleAsync(
            new GetBalanceQuery(_conta.Id.Value), TestContext.Current.CancellationToken);
        resultado.Stale.ShouldBe(false, "o caminho rápido voltou");
        resultado.Balance.ShouldBe(1_400m);
    }

    [Fact]
    public async Task Reconciliacao_nao_acusa_nada_quando_o_cache_corresponde_ao_razao()
    {
        await Registrar(500m, MovementType.Credit, Dia(5));
        await Consolidacao().HandleAsync(TestContext.Current.CancellationToken);

        var relatorio = await Reconciliacao().HandleAsync(TestContext.Current.CancellationToken);

        relatorio.IsClean.ShouldBeTrue();
        relatorio.SnapshotsChecked.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Reconciliacao_encontra_fechamento_corrompido_e_marca_para_recalculo()
    {
        // O cenário que a reconciliação existe para pegar: o cache divergiu por
        // um motivo que não é retroatividade — defeito de código, alteração
        // manual no banco, migração mal aplicada. Sem ela, a divergência
        // apareceria quando um cliente contestasse o extrato.
        await Registrar(500m, MovementType.Credit, Dia(5));
        await Consolidacao().HandleAsync(TestContext.Current.CancellationToken);

        _snapshots.Corromper(_conta.Id, new DateOnly(2026, 11, 6), Money.Of(999m, Currency.Brl));

        var relatorio = await Reconciliacao().HandleAsync(TestContext.Current.CancellationToken);

        relatorio.IsClean.ShouldBeFalse();
        relatorio.Divergences.ShouldContain(d =>
            d.AsOfDate == new DateOnly(2026, 11, 6)
            && d.StoredBalance == 999m
            && d.RecomputedBalance == 500m);
        relatorio.MarkedStale.ShouldBeGreaterThan(0);

        // A partir do dia divergente, não apenas nele: o fechamento é acumulado,
        // logo um erro num dia contamina todos os posteriores.
        var depois = await _snapshots.ListAsync(_conta.Id, TestContext.Current.CancellationToken);
        depois.Where(s => s.AsOfDate >= new DateOnly(2026, 11, 6))
            .ShouldAllBe(s => s.IsStale);
    }

    [Fact]
    public async Task Reconciliacao_e_consolidacao_juntas_curam_o_cache()
    {
        await Registrar(500m, MovementType.Credit, Dia(5));
        await Consolidacao().HandleAsync(TestContext.Current.CancellationToken);
        _snapshots.Corromper(_conta.Id, new DateOnly(2026, 11, 6), Money.Of(999m, Currency.Brl));

        await Reconciliacao().HandleAsync(TestContext.Current.CancellationToken);
        await Consolidacao().HandleAsync(TestContext.Current.CancellationToken);

        var relatorio = await Reconciliacao().HandleAsync(TestContext.Current.CancellationToken);

        relatorio.IsClean.ShouldBeTrue(
            "o sistema se corrige com a maquinaria que já existe para retroatividade");
    }

    [Fact]
    public async Task Reconciliacao_pode_so_relatar_sem_marcar()
    {
        // Serve à investigação: às vezes é preciso olhar o estado divergente
        // antes de ele ser substituído.
        await Registrar(500m, MovementType.Credit, Dia(5));
        await Consolidacao().HandleAsync(TestContext.Current.CancellationToken);
        _snapshots.Corromper(_conta.Id, new DateOnly(2026, 11, 6), Money.Of(999m, Currency.Brl));

        var relatorio = await Reconciliacao(
            new ReconcileSnapshotsOptions { MarkDivergentAsStale = false })
            .HandleAsync(TestContext.Current.CancellationToken);

        relatorio.IsClean.ShouldBeFalse();
        relatorio.MarkedStale.ShouldBe(0);
        (await _snapshots.ListAsync(_conta.Id, TestContext.Current.CancellationToken))
            .ShouldAllBe(s => !s.IsStale);
    }

    [Fact]
    public async Task Conta_sem_movimentacao_nao_gera_fechamento()
    {
        var relatorio = await Consolidacao().HandleAsync(TestContext.Current.CancellationToken);

        relatorio.AccountsVisited.ShouldBe(1);
        relatorio.SnapshotsWritten.ShouldBe(0);
        _snapshots.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Limite_de_dias_por_passada_e_respeitado_e_a_passada_seguinte_avanca()
    {
        // Evita que uma conta com histórico longo e nenhum snapshot ocupe o
        // banco numa passada única e sem sinal de progresso.
        await Registrar(10m, MovementType.Credit, Dia(1));

        var limitado = new ConsolidateSnapshotsOptions { MaxDaysPerAccountPerRun = 2 };

        var primeira = await Consolidacao(limitado).HandleAsync(TestContext.Current.CancellationToken);
        primeira.SnapshotsWritten.ShouldBe(2);

        var segunda = await Consolidacao(limitado).HandleAsync(TestContext.Current.CancellationToken);
        segunda.SnapshotsWritten.ShouldBe(2);

        (await _snapshots.FindLatestDateAsync(_conta.Id, TestContext.Current.CancellationToken))
            .ShouldBe(new DateOnly(2026, 11, 4));
    }
}
