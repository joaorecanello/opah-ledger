using System.Globalization;

namespace Ledger.Api.Resilience;

/// <summary>
/// Respostas de "não consigo agora, tente de novo" em ProblemDetails.
/// </summary>
/// <remarks>
/// <para>
/// <c>503</c> e <c>429</c> dizem coisas diferentes e as duas dizem a mesma coisa
/// ao chamador: <b>repita</b>. É a diferença para o <c>500</c>, que diz "defeito
/// do servidor, repetir provavelmente não ajuda". Num sistema onde repetir é
/// seguro por construção — a chave de idempotência garante —, responder
/// <c>500</c> a uma falha de dependência faz o chamador desistir de uma
/// operação que repetiria com sucesso, e faz o painel acusar defeito de código
/// numa queda de banco.
/// </para>
/// <para>
/// Todas levam <c>Retry-After</c>. Dizer ao chamador <i>quando</i> voltar evita
/// que ele escolha por conta própria — e os que escolhem sozinhos escolhem
/// "agora", todos juntos.
/// </para>
/// </remarks>
public static class DependencyProblems
{
    private const string Namespace = "urn:ledger:problem:";

    /// <summary>Uma dependência não responde.</summary>
    /// <param name="context">Contexto da requisição.</param>
    /// <param name="problemDetails">Escritor de ProblemDetails.</param>
    /// <param name="retryAfter">Quando o chamador deve tentar de novo.</param>
    /// <param name="circuitOpen">O circuito está aberto: a dependência nem foi tentada.</param>
    /// <param name="detail">Explicação, sem detalhe interno.</param>
    public static async ValueTask<bool> UnavailableAsync(
        HttpContext context,
        IProblemDetailsService problemDetails,
        TimeSpan retryAfter,
        bool circuitOpen,
        string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(problemDetails);

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = SecondsFor(retryAfter);

        var problema = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Type = Namespace + "dependency-unavailable",
            Title = "Serviço temporariamente indisponível",
            Status = StatusCodes.Status503ServiceUnavailable,

            // Genérico de propósito. O motivo real — host, porta, texto do
            // driver — vai para o log, não para a resposta: quem a lê não deveria
            // saber onde fica o banco.
            Detail = detail ?? "Uma dependência necessária não respondeu. Nada foi "
                   + "registrado por esta tentativa que não possa ser repetida com "
                   + "segurança: use a mesma Idempotency-Key.",
        };

        // Distingue "tentei e falhou" de "nem tentei": com o circuito aberto, o
        // banco não foi consultado, e quem opera precisa saber disso para ler o
        // incidente.
        problema.Extensions["circuitOpen"] = circuitOpen;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problema,
        }).ConfigureAwait(false);
    }

    /// <summary>A capacidade da instância para este tipo de operação está esgotada.</summary>
    public static async ValueTask WriteTooManyRequestsAsync(
        HttpContext context,
        IProblemDetailsService problemDetails,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(problemDetails);

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = "1";

        await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails =
            {
                Type = Namespace + "too-many-requests",
                Title = "Capacidade esgotada",
                Status = StatusCodes.Status429TooManyRequests,
                Detail = $"Esta instância já atende o máximo de operações de {operation} "
                       + "simultâneas. Tente de novo em instantes; repetir com a mesma "
                       + "Idempotency-Key é seguro.",
            },
        }).ConfigureAwait(false);
    }

    private static string SecondsFor(TimeSpan duration) =>
        Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
}
