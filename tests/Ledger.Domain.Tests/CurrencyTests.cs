namespace Ledger.Domain.Tests;

/// <summary>Invariantes do código de moeda.</summary>
public sealed class CurrencyTests
{
    [Theory]
    [InlineData("BRL", "BRL")]
    [InlineData("brl", "BRL")]
    [InlineData(" usd ", "USD")]
    public void Normaliza_para_maiusculas_sem_espaco(string entrada, string esperado)
    {
        Currency.Of(entrada).Code.ShouldBe(esperado);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("BR")]
    [InlineData("BRLL")]
    [InlineData("BR1")]
    [InlineData("98 ")]
    public void Rejeita_codigo_fora_do_formato_iso(string entrada)
    {
        var erro = Should.Throw<DomainException>(() => Currency.Of(entrada));

        erro.Code.ShouldBe(DomainErrorCode.InvalidCurrencyCode);
    }

    [Fact]
    public void Valor_padrao_da_struct_e_reconhecido_como_ausente()
    {
        // Toda struct em C# admite default e não há construtor que impeça isso.
        // Em lugar de fingir que o caso não existe, ele é nomeado e verificado
        // por quem recebe uma Currency de fora.
        Currency moedaPadrao = default;

        moedaPadrao.IsUnset.ShouldBeTrue();
        Should.Throw<DomainException>(() => moedaPadrao.Code)
            .Code.ShouldBe(DomainErrorCode.MissingCurrency);
    }

    [Fact]
    public void Mesmo_codigo_e_a_mesma_moeda()
    {
        Currency.Of("BRL").ShouldBe(Currency.Brl);
        (Currency.Of("BRL") == Currency.Brl).ShouldBeTrue();
        (Currency.Of("USD") != Currency.Brl).ShouldBeTrue();
    }
}
