using System.Net;

namespace Ledger.Integration.Tests;

/// <summary>
/// Verifica que a estrutura mínima executável de fato executa.
/// </summary>
/// <remarks>
/// Um projeto que compila mas não sobe não está pronto. Estes testes falham
/// cedo em erro de composição do host — injeção de dependência mal registrada,
/// middleware em ordem inválida, configuração ausente —, que é a classe de erro
/// que, sem eles, só apareceria ao rodar a aplicação à mão.
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
        // RFC 9457 vale para o pipeline inteiro, não só para os handlers
        // escritos à mão. Garante que o cliente nunca receba página de erro
        // HTML onde espera JSON.
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/rota-que-nao-existe", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }
}
