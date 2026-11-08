using Ledger.Application.Abstractions;
using Ledger.Application.Balances;
using Ledger.Application.Movements;
using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Infrastructure;

/// <summary>Registra o razão no contêiner de injeção de dependência.</summary>
public static class LedgerServiceCollectionExtensions
{
    /// <summary>
    /// Registra persistência, relógio e casos de uso do razão.
    /// </summary>
    /// <param name="services">Coleção de serviços.</param>
    /// <param name="connectionString">Connection string do PostgreSQL.</param>
    /// <remarks>
    /// <para>
    /// A composição mora na infraestrutura, e não na camada de aplicação, porque
    /// é aqui que se sabe quais implementações existem. A aplicação declara as
    /// portas e não conhece quem as implementa.
    /// </para>
    /// <para>
    /// O contexto é <c>Scoped</c>: um por requisição. Isso importa no caso de uso
    /// de registro — a tentativa duplicada limpa o rastreador de mudanças, e um
    /// contexto compartilhado entre requisições concorrentes transformaria essa
    /// limpeza em efeito colateral sobre o trabalho de outra requisição.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddLedger(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<LedgerDbContext>(options =>
            options.UseNpgsql(
                connectionString,
                npgsql => npgsql.MigrationsHistoryTable("__migrations")));

        services.AddScoped<ILedgerRepository, LedgerRepository>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(new RegisterMovementOptions());
        services.AddSingleton(new GetBalanceOptions());
        services.AddScoped<RegisterMovementHandler>();
        services.AddScoped<GetBalanceHandler>();

        return services;
    }
}
