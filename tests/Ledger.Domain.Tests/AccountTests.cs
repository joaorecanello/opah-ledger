namespace Ledger.Domain.Tests;

/// <summary>
/// Invariantes do registro de movimentação, que entra no razão pela conta.
/// </summary>
public sealed class AccountTests
{
    private static readonly Currency Brl = Currency.Brl;
    private static readonly Currency Usd = Currency.Of("USD");
    private static readonly DateTimeOffset Ocorrencia = new(2026, 11, 5, 13, 22, 10, TimeSpan.Zero);
    private static readonly DateTimeOffset Registro = new(2026, 11, 5, 13, 22, 11, TimeSpan.Zero);

    private static Account ContaAtiva(Currency? moeda = null) =>
        Account.Open(AccountId.New(), CustomerId.New(), moeda ?? Brl);

    private static Account ContaCom(AccountStatus situacao) =>
        Account.Rehydrate(AccountId.New(), CustomerId.New(), Brl, situacao);

    private static Movement Registrar(
        Account conta,
        MovementType tipo,
        decimal valor,
        Currency? moeda = null) =>
        conta.Register(
            tipo,
            Money.Of(valor, moeda ?? conta.Currency),
            Ocorrencia,
            Registro,
            IdempotencyKey.Of("chave-de-teste-0001"),
            "corr-0001");

    [Fact]
    public void Conta_aberta_nasce_ativa()
    {
        ContaAtiva().Status.ShouldBe(AccountStatus.Active);
    }

    [Fact]
    public void Conta_nao_expoe_saldo()
    {
        // A ausência de saldo na conta é a decisão central da solução, e não um
        // esquecimento. Este teste existe para que a reintrodução de uma
        // propriedade de saldo seja uma falha vermelha, com o motivo escrito
        // aqui, em lugar de uma conveniência que passa na revisão.
        var propriedades = typeof(Account)
            .GetProperties()
            .Select(p => p.Name)
            .ToArray();

        propriedades.ShouldNotContain("Balance");
        propriedades.ShouldNotContain("Saldo");
        propriedades.ShouldBe(["Id", "CustomerId", "Currency", "Status"], ignoreOrder: true);
    }

    [Fact]
    public void Credito_tem_valor_assinado_positivo()
    {
        var movimentacao = Registrar(ContaAtiva(), MovementType.Credit, 150m);

        movimentacao.Amount.Amount.ShouldBe(150m);
        movimentacao.SignedAmount.Amount.ShouldBe(150m);
    }

    [Fact]
    public void Debito_guarda_valor_positivo_e_assina_negativo()
    {
        // O sinal é semântica do tipo, não do número. Guardar o valor sempre
        // positivo impede a classe de bug em que um crédito com valor negativo
        // se comporta como débito sem que nada no código pareça errado.
        var movimentacao = Registrar(ContaAtiva(), MovementType.Debit, 150m);

        movimentacao.Amount.Amount.ShouldBe(150m);
        movimentacao.SignedAmount.Amount.ShouldBe(-150m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-0.01)]
    public void Rejeita_valor_nao_positivo(decimal valor)
    {
        var erro = Should.Throw<DomainException>(
            () => Registrar(ContaAtiva(), MovementType.Credit, valor));

        erro.Code.ShouldBe(DomainErrorCode.AmountNotPositive);
    }

    [Fact]
    public void Rejeita_moeda_diferente_da_conta()
    {
        var erro = Should.Throw<DomainException>(
            () => Registrar(ContaAtiva(Brl), MovementType.Credit, 10m, Usd));

        erro.Code.ShouldBe(DomainErrorCode.CurrencyMismatch);
    }

    [Fact]
    public void Conta_encerrada_recusa_movimentacao()
    {
        // Encerramento é definitivo: a conta não existe mais para fins
        // operacionais, e um lançamento nela indica erro de roteamento na
        // origem.
        var erro = Should.Throw<DomainException>(
            () => Registrar(ContaCom(AccountStatus.Closed), MovementType.Credit, 10m));

        erro.Code.ShouldBe(DomainErrorCode.AccountClosed);
    }

