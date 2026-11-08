using Ledger.Infrastructure.Resilience;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Ledger.Api.Resilience;

/// <summary>
/// Circuito que protege o banco de uma carga que ele não consegue atender.
/// </summary>
/// <remarks>
/// <para>
/// Quando o banco cai, cada requisição que chega tenta conectar, espera o limite
/// de conexão e as repetições, e só então falha. Com tráfego de milhares por
/// segundo isso empilha conexões e threads esperando por algo que não vai
/// responder, e a aplicação inteira fica lenta por causa de uma dependência que
/// só ela está falhando. O circuito corta isso: depois de falhas suficientes,
/// recusa na hora e dá tempo ao banco de se recuperar sem receber a carga que o
/// derrubou.
/// </para>
/// <para>
/// <b>É por instância</b>, e isso é deliberado e tem um custo. Cada instância
/// aprende sozinha que o banco caiu, o que custa algumas requisições falhas por
/// instância. Compartilhar estado entre instâncias exigiria uma dependência a
/// mais — e a dependência a mais seria exatamente o tipo de coisa que falha junto
/// com o banco.
/// </para>
/// </remarks>
public sealed class DependencyGuard
{
    private readonly ResiliencePipeline _pipeline;

    /// <summary>Cria o circuito a partir das opções.</summary>
    public DependencyGuard(ApiResilienceOptions options, ILogger<DependencyGuard> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = options.BreakerFailureRatio,
                MinimumThroughput = options.BreakerMinimumThroughput,
                SamplingDuration = options.BreakerSamplingDuration,
                BreakDuration = options.BreakerBreakDuration,

                // A mesma definição de "falha de dependência" do retry e do
                // mapeamento para 503. Três mecanismos que discordassem sobre o
                // que é transitório seriam três fontes de comportamento
                // inconsistente: repetir o que o circuito ignora, ou abrir o
                // circuito por um erro que não é de dependência.
                ShouldHandle = new PredicateBuilder().Handle<Exception>(
                    erro => TransientFailure.IsTransient(erro) || erro is TimeoutRejectedException),

                OnOpened = _ =>
                {
                    ApiLog.CircuitoAberto(logger, options.BreakerBreakDuration.TotalSeconds);
                    return default;
                },
                OnClosed = _ =>
                {
                    ApiLog.CircuitoFechado(logger);
                    return default;
                },
                OnHalfOpened = _ =>
                {
                    ApiLog.CircuitoSemiAberto(logger);
                    return default;
                },
            })
            .Build();
    }

    /// <summary>Executa a operação sob o circuito.</summary>
    /// <exception cref="BrokenCircuitException">O circuito está aberto.</exception>
    public ValueTask<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> operation,
        CancellationToken cancellationToken) =>
        _pipeline.ExecuteAsync(operation, cancellationToken);
}

/// <summary>
/// Aplica o circuito a cada requisição dos endpoints de negócio.
/// </summary>
/// <remarks>
/// Fica num filtro de endpoint, e não em cada caso de uso, para que a proteção
/// valha para qualquer rota nova sem ninguém precisar lembrar de adicioná-la. O
/// filtro só observa: a falha original continua subindo, e é o tratador de
/// exceção que a traduz para <c>503</c>.
/// </remarks>
public sealed class DependencyGuardFilter(DependencyGuard guard) : IEndpointFilter
{
    /// <inheritdoc/>
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        return await guard.ExecuteAsync(
            async _ => await next(context).ConfigureAwait(false),
            context.HttpContext.RequestAborted).ConfigureAwait(false);
    }
}
