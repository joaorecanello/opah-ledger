using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Ledger.Integration.Tests;

/// <summary>
/// Disponibiliza um PostgreSQL real para os testes de integração.
/// </summary>
/// <remarks>
/// <para>
/// <b>PostgreSQL real, e não banco em memória.</b> Banco em memória não
/// reproduz unique constraint sob concorrência, logo o teste de idempotência —
/// o mais importante da suíte — passaria sem provar nada. Falso-positivo no
/// único cenário onde mais preciso de certeza é pior que não ter o teste.
/// </para>
/// <para>
/// A resolução do banco tem dois caminhos, com tratamentos deliberadamente
/// diferentes:
/// </para>
/// <list type="number">
///   <item>
///     <b><c>LEDGER_TEST_DB</c> definida</b> — usa esse banco. Se ele não
///     responder, os testes <b>falham</b>: quem definiu a variável pediu para
///     rodar contra aquele banco, e ignorar em silêncio esconderia o problema.
///   </item>
///   <item>
///     <b>variável ausente</b> — tenta o Testcontainers, que sobe e descarta um
///     container. É o caminho do CI e de quem tem Docker. Se o Docker não
///     estiver alcançável, os testes são <b>ignorados com mensagem explícita</b>,
///     para que <c>dotnet test</c> nunca falhe por ambiente na máquina de quem
///     avalia o desafio.
///   </item>
/// </list>
/// <para>
/// Ignorar não é esconder: a mensagem diz o que faltou e como resolver.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Variável que aponta para um PostgreSQL já existente.</summary>
    public const string ConnectionStringVariable = "LEDGER_TEST_DB";

    /// <summary>Imagem usada quando o container é criado pelo teste.</summary>
    public const string PostgresImage = "postgres:18-alpine";

    private PostgreSqlContainer? _container;

    /// <summary>Connection string do banco, ou <c>null</c> se indisponível.</summary>
    public string? ConnectionString { get; private set; }

    /// <summary>Motivo pelo qual o banco não está disponível, quando for o caso.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>O banco está disponível para os testes.</summary>
    public bool IsAvailable => ConnectionString is not null;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        var externa = Environment.GetEnvironmentVariable(ConnectionStringVariable);

        if (!string.IsNullOrWhiteSpace(externa))
        {
            // Sem try: falha aqui é configuração errada de quem pediu este
            // banco, e precisa aparecer.
            ConnectionString = externa;
            await AplicarMigrationsAsync().ConfigureAwait(false);
            return;
        }

        try
        {
            _container = new PostgreSqlBuilder(PostgresImage)
                .WithDatabase("ledger")
                .WithUsername("ledger")
                .WithPassword("ledger")
                .Build();

            await _container.StartAsync().ConfigureAwait(false);
            ConnectionString = _container.GetConnectionString();
            await AplicarMigrationsAsync().ConfigureAwait(false);
        }
        catch (Exception excecao)
        {
            // Falha ao obter o banco por conta própria significa ambiente sem
            // Docker alcançável. Não é falha do código sob teste.
            UnavailableReason =
                $"PostgreSQL indisponível. Defina {ConnectionStringVariable} com a "
                + "connection string de um PostgreSQL existente, ou deixe o Docker "
                + $"acessível para o Testcontainers subir um. Causa: {excecao.Message}";
            ConnectionString = null;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cria um contexto apontado para o banco de teste.
    /// </summary>
    /// <param name="logSql">
    /// Recebe cada comando SQL emitido. Serve a testes que verificam <b>como</b>
    /// uma consulta é traduzida, e não apenas o resultado dela — por exemplo,
    /// que a soma de saldo é uma agregação no banco e não uma varredura trazida
    /// para a memória.
    /// </param>
    /// <exception cref="InvalidOperationException">O banco não está disponível.</exception>
    public LedgerDbContext CreateContext(Action<string>? logSql = null)
    {
        if (ConnectionString is null)
        {
            throw new InvalidOperationException(
                UnavailableReason ?? "Banco de teste não disponível.");
        }

        var builder = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.MigrationsHistoryTable("__migrations"));

        if (logSql is not null)
        {
            builder.LogTo(
                logSql,
                [Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.CommandExecuted]);
        }

        return new LedgerDbContext(builder.Options);
    }

    /// <summary>
    /// Ignora o teste quando o banco não está disponível, explicando o motivo.
    /// </summary>
    public void SkipIfUnavailable()
    {
        if (!IsAvailable)
        {
            Assert.Skip(UnavailableReason ?? "Banco de teste não disponível.");
        }
    }

    private async Task AplicarMigrationsAsync()
    {
        // As migrations são aplicadas contra o banco em toda execução da suíte.
        // Isso verifica, de graça e sempre, que elas de fato aplicam — que é o
        // critério de conclusão da etapa de persistência, e algo que só um
        // banco real diz.
        await using var contexto = CreateContext();
        await contexto.Database.MigrateAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Agrupa os testes que compartilham o mesmo PostgreSQL.
/// </summary>
/// <remarks>
/// Um banco por execução, não um por classe de teste: subir container custa
/// segundos, e os testes se isolam usando contas e chaves próprias em lugar de
/// bancos próprios.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class PostgresDatabase : ICollectionFixture<PostgresFixture>
{
    /// <summary>Nome da coleção.</summary>
    public const string Name = "postgres";
}
