namespace Ledger.Domain.Tests;

/// <summary>
/// Invariantes do valor monetário. É o tipo mais usado do domínio e o que, se
/// estiver errado, erra dinheiro em todo lugar.
/// </summary>
public sealed class MoneyTests
{
    private static readonly Currency Brl = Currency.Brl;
    private static readonly Currency Usd = Currency.Of("USD");

    [Fact]
    public void Exige_moeda()
    {
        var erro = Should.Throw<DomainException>(() => Money.Of(10m, default));

        erro.Code.ShouldBe(DomainErrorCode.MissingCurrency);
    }

    [Theory]
    [InlineData("10")]
    [InlineData("10.5")]
    [InlineData("10.50")]
    [InlineData("10.500")]
    [InlineData("10.5000")]
    [InlineData("-0.0001")]
    public void Aceita_ate_quatro_casas_decimais(string valor)
    {
        var quantia = decimal.Parse(valor, System.Globalization.CultureInfo.InvariantCulture);

        var dinheiro = Money.Of(quantia, Brl);

        dinheiro.Amount.ShouldBe(quantia);
    }

    [Theory]
    [InlineData("10.50001")]
    [InlineData("0.000001")]
    public void Rejeita_mais_de_quatro_casas_decimais(string valor)
    {
        var quantia = decimal.Parse(valor, System.Globalization.CultureInfo.InvariantCulture);

        var erro = Should.Throw<DomainException>(() => Money.Of(quantia, Brl));

        erro.Code.ShouldBe(DomainErrorCode.MoneyScaleExceeded);
    }

    [Fact]
    public void Rejeita_em_vez_de_arredondar()
    {
        // Arredondamento silencioso move dinheiro sem que ninguém tenha
        // decidido isso. Falhar alto é a escolha correta aqui.
        var erro = Should.Throw<DomainException>(() => Money.Of(0.00005m, Brl));

        erro.Code.ShouldBe(DomainErrorCode.MoneyScaleExceeded);
        erro.Message.ShouldContain("casas decimais");
    }

    [Fact]
    public void Soma_valores_da_mesma_moeda()
    {
        var resultado = Money.Of(100.25m, Brl) + Money.Of(50.75m, Brl);

        resultado.Amount.ShouldBe(151.00m);
        resultado.Currency.ShouldBe(Brl);
    }

    [Fact]
    public void Subtrai_valores_da_mesma_moeda()
    {
        var resultado = Money.Of(100m, Brl) - Money.Of(30.50m, Brl);

        resultado.Amount.ShouldBe(69.50m);
    }

    [Fact]
    public void Rejeita_operacao_entre_moedas_diferentes()
    {
        var erro = Should.Throw<DomainException>(() => Money.Of(10m, Brl) + Money.Of(10m, Usd));

        erro.Code.ShouldBe(DomainErrorCode.CurrencyMismatch);

        // A mensagem diz por que não há conversão automática: converter é
        // decisão de negócio (qual taxa? de que momento?), não detalhe técnico.
        erro.Message.ShouldContain("decisão de negócio");
    }

    [Fact]
    public void Inverter_sinal_preserva_a_moeda()
    {
        var invertido = Money.Of(42.42m, Brl).Negate();

        invertido.Amount.ShouldBe(-42.42m);
        invertido.Currency.ShouldBe(Brl);
    }

    [Fact]
    public void Zero_e_zero_na_moeda_informada()
    {
        var zero = Money.Zero(Brl);

        zero.IsZero.ShouldBeTrue();
        zero.IsPositive.ShouldBeFalse();
        zero.IsNegative.ShouldBeFalse();
        zero.Currency.ShouldBe(Brl);
    }

    [Fact]
    public void Compara_valores_da_mesma_moeda()
    {
        var menor = Money.Of(10m, Brl);
        var maior = Money.Of(20m, Brl);

        (menor < maior).ShouldBeTrue();
        (maior > menor).ShouldBeTrue();
        (menor <= Money.Of(10m, Brl)).ShouldBeTrue();
    }

    [Fact]
    public void Rejeita_comparacao_entre_moedas_diferentes()
    {
        var erro = Should.Throw<DomainException>(() => Money.Of(10m, Brl) < Money.Of(10m, Usd));

        erro.Code.ShouldBe(DomainErrorCode.CurrencyMismatch);
    }

    [Fact]
    public void Valores_numericamente_iguais_com_escalas_diferentes_sao_iguais()
    {
        // 10.5 e 10.5000 são a mesma quantia. Se a igualdade distinguisse os
        // dois, uma conciliação compararia valores idênticos e acusaria
        // divergência.
        Money.Of(10.5m, Brl).ShouldBe(Money.Of(10.5000m, Brl));
        Money.Of(10.5m, Brl).GetHashCode().ShouldBe(Money.Of(10.5000m, Brl).GetHashCode());
    }

    [Fact]
    public void Somar_um_milhao_de_centavos_nao_acumula_erro()
    {
        // Cenário crítico 9 de docs/testes.md.
        var soma = Money.Zero(Brl);
        var centavo = Money.Of(0.01m, Brl);

        for (var i = 0; i < 1_000_000; i++)
        {
            soma += centavo;
        }

        soma.Amount.ShouldBe(10_000m);
    }

    [Fact]
    public void Ponto_flutuante_binario_acumularia_erro_na_mesma_soma()
    {
        // Este teste não verifica o domínio: documenta, de forma executável,
        // por que o domínio proíbe double e float. Se alguém propuser a troca,
        // este é o número que responde.
        double somaEmDouble = 0;

        for (var i = 0; i < 1_000_000; i++)
        {
            somaEmDouble += 0.01;
        }

        somaEmDouble.ShouldNotBe(10_000d);
        Math.Abs(somaEmDouble - 10_000d).ShouldBeGreaterThan(0d);
    }

    [Fact]
    public void Texto_mostra_moeda_e_quantia()
    {
        // A escala canônica aparece no texto: o tipo tem uma representação só.
        Money.Of(1234.56m, Brl).ToString().ShouldBe("BRL 1234.5600");
    }
}
