using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Ledger.Integration.Tests;

/// <summary>
/// Sobe a API em memória para os testes de integração.
/// </summary>
/// <remarks>
/// <para>
/// Em memória, e não em processo separado: sem porta a alocar, sem espera por
/// readiness e sem instabilidade de startup no CI.
/// </para>
/// <para>
/// A configuração é injetada por uma fonte em memória adicionada <b>depois</b>
/// das padrões, logo ela vence inclusive a variável de ambiente do processo.
/// Isso importa porque a variável é do processo e o processo é compartilhado por
/// todos os testes — sem a sobreposição, um teste não conseguiria apontar para
/// um banco próprio.
/// </para>
/// </remarks>
public sealed class LedgerApiFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Connection string usada quando o teste não precisa de banco.
    /// </summary>
    /// <remarks>
    /// Sintaticamente válida e deliberadamente inalcançável. O host registra o
    /// contexto sem conectar, então os testes que só exercitam o pipeline HTTP —
    /// liveness, formato de erro — sobem sem banco algum. Uma porta impossível
    /// garante que, se algum deles tocar o banco por acidente, a falha seja
    /// evidente em lugar de silenciosamente usar o banco de outro teste.
    /// </remarks>
    public const string UnreachableConnectionString =
        "Host=localhost;Port=1;Database=nao-usado;Username=nao-usado;Password=nao-usado";

    /// <summary>
    /// Banco ao qual a API vai apontar.
    /// </summary>
    /// <remarks>
    /// Propriedade de inicialização, e não argumento de construtor: o xUnit
    /// instancia um fixture de classe sem argumentos, e um construtor com
    /// parâmetro — mesmo opcional — faz a resolução falhar. Quem precisa de um
    /// banco escreve
    /// <c>new LedgerApiFactory { ConnectionString = ... }</c>.
    /// </remarks>
    public string ConnectionString { get; init; } = UnreachableConnectionString;

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration(configuration =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LEDGER_DB"] = ConnectionString,
                ["ConnectionStrings:Ledger"] = ConnectionString,

                // As migrations são aplicadas pelo fixture, uma vez por
                // execução. Deixar o host aplicá-las também faria instâncias
                // concorrentes disputarem o lock de migração.
                ["Ledger:MigrateOnStartup"] = "false",
            }));
    }
}
