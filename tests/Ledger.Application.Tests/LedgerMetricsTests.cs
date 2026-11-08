using Ledger.Application.Balances;
using Ledger.Application.Movements;
using Ledger.Application.Observability;
using Ledger.Application.Security;
using Ledger.Domain;

namespace Ledger.Application.Tests;

/// <summary>
/// As métricas de negócio do razão.
/// </summary>
/// <remarks>
/// Métrica técnica mede o veículo; métrica de negócio mede se a carga chegou. Um
/// painel com latência saudável e taxa de erro zero pode descrever um sistema que
/// perdeu movimentações em silêncio, e só a segunda coisa é emergência — então
/// estas séries precisam estar certas, não só existir.
/// </remarks>
public sealed class LedgerMetricsTests : IDisposable
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly TestMeterFactory _fabrica = new();
    private readonly LedgerMetrics _metricas;
    private readonly ColetorDeMetricas _coletor;
    private readonly FakeSnapshotStore _snapshots = new();
    private readonly FakeLedgerRepository _repositorio;
    private readonly FixedClock _relogio = new(Agora);
    private readonly Account _conta =
        Account.Open(AccountId.New(), CustomerId.New(), Currency.Brl);

    public LedgerMetricsTests()
    {
        _metricas = new LedgerMetrics(_fabrica);
        _coletor = new ColetorDeMetricas(_metricas);
        _repositorio = new FakeLedgerRepository(_snapshots);
        _repositorio.Adicionar(_conta);
    }

    public void Dispose()
    {
        _coletor.Dispose();
        _metricas.Dispose();
        _fabrica.Dispose();
    }

    private RegisterMovementHandler Registro() =>
        new(
            _repositorio,
            FixedCallerAccess.Unrestricted,
            _relogio,
            new RegisterMovementOptions { MaxBackdating = TimeSpan.FromDays(3650) },
            _metricas);

    private GetBalanceHandler Consulta() =>
        new(_repositorio, FixedCallerAccess.Unrestricted, _snapshots, _relogio, metrics: _metricas);

    private ConsolidateSnapshotsHandler Consolidacao(ConsolidateSnapshotsOptions? opcoes = null) =>
        new(_repositorio, _snapshots, _relogio, opcoes, _metricas);

    private ReconcileSnapshotsHandler Reconciliacao() =>
        new(_repositorio, _snapshots, metrics: _metricas);

    private RegisterMovementCommand Comando(
        string chave,
        decimal valor = 100m,
        MovementType tipo = MovementType.Credit,
        DateTimeOffset? ocorridaEm = null) =>
        new(
            _conta.Id.Value, tipo, valor, "BRL",
            ocorridaEm ?? Agora.AddMinutes(-5), chave, "corr");

    // ------------------------------------------------------------ movimentação

    [Fact]
    public async Task Registro_conta_a_movimentacao_por_tipo()
    {
        await Registro().HandleAsync(Comando("chave-metrica-0001"), TestContext.Current.CancellationToken);
        await Registro().HandleAsync(
            Comando("chave-metrica-0002", tipo: MovementType.Debit), TestContext.Current.CancellationToken);
        await Registro().HandleAsync(
            Comando("chave-metrica-0003", tipo: MovementType.Debit), TestContext.Current.CancellationToken);

        _coletor.Soma("ledger.movements.posted", "type", "Credit").ShouldBe(1);
        _coletor.Soma("ledger.movements.posted", "type", "Debit").ShouldBe(2);
    }

    [Fact]
    public async Task Repeticao_conta_replay_e_nao_conta_nova_movimentacao()
    {
        // Alta de replays não é erro do razão: indica cliente com timeout mal
        // calibrado. Mas contar o replay como movimentação nova inflaria o volume
        // real que o painel mostra, que é justamente o que esta métrica mede.
        var pedido = Comando("chave-metrica-0010");

        await Registro().HandleAsync(pedido, TestContext.Current.CancellationToken);
        await Registro().HandleAsync(pedido, TestContext.Current.CancellationToken);
        await Registro().HandleAsync(pedido, TestContext.Current.CancellationToken);

        _coletor.Soma("ledger.movements.posted").ShouldBe(1);
        _coletor.Soma("ledger.idempotency.replays").ShouldBe(2);
        _coletor.Soma("ledger.idempotency.conflicts").ShouldBe(0);
    }

    [Fact]
    public async Task Conflito_de_idempotencia_e_contado_a_parte()
    {
        await Registro().HandleAsync(Comando("chave-metrica-0020", 100m), TestContext.Current.CancellationToken);
        await Registro().HandleAsync(Comando("chave-metrica-0020", 999m), TestContext.Current.CancellationToken);

        _coletor.Soma("ledger.idempotency.conflicts").ShouldBe(1);
        _coletor.Soma("ledger.idempotency.replays").ShouldBe(0);
        _coletor.Soma("ledger.movements.posted").ShouldBe(1);
    }

    [Fact]
    public async Task Recusa_por_regra_e_contada_com_o_motivo()
    {
        // Concentração num motivo costuma indicar um cliente com defeito.
        await Registro().HandleAsync(
            Comando("chave-metrica-0030", valor: 0m), TestContext.Current.CancellationToken);
        await Registro().HandleAsync(
            Comando("chave-metrica-0031", valor: -5m), TestContext.Current.CancellationToken);

        _coletor.Soma("ledger.movements.rejected", "reason", "AmountNotPositive").ShouldBe(2);
        _coletor.Soma("ledger.movements.posted").ShouldBe(0, "recusa não é registro");
    }

    // ------------------------------------------------------------------- saldo

    [Fact]
    public async Task Consulta_de_saldo_registra_duracao_e_caminho_de_calculo()
    {
        await Registro().HandleAsync(
            Comando("chave-metrica-0040", ocorridaEm: Agora.AddDays(-3)),
            TestContext.Current.CancellationToken);

        await Consulta().HandleAsync(
            new GetBalanceQuery(_conta.Id.Value), TestContext.Current.CancellationToken);

        var medicao = _coletor.Medicoes.Single(m => m.Instrumento == "ledger.balance.query.duration");

        medicao.Valor.ShouldBeGreaterThanOrEqualTo(0);
        medicao.Valor.ShouldBeLessThan(5, "uma consulta em memória não leva segundos; o valor está em segundos");
        medicao.Etiquetas["source"].ShouldBe("DirectSum");
        medicao.Etiquetas["stale"].ShouldBe(false);
    }

    [Fact]
    public async Task Consulta_com_fechamento_informa_o_caminho_rapido()
    {
        await Registro().HandleAsync(
            Comando("chave-metrica-0050", ocorridaEm: Agora.AddDays(-3)),
            TestContext.Current.CancellationToken);
        await Consolidacao().HandleAsync(TestContext.Current.CancellationToken);

        await Consulta().HandleAsync(
            new GetBalanceQuery(_conta.Id.Value), TestContext.Current.CancellationToken);

        _coletor.Medicoes
            .Single(m => m.Instrumento == "ledger.balance.query.duration")
            .Etiquetas["source"].ShouldBe("SnapshotPlusDelta");
    }

    [Fact]
    public async Task Consulta_que_recuou_de_fechamento_obsoleto_e_sinalizada()
    {
        await Registro().HandleAsync(
            Comando("chave-metrica-0060", ocorridaEm: Agora.AddDays(-5)),
            TestContext.Current.CancellationToken);
        await Consolidacao().HandleAsync(TestContext.Current.CancellationToken);

        // Movimentação retroativa: invalida fechamentos.
        await Registro().HandleAsync(
            Comando("chave-metrica-0061", ocorridaEm: Agora.AddDays(-3)),
            TestContext.Current.CancellationToken);

        await Consulta().HandleAsync(
            new GetBalanceQuery(_conta.Id.Value), TestContext.Current.CancellationToken);

        _coletor.Medicoes
            .Single(m => m.Instrumento == "ledger.balance.query.duration")
            .Etiquetas["stale"].ShouldBe(true);
    }

    [Fact]
    public async Task Consulta_recusada_nao_polui_o_histograma_de_duracao()
    {
        await Consulta().HandleAsync(
            new GetBalanceQuery(Guid.CreateVersion7()), TestContext.Current.CancellationToken);
        await Consulta().HandleAsync(
            new GetBalanceQuery(_conta.Id.Value, Agora.AddYears(1)), TestContext.Current.CancellationToken);

        _coletor.Contagem("ledger.balance.query.duration").ShouldBe(
            0,
            "só consultas respondidas medem a duração do cálculo");
    }

    // --------------------------------------------------------------- snapshot

    [Fact]
    public async Task Consolidacao_conta_fechamentos_e_informa_que_esta_em_dia()
    {
        await Registro().HandleAsync(
            Comando("chave-metrica-0070", ocorridaEm: Agora.AddDays(-3)),
            TestContext.Current.CancellationToken);

        await Consolidacao().HandleAsync(TestContext.Current.CancellationToken);

        _coletor.Soma("ledger.snapshot.consolidated").ShouldBeGreaterThan(0);

        // Publicado mesmo quando é zero: um gauge que só aparece quando há atraso
        // não distingue "em dia" de "o worker parou de reportar".
        var atraso = _coletor.Medicoes.Single(m => m.Instrumento == "ledger.snapshot.lag");
        atraso.Valor.ShouldBe(0);
    }

    [Fact]
    public async Task Limite_de_dias_por_passada_aparece_como_atraso()
    {
        await Registro().HandleAsync(
            Comando("chave-metrica-0080", ocorridaEm: Agora.AddDays(-7)),
            TestContext.Current.CancellationToken);

        await Consolidacao(new ConsolidateSnapshotsOptions { MaxDaysPerAccountPerRun = 2 })
            .HandleAsync(TestContext.Current.CancellationToken);

        var atraso = _coletor.Medicoes.Single(m => m.Instrumento == "ledger.snapshot.lag");

        // Consolidou 2 dos 7 dias encerrados; faltam 5. Em segundos.
        atraso.Valor.ShouldBe(TimeSpan.FromDays(5).TotalSeconds);
    }

    [Fact]
    public async Task Divergencia_e_contada_e_cache_limpo_nao_conta_nada()
    {
        // A métrica mais importante do sistema. Todas as outras dizem se ele está
        // rápido ou se devolve erro; nenhuma pega responder 200 OK com o número
        // errado.
        await Registro().HandleAsync(
            Comando("chave-metrica-0090", ocorridaEm: Agora.AddDays(-4)),
            TestContext.Current.CancellationToken);
        await Consolidacao().HandleAsync(TestContext.Current.CancellationToken);

        await Reconciliacao().HandleAsync(TestContext.Current.CancellationToken);
        _coletor.Soma("ledger.snapshot.divergence").ShouldBe(
            0, "cache correspondente ao razão não pode acusar divergência");

        _snapshots.Corromper(_conta.Id, new DateOnly(2026, 11, 6), Money.Of(999m, Currency.Brl));
        await Reconciliacao().HandleAsync(TestContext.Current.CancellationToken);

        _coletor.Soma("ledger.snapshot.divergence").ShouldBe(1);
    }

    // ------------------------------------------------------------- cardinalidade

    [Fact]
    public async Task Nenhuma_etiqueta_carrega_identificador_nem_valor_de_alta_cardinalidade()
    {
        // Cada combinação de valores de etiqueta vira uma série nova no backend.
        // Uma etiqueta por conta significaria dez milhões de séries — o que
        // derruba o sistema de métricas, não o razão. E identificador em etiqueta
        // de métrica é vazamento tanto quanto em log.
        await Registro().HandleAsync(Comando("chave-metrica-0100"), TestContext.Current.CancellationToken);
        await Registro().HandleAsync(Comando("chave-metrica-0100"), TestContext.Current.CancellationToken);
        await Registro().HandleAsync(Comando("chave-metrica-0100", 5m), TestContext.Current.CancellationToken);
        await Registro().HandleAsync(Comando("chave-metrica-0101", 0m), TestContext.Current.CancellationToken);
        await Consulta().HandleAsync(new GetBalanceQuery(_conta.Id.Value), TestContext.Current.CancellationToken);
        await Consolidacao().HandleAsync(TestContext.Current.CancellationToken);

        var permitidas = new HashSet<string> { "type", "reason", "source", "stale" };

        var todasAsEtiquetas = _coletor.Medicoes.SelectMany(m => m.Etiquetas).ToList();
        todasAsEtiquetas.ShouldNotBeEmpty("o teste precisa ter exercitado etiquetas de verdade");

        foreach (var (chave, valor) in todasAsEtiquetas)
        {
            permitidas.ShouldContain(chave, $"etiqueta inesperada '{chave}'");

            var texto = valor?.ToString() ?? string.Empty;
            Guid.TryParse(texto, out _).ShouldBeFalse($"a etiqueta '{chave}' carrega um identificador: {texto}");
            texto.ShouldNotContain(_conta.Id.Value.ToString("N")[..8]);
            texto.ShouldNotContain("chave-metrica");
        }
    }
}
