using Ledger.Application.Movements;
using Ledger.Domain;

namespace Ledger.Application.Tests;

/// <summary>
/// A decisão do registro idempotente: registrou, replay, conflito ou recusa.
/// </summary>
/// <remarks>
/// Aqui se verifica a ramificação. O mecanismo de unicidade sob concorrência é
/// verificado contra PostgreSQL real nos testes de integração — um falso em
/// memória não o reproduz, e fingir que reproduz daria falso-positivo no cenário
/// de maior risco do sistema.
/// </remarks>
public sealed class RegisterMovementHandlerTests
{
    private static readonly DateTimeOffset Agora = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeSnapshotStore _snapshots = new();
    private readonly FakeLedgerRepository _repositorio;
    private readonly FixedClock _relogio = new(Agora);
    private readonly Account _conta =
        Account.Open(AccountId.New(), CustomerId.New(), Currency.Brl);

    public RegisterMovementHandlerTests() => _repositorio = new FakeLedgerRepository(_snapshots);

    private RegisterMovementHandler Criar(RegisterMovementOptions? opcoes = null)
    {
        _repositorio.Adicionar(_conta);
        return new RegisterMovementHandler(_repositorio, _relogio, opcoes);
    }

    private RegisterMovementCommand Comando(
        decimal valor = 100m,
        string chave = "chave-de-teste-0001",
        MovementType tipo = MovementType.Credit,
        DateTimeOffset? ocorridaEm = null,
        string moeda = "BRL",
        Guid? conta = null,
        string? referencia = null) =>
        new(
            conta ?? _conta.Id.Value,
            tipo,
            valor,
            moeda,
            ocorridaEm ?? Agora.AddMinutes(-5),
            chave,
            CorrelationId: "corr-0001",
            ExternalReference: referencia);

