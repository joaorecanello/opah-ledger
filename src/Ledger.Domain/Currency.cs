namespace Ledger.Domain;

/// <summary>
/// Moeda, no formato alfabético de três letras da ISO 4217.
/// </summary>
/// <remarks>
/// O projeto opera em moeda única (BRL), mas <see cref="Money"/> carrega a moeda
/// desde o início e rejeita operação entre moedas diferentes.
/// <para>
/// A razão é assimetria de custo: manter a moeda correta desde já é barato, e
/// adicioná-la depois é caro — exigiria reinterpretar todo o histórico já
/// gravado, e não há como saber retroativamente em que moeda um número estava.
/// </para>
/// </remarks>
public readonly struct Currency : IEquatable<Currency>
{
    /// <summary>Número de letras do código ISO 4217 alfabético.</summary>
    public const int CodeLength = 3;

    private readonly string? _code;

    private Currency(string code) => _code = code;

    /// <summary>Real brasileiro.</summary>
    public static Currency Brl => new("BRL");

    /// <summary>Código ISO 4217 da moeda.</summary>
    /// <exception cref="DomainException">A instância é o valor padrão.</exception>
    public string Code =>
        _code ?? throw DomainException.For(
            DomainErrorCode.MissingCurrency,
            "Moeda não informada.");

    /// <summary>
    /// Indica que a instância é o valor padrão da struct e não representa moeda
    /// alguma.
    /// </summary>
    /// <remarks>
    /// Toda struct em C# admite <c>default</c>, e não há construtor que impeça
    /// isso. Em lugar de fingir que o caso não existe, ele é nomeado aqui e
    /// verificado por quem recebe uma <see cref="Currency"/> de fora.
    /// </remarks>
    public bool IsUnset => _code is null;

    /// <summary>Cria uma moeda a partir do código ISO 4217.</summary>
    /// <param name="code">Três letras, por exemplo <c>BRL</c>.</param>
    /// <exception cref="DomainException">O código está fora do formato.</exception>
    public static Currency Of(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw DomainException.For(
                DomainErrorCode.InvalidCurrencyCode,
                "Código de moeda não informado.");
        }

        var normalizado = code.Trim().ToUpperInvariant();

        if (normalizado.Length != CodeLength)
        {
            throw DomainException.For(
                DomainErrorCode.InvalidCurrencyCode,
                $"Código de moeda deve ter {CodeLength} letras. Recebido: '{code}'.");
        }

        foreach (var caractere in normalizado)
        {
            if (!char.IsAsciiLetterUpper(caractere))
            {
                throw DomainException.For(
                    DomainErrorCode.InvalidCurrencyCode,
                    $"Código de moeda aceita apenas letras. Recebido: '{code}'.");
            }
        }

        return new Currency(normalizado);
    }

    /// <inheritdoc/>
    public bool Equals(Currency other) =>
        string.Equals(_code, other._code, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Currency outra && Equals(outra);

    /// <inheritdoc/>
    public override int GetHashCode() =>
        _code is null ? 0 : StringComparer.Ordinal.GetHashCode(_code);

    /// <inheritdoc/>
    public override string ToString() => _code ?? "(moeda não informada)";

    /// <summary>Compara duas moedas.</summary>
    public static bool operator ==(Currency left, Currency right) => left.Equals(right);

    /// <summary>Compara duas moedas.</summary>
    public static bool operator !=(Currency left, Currency right) => !left.Equals(right);
}
