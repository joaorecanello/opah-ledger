using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
    /// <para>
    /// O endereço é <c>127.0.0.1</c>, e não <c>localhost</c>, de propósito: no
    /// Windows, <c>localhost</c> resolve para <c>::1</c> e para <c>127.0.0.1</c>, e
    /// cada uma recusa a conexão com atraso — medido em cerca de cinco segundos por
    /// tentativa. Com o endereço direto a recusa é imediata, e os testes que
    /// dependem de o banco estar fora deixam de custar minutos.
    /// </para>
    /// </remarks>
    public const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=nao-usado;Username=nao-usado;Password=nao-usado";

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

    /// <summary>
    /// Configuração adicional para o teste, que vence qualquer outra fonte.
    /// </summary>
    /// <remarks>
    /// Serve a limites de resiliência — um breaker que abre com poucas falhas,
    /// uma capacidade de escrita zerada — sem precisar de um host por cenário.
    /// </remarks>
    public IReadOnlyDictionary<string, string?> Settings { get; init; } =
        new Dictionary<string, string?>();

    /// <summary>
    /// Substitui serviços do host, para testes que precisam de um ponto de
    /// controle — por exemplo, segurar uma requisição em voo.
    /// </summary>
    public Action<IServiceCollection>? OverrideServices { get; init; }

    /// <summary>Ambiente do host. "Development" liga a documentação interativa.</summary>
    public string EnvironmentName { get; init; } = "Testing";

    /// <summary>
    /// Os clientes criados enviam, por padrão, um token com acesso irrestrito.
    /// </summary>
    /// <remarks>
    /// Os testes de segurança desligam isto e escolhem o cabeçalho, caso a caso.
    /// </remarks>
    public bool AuthenticatedByDefault { get; init; } = true;

    /// <inheritdoc/>
    protected override void ConfigureClient(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        base.ConfigureClient(client);

        // Cabeçalho padrão do cliente: um cabeçalho definido na própria requisição
        // prevalece sobre ele, e é assim que os testes de segurança escolhem o seu.
        if (AuthenticatedByDefault)
        {
            client.DefaultRequestHeaders.Authorization = TestTokens.Bearer(TestTokens.CrossCustomer());
        }
    }

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment(EnvironmentName);

        builder.ConfigureAppConfiguration(configuration =>
        {
            var valores = new Dictionary<string, string?>
            {
                ["LEDGER_DB"] = ConnectionString,
                ["ConnectionStrings:Ledger"] = ConnectionString,

                // As migrations são aplicadas pelo fixture, uma vez por
                // execução. Deixar o host aplicá-las também faria instâncias
                // concorrentes disputarem o lock de migração.
                ["Ledger:MigrateOnStartup"] = "false",

                ["Ledger:Auth:Issuer"] = TestTokens.Issuer,
                ["Ledger:Auth:Audience"] = TestTokens.Audience,
                ["Ledger:Auth:SigningKey"] = TestTokens.Key,
            };

            foreach (var (chave, valor) in Settings)
            {
                valores[chave] = valor;
            }

            configuration.AddInMemoryCollection(valores);
        });

        if (OverrideServices is { } substituicoes)
        {
            builder.ConfigureTestServices(substituicoes);
        }
    }
}
