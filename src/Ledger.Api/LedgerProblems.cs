using Ledger.Domain;

namespace Ledger.Api;

/// <summary>
/// Traduz desfechos de negócio para respostas de erro no formato
/// ProblemDetails (RFC 9457).
/// </summary>
/// <remarks>
/// <para>
/// A tradução vive aqui, na borda, e não nos casos de uso. Os casos de uso
/// devolvem um desfecho de negócio — <c>ConflictingPayload</c>,
/// <c>AccountNotFound</c> — e desconhecem HTTP. É o que permite que a mesma
/// regra sirva a outra porta de entrada: consumo em lote, fila, job interno.
/// </para>
/// <para>
/// O campo <c>type</c> usa URN e não URL. Uma URL prometeria uma página de
/// documentação que não existe, e link morto em contrato de API é pior que
/// identificador opaco. A RFC exige uma referência de URI, não que ela resolva.
/// </para>
/// </remarks>
public static class LedgerProblems
{
    private const string Namespace = "urn:ledger:problem:";

    /// <summary>Chave de idempotência ausente no cabeçalho.</summary>
    public static IResult MissingIdempotencyKey(string headerName) =>
        Results.Problem(
            type: Namespace + "missing-idempotency-key",
            title: "Chave de idempotência ausente",
            statusCode: StatusCodes.Status400BadRequest,
            detail: $"O cabeçalho {headerName} é obrigatório no registro de "
                  + "movimentação. Ele é o que torna seguro repetir a requisição "
                  + "depois de um timeout, e sem ele uma queda de rede duplicaria "
                  + "o lançamento.");

    /// <summary>Tipo de movimentação não reconhecido.</summary>
    public static IResult UnknownMovementType(string? informado) =>
        Results.Problem(
            type: Namespace + "unknown-movement-type",
            title: "Tipo de movimentação inválido",
            statusCode: StatusCodes.Status400BadRequest,
            detail: $"Tipo não reconhecido: '{informado}'. Use 'Credit' ou 'Debit'.");

    /// <summary>Instante de consulta fora do formato aceito.</summary>
    /// <remarks>
    /// A mensagem lembra de codificar o sinal de mais, porque é o erro que
    /// acontece de verdade: <c>?asOf=2026-11-06T13:00:00+00:00</c> chega ao
    /// servidor com o <c>+</c> decodificado como espaço, e a data fica
    /// inválida. Vale mais dizer isso do que repetir "formato inválido".
    /// </remarks>
    public static IResult InvalidAsOf(string? informado) =>
        Results.Problem(
            type: Namespace + "invalid-as-of",
            title: "Instante de consulta inválido",
            statusCode: StatusCodes.Status400BadRequest,
            detail: $"Não foi possível interpretar '{informado}' como um instante. "
                  + "Use o formato ISO 8601, por exemplo 2026-11-05T23:59:59Z. "
                  + "Se usar deslocamento com sinal de mais, codifique-o como "
                  + "%2B — um '+' cru na URL é lido como espaço.");

    /// <summary>O chamador não pode abrir conta para o cliente informado.</summary>
    public static IResult CustomerForbidden(string? detail) =>
        Results.Problem(
            type: Namespace + "customer-forbidden",
            title: "Cliente fora do alcance do token",
            statusCode: StatusCodes.Status403Forbidden,
            detail: detail);

    /// <summary>Conta não encontrada.</summary>
    public static IResult AccountNotFound(string? detail) =>
        Results.Problem(
            type: Namespace + "account-not-found",
            title: "Conta não encontrada",
            statusCode: StatusCodes.Status404NotFound,
            detail: detail);

    /// <summary>Movimentação não encontrada.</summary>
    public static IResult MovementNotFound() =>
        Results.Problem(
            type: Namespace + "movement-not-found",
            title: "Movimentação não encontrada",
            statusCode: StatusCodes.Status404NotFound,
            detail: "Não há movimentação com esse identificador nesta conta.");

    /// <summary>
    /// A chave de idempotência já foi usada com conteúdo diferente.
    /// </summary>
    /// <remarks>
    /// <c>409</c>, e não "registrar o novo valor" nem "devolver o antigo": as
    /// duas interpretações possíveis movimentam dinheiro, e adivinhar a intenção
    /// do chamador em sistema financeiro é inaceitável.
    /// </remarks>
    public static IResult IdempotencyConflict(string? detail) =>
        Results.Problem(
            type: Namespace + "idempotency-conflict",
            title: "Conflito de idempotência",
            statusCode: StatusCodes.Status409Conflict,
            detail: detail);

    /// <summary>Regra de negócio violada.</summary>
    public static IResult Rejected(DomainErrorCode? code, string? detail) =>
        Results.Problem(
            type: Namespace + ParaKebab(code ?? DomainErrorCode.Unspecified),
            title: "Pedido recusado",
            statusCode: StatusCodes.Status400BadRequest,
            detail: detail);

    /// <summary>
    /// Converte o código do domínio para a forma usada na URN.
    /// </summary>
    /// <remarks>
    /// O código vem do domínio e aparece no contrato da API. Isso é deliberado:
    /// quem integra consegue tratar <c>amount-not-positive</c> de forma diferente
    /// de <c>currency-mismatch</c> sem interpretar texto livre, que muda.
    /// </remarks>
    public static string ParaKebab(DomainErrorCode code)
    {
        var nome = code.ToString();
        var saida = new System.Text.StringBuilder(nome.Length + 8);

        for (var i = 0; i < nome.Length; i++)
        {
            if (i > 0 && char.IsUpper(nome[i]))
            {
                saida.Append('-');
            }

            saida.Append(char.ToLowerInvariant(nome[i]));
        }

        return saida.ToString();
    }
}
