using Microsoft.AspNetCore.Diagnostics;

namespace Ledger.Api.Observability;

/// <summary>
/// Rótulo de rota seguro para log e para etiqueta de métrica.
/// </summary>
/// <remarks>
/// O caminho de uma requisição carrega o identificador da conta
/// (<c>/api/v1/accounts/01a1bc13-.../balance</c>). O <b>template</b> da rota —
/// <c>/api/v1/accounts/{accountId}/balance</c> — diz qual operação foi e não diz
/// de quem. Também tem cardinalidade fechada: usado como etiqueta, produz uma
/// série por rota, e não uma por conta.
/// </remarks>
public static class RouteLabel
{
    /// <summary>Rótulo devolvido quando a requisição não casou com nenhuma rota.</summary>
    public const string Unmatched = "(sem rota)";

    /// <summary>Devolve o template da rota que atendeu a requisição.</summary>
    public static string For(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // O middleware de exceção limpa o endpoint antes de chamar o tratador, para
        // reexecutar o pipeline. Sem olhar o que ele guardou, justamente a falha —
        // a linha que mais se quer atribuir a uma operação — sairia como "sem rota".
        var endpoint = context.GetEndpoint()
            ?? context.Features.Get<IExceptionHandlerFeature>()?.Endpoint;

        return endpoint is RouteEndpoint rota
            ? rota.RoutePattern.RawText ?? Unmatched
            : Unmatched;
    }
}
