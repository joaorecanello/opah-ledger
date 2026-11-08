using Ledger.Infrastructure.Resilience;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ledger.Infrastructure.Persistence;

/// <summary>
/// Configuração única do acesso ao PostgreSQL.
/// </summary>
/// <remarks>
/// Um só ponto aplica provider, timeouts e política de repetição, e é usado pelo
/// registro da aplicação e pelos testes que precisam reproduzir exatamente a
/// mesma configuração. Se cada um montasse a sua, o teste de resiliência
/// provaria o comportamento de um contexto que não é o de produção.
/// </remarks>
public static class LedgerDbContextOptions
{
    /// <summary>Aplica provider, timeouts e retry limitado ao construtor de opções.</summary>
    /// <param name="builder">Construtor de opções do contexto.</param>
    /// <param name="connectionString">Connection string do PostgreSQL.</param>
    /// <param name="resilience">Limites de tempo e de repetição.</param>
    public static DbContextOptionsBuilder Apply(
        DbContextOptionsBuilder builder,
        string connectionString,
        LedgerResilienceOptions resilience)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resilience);

        return builder.UseNpgsql(
            ComTimeouts(connectionString, resilience),
            npgsql =>
            {
                npgsql.MigrationsHistoryTable("__migrations");
                npgsql.CommandTimeout((int)resilience.CommandTimeout.TotalSeconds);

                if (resilience.MaxRetries > 0)
                {
                    // Só falhas transitórias, dentro de um orçamento de tempo,
                    // e com o mesmo critério do circuito e do 503.
                    //
                    // O retry é seguro aqui por um motivo específico deste
                    // sistema: a escrita é idempotente. Uma repetição depois de
                    // um commit cuja confirmação se perdeu encontra a chave já
                    // registrada, viola o unique constraint, e o caso de uso
                    // responde com a resposta original em vez de duplicar o
                    // lançamento.
                    npgsql.ExecutionStrategy(dependencias => new LedgerExecutionStrategy(
                        dependencias,
                        resilience.MaxRetries,
                        resilience.MaxRetryDelay,
                        resilience.RetryBudget));
                }
            });
    }

    /// <summary>
    /// Aplica o timeout de conexão, a menos que a connection string já o defina.
    /// </summary>
    /// <remarks>
    /// Quem escreveu um valor explícito na connection string sabe o que quis, e
    /// o padrão não deve sobrepô-lo.
    /// </remarks>
    public static string ComTimeouts(string connectionString, LedgerResilienceOptions resilience)
    {
        ArgumentNullException.ThrowIfNull(resilience);

        var construtor = new NpgsqlConnectionStringBuilder(connectionString);

        if (!construtor.ContainsKey("Timeout"))
        {
            construtor.Timeout = (int)Math.Max(1, resilience.ConnectTimeout.TotalSeconds);
        }

        return construtor.ConnectionString;
    }
}
