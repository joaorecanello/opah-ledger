using System.Text.Json;
using Ledger.Infrastructure.Resilience;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ledger.Api.Resilience;

/// <summary>
/// Pergunta se a instância pode receber tráfego: o banco responde?
/// </summary>
/// <remarks>
/// <para>
/// <b>Prontidão e vitalidade são perguntas diferentes</b>, e confundi-las é um
/// erro operacional com consequência grave. <c>/health/live</c> pergunta "o
/// processo está vivo?" e não toca dependência alguma. <c>/health/ready</c>
/// pergunta "posso receber tráfego agora?" e toca o banco.
/// </para>
/// <para>
/// Se a vitalidade verificasse o banco, o orquestrador mataria instâncias que
/// estão apenas esperando o banco voltar. O resultado é um ciclo de reinício
/// durante uma indisponibilidade de dependência — falha parcial convertida em
/// queda total, que é o oposto do que o requisito pede.
/// </para>
/// <para>
/// A prontidão falha, o balanceador tira a instância da rotação, e quando o
/// banco volta ela volta sozinha. Nada foi reiniciado.
/// </para>
/// </remarks>
public sealed class ReadinessCheck(LedgerDatabaseProbe probe, ILogger<ReadinessCheck> logger)
    : IHealthCheck
{
    /// <summary>Nome do check, que aparece na resposta.</summary>
    public const string Name = "database";

    /// <summary>Etiqueta que separa os checks de prontidão dos demais.</summary>
    public const string Tag = "ready";

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var resultado = await probe.ProbeAsync(cancellationToken).ConfigureAwait(false);

        if (resultado.IsReachable)
        {
            return HealthCheckResult.Healthy(
                data: new Dictionary<string, object>
                {
                    ["elapsedMs"] = (long)resultado.Elapsed.TotalMilliseconds,
                });
        }

        ApiLog.ProntidaoFalhou(logger);

        // Sem a exceção e sem o motivo: o corpo da resposta de saúde é visto por
        // quem não deveria ver o endereço do banco.
        return HealthCheckResult.Unhealthy(
            "O banco de dados não está respondendo.",
            data: new Dictionary<string, object>
            {
                ["elapsedMs"] = (long)resultado.Elapsed.TotalMilliseconds,
            });
    }

    /// <summary>Escreve a resposta de prontidão como JSON, sem detalhe interno.</summary>
    public static Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        context.Response.ContentType = "application/json";

        var corpo = new
        {
            status = report.Status.ToString().ToLowerInvariant(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString().ToLowerInvariant(),
                elapsedMs = e.Value.Data.TryGetValue("elapsedMs", out var ms) ? ms : null,
            }),
        };

        return context.Response.WriteAsync(
            JsonSerializer.Serialize(corpo, JsonSerializerOptions.Web));
    }
}
