using Ledger.Infrastructure.Resilience;
using Microsoft.AspNetCore.Diagnostics;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Ledger.Api.Resilience;

/// <summary>
/// Traduz falha de dependência para <c>503</c> com <c>Retry-After</c>.
/// </summary>
/// <remarks>
/// <para>
/// Sem isto, uma queda de banco responde <c>500</c> — o que foi medido antes de
/// implementar: com o banco parado, escrita e leitura devolviam "erro do
/// servidor". Isso tem três consequências ruins. O chamador conclui que repetir
/// não ajuda, quando repetir é exatamente a resposta certa. O painel acusa
/// defeito de código numa falha de infraestrutura. E o texto do erro do driver,
/// com endereço e porta do banco, tem uma chance de vazar.
/// </para>
/// <para>
/// <b>Só falha transitória vira 503.</b> Violação de constraint, erro de SQL,
/// dado inválido continuam sendo <c>500</c>: são determinísticos, repetir devolve
/// o mesmo erro, e um <c>503</c> mandaria o chamador esperar por algo que nunca
/// vai se resolver. A definição é a mesma do retry e do circuito — ver
/// <see cref="TransientFailure"/>.
/// </para>
/// </remarks>
public sealed class DependencyUnavailableExceptionHandler(
    IProblemDetailsService problemDetails,
    ApiResilienceOptions options,
    ILogger<DependencyUnavailableExceptionHandler> logger) : IExceptionHandler
{
    /// <inheritdoc/>
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is BrokenCircuitException circuito)
        {
            // Circuito aberto: o banco nem foi consultado. Aviso e não erro — é o
            // sistema funcionando como projetado durante uma queda, e a 2.000
            // requisições por segundo um log de erro por requisição seria, ele
            // mesmo, parte do problema.
            ApiLog.RecusadoComCircuitoAberto(logger, httpContext.Request.Path);

            return await DependencyProblems.UnavailableAsync(
                httpContext,
                problemDetails,
                circuito.RetryAfter ?? options.BreakerBreakDuration,
                circuitOpen: true).ConfigureAwait(false);
        }

        if (exception is TimeoutRejectedException || TransientFailure.IsTransient(exception))
        {
            // O motivo detalhado vai para o log, com a exceção — é onde quem
            // opera vai procurar. A resposta não o carrega.
            ApiLog.DependenciaIndisponivel(logger, exception, httpContext.Request.Path);

            return await DependencyProblems.UnavailableAsync(
                httpContext,
                problemDetails,
                options.RetryAfter,
                circuitOpen: false).ConfigureAwait(false);
        }

        // Não é falha de dependência. Deixa seguir para o tratamento padrão, que
        // responde 500: é defeito nosso, e esconder isso atrás de um 503 seria
        // mentir sobre quem precisa agir.
        return false;
    }
}
