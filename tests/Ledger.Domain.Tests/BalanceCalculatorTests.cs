namespace Ledger.Domain.Tests;

/// <summary>
/// O cálculo de saldo, que o ledger append-only torna necessário.
/// </summary>
public sealed class BalanceCalculatorTests
{
    private static readonly Currency Brl = Currency.Brl;
    private static readonly DateTimeOffset Instante = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static Movement Mov(Account conta, MovementType tipo, decimal valor, int sequencia) =>
        conta.Register(
            tipo,
            Money.Of(valor, conta.Currency),
            Instante,
            Instante.AddSeconds(sequencia),
            IdempotencyKey.Of($"chave-calculo-{sequencia:D4}"),
            "corr-calculo");

    [Fact]
    public void Conta_sem_movimentacao_tem_saldo_zero_e_nao_erro()
    {
        // Uma consulta anterior à primeira movimentação é pergunta legítima,
        // não caso de borda inválido. Cenário crítico 4 de docs/testes.md.
        var saldo = BalanceCalculator.From(Brl, []);

        saldo.IsZero.ShouldBeTrue();
        saldo.Currency.ShouldBe(Brl);
    }

    [Fact]
    public void Soma_creditos_e_subtrai_debitos()
    {
        var conta = Account.Open(AccountId.New(), CustomerId.New(), Brl);
        Movement[] movimentacoes =
        [
            Mov(conta, MovementType.Credit, 1_000m, 1),
            Mov(conta, MovementType.Debit, 250.50m, 2),
            Mov(conta, MovementType.Credit, 0.50m, 3),
        ];

        BalanceCalculator.From(Brl, movimentacoes).Amount.ShouldBe(750m);
    }

    [Fact]
    public void Ordem_das_movimentacoes_nao_altera_o_saldo()
    {
        // A soma é comutativa, e isso não é um detalhe: significa que o saldo
        // não depende de o ledger ter sequência contígua por conta — que é o
        // que permite escrever sem contenção. Ver ADR-0007.
        var conta = Account.Open(AccountId.New(), CustomerId.New(), Brl);
        Movement[] ordemOriginal =
        [
            Mov(conta, MovementType.Credit, 300m, 1),
            Mov(conta, MovementType.Debit, 120.25m, 2),
            Mov(conta, MovementType.Credit, 45.75m, 3),
            Mov(conta, MovementType.Debit, 10m, 4),
        ];
        var ordemInvertida = ordemOriginal.Reverse().ToArray();

        BalanceCalculator.From(Brl, ordemOriginal)
            .ShouldBe(BalanceCalculator.From(Brl, ordemInvertida));
    }

    [Fact]
    public void Aplica_o_saldo_de_abertura()
    {
        // É este caminho que a consulta usa em produção: parte do fechamento do
        // snapshot anterior e soma apenas o delta do período corrente.
        var conta = Account.Open(AccountId.New(), CustomerId.New(), Brl);
        var abertura = Money.Of(12_500m, Brl);
        Movement[] delta = [Mov(conta, MovementType.Credit, 250.40m, 1)];

        BalanceCalculator.From(abertura, delta).Amount.ShouldBe(12_750.40m);
    }

    [Fact]
    public void Calculo_incremental_equivale_ao_calculo_desde_o_inicio()
    {
        // Versão em miniatura do cenário crítico 6: a otimização do snapshot não
        // pode mudar o número. Com o banco e o worker, o mesmo teste roda com
        // 10.000 movimentações em datas aleatórias.
        var conta = Account.Open(AccountId.New(), CustomerId.New(), Brl);
        var todas = Enumerable.Range(1, 200)
            .Select(i => Mov(conta, i % 3 == 0 ? MovementType.Debit : MovementType.Credit, 10m + i, i))
            .ToArray();

        var desdeOInicio = BalanceCalculator.From(Brl, todas);

        var fechamentoParcial = BalanceCalculator.From(Brl, todas.Take(120));
        var incremental = BalanceCalculator.From(fechamentoParcial, todas.Skip(120));

        incremental.ShouldBe(desdeOInicio);
    }

    [Fact]
    public void Rejeita_movimentacao_em_moeda_diferente_do_saldo()
    {
        var contaEmDolar = Account.Open(AccountId.New(), CustomerId.New(), Currency.Of("USD"));
        Movement[] emDolar = [Mov(contaEmDolar, MovementType.Credit, 10m, 1)];

        var erro = Should.Throw<DomainException>(() => BalanceCalculator.From(Brl, emDolar));

        erro.Code.ShouldBe(DomainErrorCode.CurrencyMismatch);
    }

    [Fact]
    public void Saldo_pode_ser_negativo()
    {
        // Consequência de PR-03: o razão registra, não autoriza. Saldo negativo
        // é resultado possível e não é erro deste sistema.
        var conta = Account.Open(AccountId.New(), CustomerId.New(), Brl);
        Movement[] movimentacoes =
        [
            Mov(conta, MovementType.Credit, 100m, 1),
            Mov(conta, MovementType.Debit, 450m, 2),
        ];

        var saldo = BalanceCalculator.From(Brl, movimentacoes);

        saldo.Amount.ShouldBe(-350m);
        saldo.IsNegative.ShouldBeTrue();
    }
}
