namespace Ledger.Domain.Tests;

/// <summary>
/// Invariantes da chave de idempotência.
/// </summary>
/// <remarks>
/// A garantia de não-duplicidade propriamente dita vive no índice único do
/// banco, e é verificada nos testes de integração. Aqui se verifica apenas que
/// a chave tem forma utilizável antes de chegar lá.
/// </remarks>
public sealed class IdempotencyKeyTests
{
    [Fact]
    public void Aceita_chave_valida()
    {
        IdempotencyKey.Of("pix-e3012-2026-11-07-0001").Value
            .ShouldBe("pix-e3012-2026-11-07-0001");
    }

    [Fact]
    public void Remove_espaco_nas_bordas()
    {
        IdempotencyKey.Of("  chave-valida-1  ").Value.ShouldBe("chave-valida-1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejeita_chave_ausente(string entrada)
    {
        var erro = Should.Throw<DomainException>(() => IdempotencyKey.Of(entrada));

        erro.Code.ShouldBe(DomainErrorCode.InvalidIdempotencyKey);
        erro.Message.ShouldContain("obrigatória");
    }

    [Fact]
    public void Rejeita_chave_curta_demais()
    {
        // Chave curta tende a ser chave reaproveitada por acidente, e colisão
        // entre clientes diferentes recusaria um lançamento legítimo com 409.
        Should.Throw<DomainException>(() => IdempotencyKey.Of("abc"))
            .Code.ShouldBe(DomainErrorCode.InvalidIdempotencyKey);
    }

    [Fact]
    public void Rejeita_chave_longa_demais()
    {
        var longa = new string('a', IdempotencyKey.MaxLength + 1);

        Should.Throw<DomainException>(() => IdempotencyKey.Of(longa))
            .Code.ShouldBe(DomainErrorCode.InvalidIdempotencyKey);
    }

    [Theory]
    [InlineData("chave com espaco")]
    [InlineData("chave\tcom\ttab")]
    [InlineData("chave\ncom\nquebra")]
    [InlineData("chave-com-acento-ç")]
    public void Rejeita_caractere_que_confunde_log_e_indice(string entrada)
    {
        // A chave viaja em header HTTP e vira valor de índice único. Espaço e
        // caractere de controle produziriam duas chaves que parecem iguais no
        // log e são diferentes no banco — exatamente a confusão que a
        // idempotência existe para evitar.
        Should.Throw<DomainException>(() => IdempotencyKey.Of(entrada))
            .Code.ShouldBe(DomainErrorCode.InvalidIdempotencyKey);
    }

    [Fact]
    public void Valor_padrao_da_struct_e_reconhecido_como_ausente()
    {
        IdempotencyKey chavePadrao = default;

        chavePadrao.IsUnset.ShouldBeTrue();
    }
}
