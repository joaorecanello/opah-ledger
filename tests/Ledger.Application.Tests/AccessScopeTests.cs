using System.Text.RegularExpressions;
using Ledger.Application.Accounts;
using Ledger.Application.Balances;
using Ledger.Application.Movements;
using Ledger.Application.Security;
using Ledger.Domain;

namespace Ledger.Application.Tests;

/// <summary>
/// Titularidade da conta, decidida no caso de uso.
/// </summary>
/// <remarks>
/// Verificada aqui, e não só pela API, de propósito: a regra precisa valer para
/// qualquer porta de entrada. Um teste que passasse apenas pelo HTTP provaria que
/// o endpoint está protegido, não que o caso de uso está.
/// </remarks>
public sealed class AccessScopeTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 8, 18, 0, 0, TimeSpan.Zero);

    private readonly FakeSnapshotStore _snapshots = new();
    private readonly FakeLedgerRepository _repositorio;
    private readonly FixedClock _relogio = new(Agora);
    private readonly Account _contaDaMaria = Account.Open(AccountId.New(), CustomerId.New(), Currency.Brl);

    public AccessScopeTests()
    {
        _repositorio = new FakeLedgerRepository(_snapshots);
        _repositorio.Adicionar(_contaDaMaria);
    }

    private static FixedCallerAccess Titular(Guid cliente) =>
        new(AccessScope.ForCustomer(cliente));

    private static RegisterMovementCommand Registro(Guid conta, string chave = "chave-de-teste-0001") =>
        new(
            conta,
            MovementType.Credit,
            100m,
            "BRL",
            Agora.AddMinutes(-5),
            chave,
            "corr-1");

    [Fact]
    public async Task Titular_registra_na_propria_conta()
    {
        var handler = new RegisterMovementHandler(_repositorio, Titular(_contaDaMaria.CustomerId.Value), _relogio);

        var resultado = await handler.HandleAsync(
            Registro(_contaDaMaria.Id.Value), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.Registered);
    }

    [Fact]
    public async Task Outro_cliente_nao_registra_e_nada_e_gravado()
    {
        var handler = new RegisterMovementHandler(_repositorio, Titular(Guid.CreateVersion7()), _relogio);

        var resultado = await handler.HandleAsync(
            Registro(_contaDaMaria.Id.Value), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.AccountNotFound);
        _repositorio.Movimentacoes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Conta_de_outro_responde_exatamente_como_conta_inexistente()
    {
        // Se as duas respostas fossem distinguíveis (403 x 404, ou mensagens
        // diferentes), a API viraria um oráculo: qualquer chamador com permissão de
        // leitura poderia varrer identificadores e descobrir quais contas existem.
        var handler = new RegisterMovementHandler(_repositorio, Titular(Guid.CreateVersion7()), _relogio);

        var alheia = await handler.HandleAsync(
            Registro(_contaDaMaria.Id.Value), TestContext.Current.CancellationToken);
        var inexistente = await handler.HandleAsync(
            Registro(Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        alheia.Status.ShouldBe(inexistente.Status);
        Normalizar(alheia.Detail, _contaDaMaria.Id.Value).ShouldBe(Normalizar(inexistente.Detail, null));
    }

    [Fact]
    public async Task Acesso_irrestrito_alcanca_qualquer_conta()
    {
        var handler = new RegisterMovementHandler(_repositorio, FixedCallerAccess.Unrestricted, _relogio);

        var resultado = await handler.HandleAsync(
            Registro(_contaDaMaria.Id.Value), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.Registered);
    }

    [Fact]
    public async Task Saldo_de_conta_alheia_e_recusado_como_inexistente()
    {
        var handler = new GetBalanceHandler(
            _repositorio, Titular(Guid.CreateVersion7()), _snapshots, _relogio);

        var resultado = await handler.HandleAsync(
            new GetBalanceQuery(_contaDaMaria.Id.Value), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(GetBalanceStatus.AccountNotFound);
        resultado.Balance.ShouldBeNull();
    }

    [Fact]
    public async Task Saldo_da_propria_conta_e_devolvido()
    {
        var handler = new GetBalanceHandler(
            _repositorio, Titular(_contaDaMaria.CustomerId.Value), _snapshots, _relogio);

        var resultado = await handler.HandleAsync(
            new GetBalanceQuery(_contaDaMaria.Id.Value), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(GetBalanceStatus.Found);
    }

    [Fact]
    public async Task Movimentacao_de_conta_alheia_nao_e_devolvida_mesmo_com_o_identificador_certo()
    {
        // O identificador da movimentação é UUID v7: não é adivinhável, mas vaza
        // (log, e-mail de suporte, tela compartilhada). Quem o possui não ganha
        // acesso por isso.
        var dono = new RegisterMovementHandler(_repositorio, FixedCallerAccess.Unrestricted, _relogio);
        var criada = await dono.HandleAsync(
            Registro(_contaDaMaria.Id.Value), TestContext.Current.CancellationToken);
        var id = criada.Movement!.Id.Value;

        var intruso = new GetMovementHandler(_repositorio, Titular(Guid.CreateVersion7()));
        var legitimo = new GetMovementHandler(_repositorio, Titular(_contaDaMaria.CustomerId.Value));

        (await intruso.HandleAsync(
            new GetMovementQuery(_contaDaMaria.Id.Value, id), TestContext.Current.CancellationToken))
            .ShouldBeNull();
        (await legitimo.HandleAsync(
            new GetMovementQuery(_contaDaMaria.Id.Value, id), TestContext.Current.CancellationToken))
            .ShouldNotBeNull();
    }

    [Fact]
    public async Task Cliente_restrito_nao_abre_conta_para_outro_cliente()
    {
        var handler = new OpenAccountHandler(_repositorio, Titular(Guid.CreateVersion7()));

        var resultado = await handler.HandleAsync(
            new OpenAccountCommand(Guid.CreateVersion7(), "BRL"), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(OpenAccountStatus.Forbidden);
        resultado.AccountId.ShouldBeNull();
    }

    [Fact]
    public async Task Cliente_restrito_abre_conta_para_si()
    {
        var cliente = Guid.CreateVersion7();
        var handler = new OpenAccountHandler(_repositorio, Titular(cliente));

        var resultado = await handler.HandleAsync(
            new OpenAccountCommand(cliente, "BRL"), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(OpenAccountStatus.Opened);
    }

    [Fact]
    public void Escopo_de_cliente_vazio_e_recusado_na_construcao()
    {
        // Guid.Empty como "cliente" casaria com contas cujo titular veio zerado por
        // defeito de cadastro. Recusar na origem evita um escopo que parece
        // restrito e na prática alcança dados corrompidos.
        Should.Throw<ArgumentException>(() => AccessScope.ForCustomer(Guid.Empty));
    }

    private static string? Normalizar(string? detalhe, Guid? id) =>
        id is null ? Regex.Replace(detalhe ?? string.Empty, "[0-9a-f-]{36}", "{id}")
                   : (detalhe ?? string.Empty).Replace(id.Value.ToString(), "{id}", StringComparison.OrdinalIgnoreCase);
}
