using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ledger.Integration.Tests;

/// <summary>
/// Sobe a API em memória para os testes de integração.
/// </summary>
/// <remarks>
/// Em memória, e não em processo separado: sem porta a alocar, sem espera por
/// readiness e sem instabilidade de startup no CI. Quando a persistência
/// entrar, esta fábrica também substitui a connection string pela do container
/// PostgreSQL do teste.
/// </remarks>
public sealed class LedgerApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
    }
}
