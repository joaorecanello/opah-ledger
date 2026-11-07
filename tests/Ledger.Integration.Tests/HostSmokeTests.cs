using System.Net;

namespace Ledger.Integration.Tests;

/// <summary>
/// Verifica que a estrutura minima executavel (E03) de fato executa.
/// </summary>
/// <remarks>
/// Um scaffold que compila mas nao sobe nao e um scaffold. Este teste falha
/// cedo em erro de composicao do host (DI mal registrada, middleware em ordem
/// invalida, configuracao ausente) - a classe de erro que, sem ele, so apareceria
/// ao rodar a aplicacao a mao.
/// </remarks>
public sealed class HostSmokeTests(LedgerApiFactory factory) : IClassFixture<LedgerApiFactory>
{
    [Fact]
    public async Task Host_sobe_e_responde_liveness()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Rota_inexistente_responde_problem_details_e_nao_html()
    {
        // RFC 9457 vale para o pipeline inteiro, nao so para os handlers que
        // eu escrevo. Garante que o cliente nunca receba pagina de erro HTML
        // onde espera JSON.
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/rota-que-nao-existe", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }
}
