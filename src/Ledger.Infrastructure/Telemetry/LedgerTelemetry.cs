using Ledger.Application.Observability;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Ledger.Infrastructure.Telemetry;

/// <summary>
/// Registra a telemetria do razão, comum à API e ao worker.
/// </summary>
/// <remarks>
/// <para>
/// Instrumenta com OpenTelemetry e exporta por OTLP, que é o padrão aberto: o
/// banco pode ter Datadog, Grafana, New Relic ou Application Insights — e
/// provavelmente vai trocar de um para outro —, e a troca é de destino, não de
/// instrumentação.
/// </para>
/// <para>
/// <b>O exportador só é ligado quando <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> está
/// definida.</b> Sem ela, o SDK tentaria enviar para <c>localhost:4317</c> e
/// registraria uma falha de conexão a cada intervalo, em toda máquina de quem
/// avalia o projeto. Observabilidade que gera ruído de erro por padrão ensina a
/// ignorar o log.
/// </para>
/// <para>
/// A variável é lida direto do ambiente do processo, e não do <c>IConfiguration</c>,
/// porque é o contrato da especificação do OpenTelemetry: qualquer SDK, em
/// qualquer linguagem, a lê do ambiente. Diferente de uma opção de aplicação,
/// ninguém espera encontrá-la num <c>appsettings</c>.
/// </para>
/// </remarks>
public static class LedgerTelemetry
{
    /// <summary>Variável padrão do OpenTelemetry com o endereço do coletor.</summary>
    public const string OtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>Registra traces e métricas do razão.</summary>
    /// <param name="services">Coleção de serviços.</param>
    /// <param name="serviceName">Nome do serviço, que identifica a origem no backend.</param>
    public static OpenTelemetryBuilder AddLedgerTelemetry(
        this IServiceCollection services,
        string serviceName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var telemetria = services.AddOpenTelemetry()
            .ConfigureResource(recurso => recurso.AddService(serviceName))
            .WithTracing(rastro => rastro
                // Antes de qualquer exportador: processadores rodam na ordem de
                // registro, e a redação precisa acontecer antes de o span sair.
                .AddProcessor<RouteRedactionProcessor>()
                .AddSource("Npgsql"))
            .WithMetrics(metricas => metricas
                .AddMeter(LedgerMetrics.MeterName)
                .AddMeter("Npgsql"));

        services.AddSingleton<RouteRedactionProcessor>();

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OtlpEndpointVariable)))
        {
            telemetria.UseOtlpExporter();
        }

        return telemetria;
    }
}
