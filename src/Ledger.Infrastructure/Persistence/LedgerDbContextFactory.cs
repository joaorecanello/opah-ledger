using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ledger.Infrastructure.Persistence;

/// <summary>
/// Cria o contexto para as ferramentas de linha de comando do EF Core.
/// </summary>
/// <remarks>
/// <para>
/// Usado apenas em tempo de design, por <c>dotnet ef migrations</c>. A aplicação
/// em execução recebe o contexto por injeção de dependência e nunca passa por
/// aqui.
/// </para>
/// <para>
/// A connection string vem da variável <c>LEDGER_DB</c>, com um padrão que
/// aponta para o PostgreSQL local do <c>docker compose</c>. Gerar migration não
/// conecta ao banco — o EF só precisa de um provider para saber que dialeto
/// emitir —, então o padrão não precisa de um banco de pé.
/// </para>
/// </remarks>
public sealed class LedgerDbContextFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    /// <summary>Variável de ambiente que define a connection string.</summary>
    public const string ConnectionStringVariable = "LEDGER_DB";

    /// <summary>Connection string usada quando a variável não está definida.</summary>
    public const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=ledger;Username=ledger;Password=ledger";

    /// <inheritdoc/>
    public LedgerDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable)
            ?? DefaultConnectionString;

        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__migrations"))
            .Options;

        return new LedgerDbContext(options);
    }
}
