using Ledger.Application.Balances;
using Ledger.Application.Movements;
using Ledger.Domain;

namespace Ledger.Application.Tests;

/// <summary>
/// A consulta de posição consolidada.
/// </summary>
/// <remarks>
/// Aqui se verifica a decisão: qual corte usar, o que responder quando a conta
/// não existe, e o tratamento das fronteiras temporais. A aritmética contra SQL
/// real e a equivalência com o cálculo do domínio ficam nos testes de
/// integração.
/// </remarks>
public sealed class GetBalanceHandlerTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 8, 18, 0, 0, TimeSpan.Zero);

    private readonly FakeLedgerRepository _repositorio = new();
    private readonly FixedClock _relogio = new(Agora);
    private readonly Account _conta =
        Account.Open(AccountId.New(), CustomerId.New(), Currency.Brl);

    private GetBalanceHandler Consulta()
    {
        _repositorio.Adicionar(_conta);
        return new GetBalanceHandler(_repositorio, _relogio);
    }

    private async Task Registrar(
        decimal valor,
        MovementType tipo,
        DateTimeOffset ocorridaEm,
        string chave)
    {
        _repositorio.Adicionar(_conta);
        var handler = new RegisterMovementHandler(
            _repositorio,
            new FixedClock(Agora),
            new RegisterMovementOptions { MaxBackdating = TimeSpan.FromDays(3650) });

        var resultado = await handler.HandleAsync(
            new RegisterMovementCommand(
                _conta.Id.Value, tipo, valor, "BRL", ocorridaEm, chave, "corr"),
            TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.Registered);
    }

    [Fact]
    public async Task Conta_sem_movimentacao_tem_saldo_zero_e_nao_erro()
    {
        var resultado = await Consulta().HandleAsync(
            new GetBalanceQuery(_conta.Id.Value), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(GetBalanceStatus.Found);
        resultado.Balance.ShouldBe(0m);
        resultado.MovementCount.ShouldBe(0L);
        resultado.Currency.ShouldBe("BRL");
    }

    [Fact]
    public async Task Soma_creditos_e_subtrai_debitos()
    {
        await Registrar(1_000m, MovementType.Credit, Agora.AddDays(-3), "chave-saldo-0001");
        await Registrar(250.50m, MovementType.Debit, Agora.AddDays(-2), "chave-saldo-0002");
        await Registrar(0.50m, MovementType.Credit, Agora.AddDays(-1), "chave-saldo-0003");

        var resultado = await Consulta().HandleAsync(
            new GetBalanceQuery(_conta.Id.Value), TestContext.Current.CancellationToken);

        resultado.Balance.ShouldBe(750m);
        resultado.MovementCount.ShouldBe(3L);
    }

    [Fact]
    public async Task Saldo_em_tres_instantes_distintos_acompanha_o_historico()
    {
        // Critério de conclusão da etapa: o saldo precisa estar correto em
        // instantes diferentes, e não apenas "agora".
        var primeira = Agora.AddDays(-10);
        var segunda = Agora.AddDays(-5);

        await Registrar(500m, MovementType.Credit, primeira, "chave-tres-0001");
        await Registrar(200m, MovementType.Debit, segunda, "chave-tres-0002");

        var handler = Consulta();

        var antesDeTudo = await handler.HandleAsync(
            new GetBalanceQuery(_conta.Id.Value, primeira.AddSeconds(-1)),
            TestContext.Current.CancellationToken);
        var entreAsDuas = await handler.HandleAsync(
            new GetBalanceQuery(_conta.Id.Value, segunda.AddSeconds(-1)),
            TestContext.Current.CancellationToken);
        var depoisDeTudo = await handler.HandleAsync(
            new GetBalanceQuery(_conta.Id.Value, Agora),
            TestContext.Current.CancellationToken);

        antesDeTudo.Balance.ShouldBe(0m, "antes da primeira movimentação o saldo é zero");
        antesDeTudo.MovementCount.ShouldBe(0L);
        entreAsDuas.Balance.ShouldBe(500m);
        depoisDeTudo.Balance.ShouldBe(300m);
    }

    [Fact]
    public async Task Corte_e_inclusivo_no_instante_exato_da_movimentacao()
    {
        // Ambiguidade que vira discrepância de centavos se não for definida:
        // uma movimentação cujo instante é exatamente o consultado entra.
        var instante = Agora.AddHours(-6);
        await Registrar(123.45m, MovementType.Credit, instante, "chave-limite-0001");

        var handler = Consulta();

        var noInstante = await handler.HandleAsync(
            new GetBalanceQuery(_conta.Id.Value, instante),
            TestContext.Current.CancellationToken);
        var umTickAntes = await handler.HandleAsync(
            new GetBalanceQuery(_conta.Id.Value, instante.AddTicks(-1)),
            TestContext.Current.CancellationToken);

        noInstante.Balance.ShouldBe(123.45m);
        umTickAntes.Balance.ShouldBe(0m);
    }

    [Fact]
    public async Task Movimentacao_retroativa_entra_no_saldo_do_passado()
    {
        // O corte usa o tempo de NEGÓCIO, não o de registro: "qual era meu saldo
        // na sexta" pergunta sobre fatos, não sobre o que o sistema já sabia.
        // A movimentação abaixo é registrada agora, com data de dez dias atrás.
        await Registrar(400m, MovementType.Credit, Agora.AddDays(-10), "chave-retro-0001");

        var resultado = await Consulta().HandleAsync(
            new GetBalanceQuery(_conta.Id.Value, Agora.AddDays(-9)),
            TestContext.Current.CancellationToken);

        resultado.Balance.ShouldBe(400m);
    }

    [Fact]
    public async Task Ausencia_de_instante_significa_agora()
    {
        var resultado = await Consulta().HandleAsync(
            new GetBalanceQuery(_conta.Id.Value), TestContext.Current.CancellationToken);

        resultado.AsOf.ShouldBe(Agora);
    }

    [Fact]
    public async Task Instante_pouco_a_frente_do_relogio_e_aceito()
    {
        var resultado = await Consulta().HandleAsync(
            new GetBalanceQuery(_conta.Id.Value, Agora.AddSeconds(20)),
            TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(GetBalanceStatus.Found);
    }

    [Fact]
    public async Task Instante_futuro_e_recusado()
    {
        // Saldo "em" uma data futura não é conhecível: movimentações vão chegar
        // depois. Devolver um número apresentaria resposta incompleta como
        // final, e quem consome não teria como saber.
        var resultado = await Consulta().HandleAsync(
            new GetBalanceQuery(_conta.Id.Value, Agora.AddDays(30)),
            TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(GetBalanceStatus.Rejected);
        resultado.Balance.ShouldBeNull();
        resultado.Detail.ShouldNotBeNull().ShouldContain("futuro");
    }

    [Fact]
    public async Task Conta_inexistente_nao_devolve_saldo_zero()
    {
        // Zero e "não existe" são respostas diferentes. Devolver zero para conta
        // inexistente esconderia erro de roteamento de quem chama.
        var resultado = await Consulta().HandleAsync(
            new GetBalanceQuery(Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(GetBalanceStatus.AccountNotFound);
        resultado.Balance.ShouldBeNull();
    }

    [Fact]
    public async Task Identificador_vazio_e_recusado()
    {
        var resultado = await Consulta().HandleAsync(
            new GetBalanceQuery(Guid.Empty), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(GetBalanceStatus.Rejected);
        resultado.ErrorCode.ShouldBe(DomainErrorCode.MissingIdentifier);
    }

    [Fact]
    public async Task Resposta_diz_de_onde_o_numero_veio()
    {
        // Quando alguém contestar um saldo, a primeira pergunta é de onde ele
        // veio. Rastreabilidade do número vale mais que elegância do payload.
        var resultado = await Consulta().HandleAsync(
            new GetBalanceQuery(_conta.Id.Value), TestContext.Current.CancellationToken);

        resultado.Source.ShouldBe(BalanceSource.DirectSum);
        resultado.Stale.ShouldBe(false);
    }

    [Fact]
    public async Task Saldo_pode_ser_negativo()
    {
        // Consequência de PR-03: o razão registra, não autoriza.
        await Registrar(100m, MovementType.Credit, Agora.AddDays(-2), "chave-neg-0001");
        await Registrar(450m, MovementType.Debit, Agora.AddDays(-1), "chave-neg-0002");

        var resultado = await Consulta().HandleAsync(
            new GetBalanceQuery(_conta.Id.Value), TestContext.Current.CancellationToken);

        resultado.Balance.ShouldBe(-350m);
    }
}
