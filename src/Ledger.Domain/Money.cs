namespace Ledger.Domain;

/// <summary>
/// Valor monetário: quantia em <see cref="decimal"/> mais a moeda.
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que <see cref="decimal"/> e nunca <see cref="double"/>.</b>
/// <c>double</c> é base 2 e não representa 0,1 exatamente. Somar centavos
/// acumula erro, e num razão contábil erro acumulado é dinheiro que não fecha.
/// <c>decimal</c> é base 10 e exato para estas quantias. Há teste de
/// arquitetura que falha o build se <c>double</c> ou <c>float</c> aparecerem
/// nesta camada.
/// </para>
/// <para>
/// <b>Por que quatro casas e não duas.</b> Existem cenários financeiros com
/// fração de centavo — taxas, rateio, câmbio. Arredondar na borda do domínio
/// perderia informação que não volta. Valor com mais de quatro casas é
/// rejeitado em lugar de arredondado: arredondamento silencioso move dinheiro
/// sem que ninguém tenha decidido isso.
/// </para>
/// </remarks>
public readonly record struct Money : IComparable<Money>
{
    /// <summary>Número máximo de casas decimais aceito.</summary>
    public const int MaxScale = 4;

    /// <summary>
    /// Zero na escala canônica, usado para padronizar a representação.
    /// </summary>
    private const decimal CanonicalScale = 0.0000m;

    private Money(decimal amount, Currency currency)
    {
        Amount = amount;
        Currency = currency;
    }

    /// <summary>Quantia, com no máximo <see cref="MaxScale"/> casas decimais.</summary>
    public decimal Amount { get; }

    /// <summary>Moeda da quantia.</summary>
    public Currency Currency { get; }

    /// <summary>A quantia é exatamente zero.</summary>
    public bool IsZero => Amount == 0m;

    /// <summary>A quantia é maior que zero.</summary>
    public bool IsPositive => Amount > 0m;

    /// <summary>A quantia é menor que zero.</summary>
    public bool IsNegative => Amount < 0m;

    /// <summary>Cria um valor monetário.</summary>
    /// <param name="amount">Quantia, com no máximo quatro casas decimais.</param>
    /// <param name="currency">Moeda; não pode ser o valor padrão da struct.</param>
    /// <exception cref="DomainException">
    /// A moeda não foi informada, ou a quantia tem casas decimais em excesso.
    /// </exception>
    public static Money Of(decimal amount, Currency currency)
    {
        if (currency.IsUnset)
        {
            throw DomainException.For(
                DomainErrorCode.MissingCurrency,
                "Valor monetário exige moeda.");
        }

        if (amount.Scale > MaxScale)
        {
            throw DomainException.For(
                DomainErrorCode.MoneyScaleExceeded,
                $"Valor monetário aceita no máximo {MaxScale} casas decimais. "
                + $"Recebido: {amount} com {amount.Scale}.");
        }

        // Normaliza para a escala canônica. Não é arredondamento — a escala
        // excessiva já foi recusada acima —, é padding: 150,75 e 150,7500 são a
        // mesma quantia, e o tipo passa a ter uma representação só.
        //
        // Isso importa porque o PostgreSQL devolve numeric(19,4) sempre com
        // quatro casas. Sem normalizar aqui, o valor em memória e o valor lido
        // do banco serializariam diferente em JSON — "150.75" e "150.7500" —, e
        // a resposta de um registro discordaria da resposta do replay do mesmo
        // lançamento.
        //
        // A soma com zero na escala alvo é o jeito de padronizar: na aritmética
        // decimal, a escala do resultado é a maior das duas operandas.
        return new Money(amount + CanonicalScale, currency);
    }

    /// <summary>Cria o valor zero na moeda informada.</summary>
    public static Money Zero(Currency currency) => Of(0m, currency);

    /// <summary>Soma dois valores da mesma moeda.</summary>
    /// <exception cref="DomainException">As moedas são diferentes.</exception>
    public Money Add(Money other)
    {
        GarantirMesmaMoeda(this, other);
        return new Money(Amount + other.Amount, Currency);
    }

    /// <summary>Subtrai um valor da mesma moeda.</summary>
    /// <exception cref="DomainException">As moedas são diferentes.</exception>
    public Money Subtract(Money other)
    {
        GarantirMesmaMoeda(this, other);
        return new Money(Amount - other.Amount, Currency);
    }

    /// <summary>Inverte o sinal da quantia, mantendo a moeda.</summary>
    public Money Negate() => new(-Amount, Currency);

    /// <inheritdoc cref="Add(Money)"/>
    public static Money operator +(Money left, Money right) => left.Add(right);

    /// <inheritdoc cref="Subtract(Money)"/>
    public static Money operator -(Money left, Money right) => left.Subtract(right);

    /// <inheritdoc cref="Negate()"/>
    public static Money operator -(Money value) => value.Negate();

    /// <inheritdoc/>
    /// <exception cref="DomainException">As moedas são diferentes.</exception>
    public int CompareTo(Money other)
    {
        GarantirMesmaMoeda(this, other);
        return Amount.CompareTo(other.Amount);
    }

    /// <summary>Compara duas quantias da mesma moeda.</summary>
    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    /// <summary>Compara duas quantias da mesma moeda.</summary>
    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    /// <summary>Compara duas quantias da mesma moeda.</summary>
    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    /// <summary>Compara duas quantias da mesma moeda.</summary>
    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    /// <inheritdoc/>
    public override string ToString() =>
        Currency.IsUnset
            ? Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : $"{Currency.Code} {Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static void GarantirMesmaMoeda(Money left, Money right)
    {
        if (left.Currency.IsUnset || right.Currency.IsUnset)
        {
            throw DomainException.For(
                DomainErrorCode.MissingCurrency,
                "Operação monetária exige moeda em ambos os valores.");
        }

        if (left.Currency != right.Currency)
        {
            throw DomainException.For(
                DomainErrorCode.CurrencyMismatch,
                $"Operação entre moedas diferentes: {left.Currency.Code} e {right.Currency.Code}. "
                + "Conversão é decisão de negócio e não acontece implicitamente.");
        }
    }
}
