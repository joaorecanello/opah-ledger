using Ledger.Application.Abstractions;
using Ledger.Application.Accounts;
using Ledger.Application.Balances;
using Ledger.Application.Movements;
using Ledger.Application.Observability;
using Ledger.Infrastructure.Persistence;
using Ledger.Infrastructure.Resilience;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Infrastructure;

/// <summary>Registra o razão no contêiner de injeção de dependência.</summary>
public static class LedgerServiceCollectionExtensions
{
    /// <summary>
    /// Registra persistência, relógio e casos de uso do razão.
    /// </summary>
    /// <param name="services">Coleção de serviços.</param>
    /// <remarks>
    /// <para>
    /// A composição mora na infraestrutura, e não na camada de aplicação, porque
    /// é aqui que se sabe quais implementações existem. A aplicação declara as
    /// portas e não conhece quem as implementa.
    /// </para>
    /// <para>
    /// A connection string é resolvida <b>na construção do contexto</b>, a partir
    /// do <see cref="IConfiguration"/> do contêiner — não como argumento deste
    /// método. O motivo está em <see cref="LedgerConnectionString"/>: lida no
    /// registro, ela ignoraria qualquer fonte de configuração que o pipeline do
    /// host adicionasse depois.
    /// </para>
    /// <para>
    /// O contexto é <c>Scoped</c>: um por requisição. Isso importa no caso de uso
    /// de registro — a tentativa duplicada limpa o rastreador de mudanças, e um
    /// contexto compartilhado entre requisições concorrentes transformaria essa
    /// limpeza em efeito colateral sobre o trabalho de outra requisição.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddLedger(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDbContext<LedgerDbContext>((provider, options) =>
        {
            var configuracao = provider.GetRequiredService<IConfiguration>();

            LedgerDbContextOptions.Apply(
                options,
                LedgerConnectionString.Resolve(configuracao),
                LedgerResilienceOptions.From(configuracao));
        });

        services.AddSingleton<LedgerDatabaseProbe>();

        // Criadas pela fábrica de medidores do host, e não como estado estático:
        // cada host tem as suas séries, e a descoberta pelo nome "Ledger" continua
        // funcionando para o OpenTelemetry. Ver LedgerMetrics.
        services.AddSingleton<LedgerMetrics>();

        services.AddScoped<ILedgerRepository, LedgerRepository>();
        services.AddScoped<ISnapshotStore, SnapshotStore>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(new RegisterMovementOptions());
        services.AddSingleton(new GetBalanceOptions());
        services.AddSingleton(new ConsolidateSnapshotsOptions());
        services.AddSingleton(new ReconcileSnapshotsOptions());
        services.AddScoped<RegisterMovementHandler>();
        services.AddScoped<GetBalanceHandler>();
        services.AddScoped<GetMovementHandler>();
        services.AddScoped<OpenAccountHandler>();
        services.AddScoped<ConsolidateSnapshotsHandler>();
        services.AddScoped<ReconcileSnapshotsHandler>();

        return services;
    }
}
