using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Ledger.Integration.Tests;

/// <summary>
/// Sobe a API em memoria para os testes de integracao.
/// </summary>
/// <remarks>
/// Em memoria, nao em processo separado: sem porta a alocar, sem espera por
/// readiness e sem flakiness de startup no CI. A partir de E06 esta fabrica
/// tambem substitui a connection string pela do container PostgreSQL do teste.
/// </remarks>
public sealed class LedgerApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Testing");
    }
}