    [Fact]
    public void Conta_bloqueada_aceita_movimentacao()
    {
        // PR-13: bloqueio impede ORIGINAR operação, não REGISTRAR fato que já
        // aconteceu. Um estorno de operação anterior, ou um lançamento em
        // trânsito quando o bloqueio entrou em vigor, precisa entrar no razão —
        // recusá-lo faria a contabilidade divergir da realidade.
        var movimentacao = Registrar(ContaCom(AccountStatus.Blocked), MovementType.Credit, 10m);

        movimentacao.Amount.Amount.ShouldBe(10m);
    }

    [Fact]
    public void Nao_valida_saldo_suficiente()
    {
        // PR-03: este sistema registra o fato consumado. A decisão de autorizar
        // o débito pertence a quem origina a movimentação.
        //
        // É a premissa de maior impacto no desenho, e está verificada aqui de
        // forma executável: se alguém adicionar validação de saldo, este teste
        // falha e a conversa acontece antes do merge, não depois.
        var conta = ContaAtiva();

        var debitoSemLastro = Registrar(conta, MovementType.Debit, 1_000_000m);

        debitoSemLastro.SignedAmount.Amount.ShouldBe(-1_000_000m);
    }

    [Fact]
    public void Identificador_da_movimentacao_e_ordenavel_pelo_instante_de_registro()
    {
        var conta = ContaAtiva();
        var chave = IdempotencyKey.Of("chave-de-teste-0002");

        var primeira = conta.Register(
            MovementType.Credit, Money.Of(1m, Brl), Ocorrencia,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), chave, "corr");
        var segunda = conta.Register(
            MovementType.Credit, Money.Of(1m, Brl), Ocorrencia,
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), chave, "corr");

        primeira.Id.Value.CompareTo(segunda.Id.Value).ShouldBeLessThan(0);
    }

    [Fact]
    public void Exige_identificador_de_correlacao()
    {
        // Persistido junto do fato: é o que permite, meses depois, recuperar a
        // requisição que originou um lançamento contestado.
        var erro = Should.Throw<DomainException>(() => ContaAtiva().Register(
            MovementType.Credit,
            Money.Of(10m, Brl),
            Ocorrencia,
            Registro,
            IdempotencyKey.Of("chave-de-teste-0003"),
            correlationId: "  "));

        erro.Code.ShouldBe(DomainErrorCode.InvalidText);
    }

    [Fact]
    public void Exige_chave_de_idempotencia()
    {
        var erro = Should.Throw<DomainException>(() => ContaAtiva().Register(
            MovementType.Credit,
            Money.Of(10m, Brl),
            Ocorrencia,
            Registro,
            default,
            "corr-0001"));

        erro.Code.ShouldBe(DomainErrorCode.InvalidIdempotencyKey);
    }

    [Fact]
    public void Rejeita_tipo_de_movimentacao_desconhecido()
    {
        var erro = Should.Throw<DomainException>(
            () => Registrar(ContaAtiva(), (MovementType)99, 10m));

        erro.Code.ShouldBe(DomainErrorCode.InvalidMovementType);
    }

    [Fact]
    public void Preserva_os_dois_instantes_separadamente()
    {
        // Bitemporalidade: "qual era meu saldo na sexta" usa occurredAt;
        // "o que o sistema sabia na sexta" usa postedAt. Guardar só um fecha
        // metade das perguntas para sempre.
        var retroativa = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

        var movimentacao = ContaAtiva().Register(
            MovementType.Credit,
            Money.Of(10m, Brl),
            retroativa,
            Registro,
            IdempotencyKey.Of("chave-de-teste-0004"),
            "corr-0001");

        movimentacao.OccurredAt.ShouldBe(retroativa);
        movimentacao.PostedAt.ShouldBe(Registro);
        movimentacao.OccurredAt.ShouldBeLessThan(movimentacao.PostedAt);
    }

    [Fact]
    public void Rejeita_situacao_de_conta_desconhecida()
    {
        Should.Throw<DomainException>(
            () => Account.Rehydrate(AccountId.New(), CustomerId.New(), Brl, AccountStatus.Unspecified));
    }
}
