namespace Ledger.Domain;

/// <summary>
/// Código de erro de domínio, usado para mapear a falha para a resposta HTTP
/// sem que a camada de API precise conhecer tipos de exceção um a um.
/// </summary>
public enum DomainErrorCode
{
    /// <summary>Valor não atribuído. Nunca deve ocorrer.</summary>
    Unspecified = 0,

    /// <summary>Código de moeda fora do formato ISO 4217 alfabético.</summary>
    InvalidCurrencyCode,

    /// <summary>Moeda ausente onde ela é obrigatória.</summary>
    MissingCurrency,

    /// <summary>Operação entre valores de moedas diferentes.</summary>
    CurrencyMismatch,

    /// <summary>Valor monetário com mais casas decimais do que o permitido.</summary>
    MoneyScaleExceeded,

    /// <summary>Valor de movimentação menor ou igual a zero.</summary>
    AmountNotPositive,

    /// <summary>Identificador obrigatório não informado.</summary>
    MissingIdentifier,

    /// <summary>Chave de idempotência ausente ou fora do formato aceito.</summary>
    InvalidIdempotencyKey,

    /// <summary>Tipo de movimentação não reconhecido.</summary>
    InvalidMovementType,

    /// <summary>Instante obrigatório não informado.</summary>
    MissingTimestamp,

    /// <summary>Conta encerrada não aceita novas movimentações.</summary>
    AccountClosed,

    /// <summary>Texto obrigatório ausente, ou acima do tamanho permitido.</summary>
    InvalidText,
}

/// <summary>
/// Violação de invariante de domínio.
/// </summary>
/// <remarks>
/// <para>
/// Um único tipo de exceção com <see cref="Code"/>, em lugar de uma hierarquia
/// com uma classe por erro. A razão é o consumidor: a camada de API precisa
/// traduzir a falha para ProblemDetails (RFC 9457), e um código é mapeável por
/// tabela, enquanto uma hierarquia exige um <c>catch</c> por tipo que alguém
/// esquece de atualizar ao adicionar o próximo erro.
/// </para>
/// <para>
/// O preço é não poder filtrar por tipo no <c>catch</c>. Em troca, adicionar um
/// erro novo é adicionar um valor ao enum — e o compilador aponta o mapeamento
/// incompleto na API, em vez de o erro cair silenciosamente num handler
/// genérico.
/// </para>
/// <para>
/// Estas exceções são a última linha de defesa, não a validação de entrada.
/// Dado não confiável vindo de HTTP é validado na borda, com mensagem de erro
/// por campo. Se uma destas exceções chega a ser lançada em produção, há um
/// caminho de código que montou um objeto de domínio inválido — é defeito, não
/// entrada ruim.
/// </para>
/// </remarks>
public sealed class DomainException : Exception
{
    /// <summary>Cria a exceção sem informação adicional.</summary>
    public DomainException()
        : this(DomainErrorCode.Unspecified, "Invariante de domínio violada.")
    {
    }

    /// <summary>Cria a exceção com uma mensagem.</summary>
    public DomainException(string message)
        : this(DomainErrorCode.Unspecified, message)
    {
    }

    /// <summary>Cria a exceção com uma mensagem e a causa original.</summary>
    public DomainException(string message, Exception innerException)
        : base(message, innerException) => Code = DomainErrorCode.Unspecified;

    /// <summary>Cria a exceção com o código do erro e uma mensagem.</summary>
    public DomainException(DomainErrorCode code, string message)
        : base(message) => Code = code;

    /// <summary>Código que identifica o invariante violado.</summary>
    public DomainErrorCode Code { get; }

    /// <summary>Lança a exceção com o código e a mensagem informados.</summary>
    /// <param name="code">Código do invariante violado.</param>
    /// <param name="message">Descrição do que foi violado.</param>
    internal static DomainException For(DomainErrorCode code, string message) => new(code, message);
}