    [Fact]
    public async Task Primeira_tentativa_registra()
    {
        var resultado = await Criar().HandleAsync(Comando(), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.Registered);
        resultado.MovementId.ShouldNotBeNull();
        resultado.PostedAt.ShouldBe(Agora);
        _repositorio.Movimentacoes.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Instante_de_registro_vem_do_servidor_e_nao_do_pedido()
    {
        // Aceitar o relógio de quem chama permitiria antedatar ou pós-datar um
        // lançamento. O pedido nem tem campo para isso.
        var handler = Criar();
        var ocorrencia = Agora.AddHours(-3);

        var resultado = await handler.HandleAsync(
            Comando(ocorridaEm: ocorrencia), TestContext.Current.CancellationToken);

        resultado.PostedAt.ShouldBe(Agora);
        _repositorio.Movimentacoes.Single().OccurredAt.ShouldBe(ocorrencia);
    }

    [Fact]
    public async Task Repeticao_identica_devolve_a_resposta_original()
    {
        var handler = Criar();
        var pedido = Comando();

        var primeira = await handler.HandleAsync(pedido, TestContext.Current.CancellationToken);

        // O relógio avança: a segunda tentativa é uma requisição nova no tempo.
        _relogio.UtcNow = Agora.AddSeconds(30);
        var segunda = await handler.HandleAsync(pedido, TestContext.Current.CancellationToken);

        segunda.Status.ShouldBe(RegisterMovementStatus.Replayed);

        // Replay, não reprocessamento: identificador e instante são os da
        // primeira vez. Devolver um identificador novo para o mesmo fato criaria
        // a divergência que a idempotência existe para evitar, porque o chamador
        // pode estar usando esse identificador para conciliar.
        segunda.MovementId.ShouldBe(primeira.MovementId);
        segunda.PostedAt.ShouldBe(primeira.PostedAt);
        _repositorio.Movimentacoes.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Mesma_chave_com_valor_diferente_e_conflito()
    {
        var handler = Criar();
        await handler.HandleAsync(Comando(valor: 100m), TestContext.Current.CancellationToken);

        var resultado = await handler.HandleAsync(
            Comando(valor: 150m), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.ConflictingPayload);
        resultado.MovementId.ShouldBeNull();
        _repositorio.Movimentacoes.Count.ShouldBe(1, "nada deve ter sido gravado");
    }

    [Theory]
    [InlineData(MovementType.Debit, null)]
    [InlineData(MovementType.Credit, "PIX-OUTRO")]
    public async Task Mesma_chave_com_qualquer_campo_do_fato_diferente_e_conflito(
        MovementType tipo,
        string? referencia)
    {
        var handler = Criar();
        await handler.HandleAsync(Comando(), TestContext.Current.CancellationToken);

        var resultado = await handler.HandleAsync(
            Comando(tipo: tipo, referencia: referencia), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.ConflictingPayload);
    }

    [Fact]
    public async Task Repeticao_com_correlacao_diferente_ainda_e_replay()
    {
        // A impressão do conteúdo cobre os campos que definem o FATO, não os que
        // mudam a cada tentativa. Incluir a correlação faria todo retry parecer
        // conteúdo divergente, e o sistema recusaria com 409 exatamente o retry
        // que deveria aceitar.
        var handler = Criar();
        var pedido = Comando();
        await handler.HandleAsync(pedido, TestContext.Current.CancellationToken);

        var resultado = await handler.HandleAsync(
            pedido with { CorrelationId = "corr-de-outra-tentativa" },
            TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.Replayed);
    }

    [Fact]
    public async Task Chave_usada_cujo_registro_desapareceu_e_conflito_e_nao_novo_registro()
    {
        // Caminho improvável: a limpeza das chaves expiradas passa entre o
        // INSERT que falhou e a leitura do registro original.
        //
        // A resposta precisa ser recusa. A movimentação original pode estar no
        // razão, e registrar de novo duplicaria dinheiro — recusar é a única
        // opção segura.
        var handler = Criar();
        await handler.HandleAsync(Comando(), TestContext.Current.CancellationToken);
        _repositorio.EsquecerRegistroAposConflito = true;

        var resultado = await handler.HandleAsync(Comando(), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.ConflictingPayload);
        resultado.Detail.ShouldNotBeNull().ShouldContain("Verifique o extrato");
        _repositorio.Movimentacoes.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Conta_inexistente_nao_e_recusa_de_regra()
    {
        var resultado = await Criar().HandleAsync(
            Comando(conta: Guid.CreateVersion7()), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.AccountNotFound);
    }

    [Fact]
    public async Task Conta_encerrada_e_recusada_com_o_codigo_do_dominio()
    {
        var encerrada = Account.Rehydrate(
            AccountId.New(), CustomerId.New(), Currency.Brl, AccountStatus.Closed);
        _repositorio.Adicionar(encerrada);
        var handler = new RegisterMovementHandler(_repositorio, _relogio);

        var resultado = await handler.HandleAsync(
            Comando(conta: encerrada.Id.Value), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.Rejected);
        resultado.ErrorCode.ShouldBe(DomainErrorCode.AccountClosed);
    }

    [Theory]
    [InlineData(0, DomainErrorCode.AmountNotPositive)]
    [InlineData(-5, DomainErrorCode.AmountNotPositive)]
    public async Task Valor_nao_positivo_e_recusado(decimal valor, DomainErrorCode esperado)
    {
        var resultado = await Criar().HandleAsync(
            Comando(valor: valor), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.Rejected);
        resultado.ErrorCode.ShouldBe(esperado);
    }

    [Fact]
    public async Task Moeda_diferente_da_conta_e_recusada()
    {
        var resultado = await Criar().HandleAsync(
            Comando(moeda: "USD"), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.Rejected);
        resultado.ErrorCode.ShouldBe(DomainErrorCode.CurrencyMismatch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("curta")]
    [InlineData("com espaco no meio xx")]
    public async Task Chave_de_idempotencia_invalida_e_recusada(string chave)
    {
        var resultado = await Criar().HandleAsync(
            Comando(chave: chave), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.Rejected);
        resultado.ErrorCode.ShouldBe(DomainErrorCode.InvalidIdempotencyKey);
        _repositorio.Movimentacoes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Nao_valida_saldo_suficiente()
    {
        // PR-03: este sistema registra o fato consumado. A decisão de autorizar
        // pertence a quem origina a movimentação.
        var resultado = await Criar().HandleAsync(
            Comando(valor: 1_000_000m, tipo: MovementType.Debit),
            TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.Registered);
    }

    [Fact]
    public async Task Instante_pouco_a_frente_do_relogio_e_aceito()
    {
        // Relógios de servidores divergem por alguns segundos mesmo com NTP.
        // Recusar por isso rejeitaria movimentação legítima enviada "agora" por
        // um sistema cujo relógio está um pouco à frente.
        var resultado = await Criar().HandleAsync(
            Comando(ocorridaEm: Agora.AddSeconds(20)), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.Registered);
    }

    [Fact]
    public async Task Instante_muito_a_frente_e_recusado()
    {
        var resultado = await Criar().HandleAsync(
            Comando(ocorridaEm: Agora.AddYears(70)), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.Rejected);
        resultado.Detail.ShouldNotBeNull().ShouldContain("futuro");
    }

    [Fact]
    public async Task Movimentacao_retroativa_dentro_do_limite_e_aceita()
    {
        // Retroatividade existe e é legítima; ver PR-04.
        var resultado = await Criar().HandleAsync(
            Comando(ocorridaEm: Agora.AddDays(-10)), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.Registered);
    }

    [Fact]
    public async Task Retroatividade_alem_do_limite_e_recusada()
    {
        // O limite barra erro de digitação ou de conversão de data — ano 1900 —
        // que entraria no razão e invalidaria snapshots de anos inteiros.
        var resultado = await Criar().HandleAsync(
            Comando(ocorridaEm: Agora.AddDays(-400)), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.Rejected);
        resultado.Detail.ShouldNotBeNull().ShouldContain("retroatividade");
    }

    [Fact]
    public async Task Limites_temporais_sao_configuraveis()
    {
        // São política, não invariante de domínio: quem opera o sistema ajusta.
        var frouxo = new RegisterMovementOptions { MaxBackdating = TimeSpan.FromDays(3650) };

        var resultado = await Criar(frouxo).HandleAsync(
            Comando(ocorridaEm: Agora.AddDays(-400)), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.Registered);
    }
}
