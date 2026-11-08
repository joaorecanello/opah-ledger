using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Ledger.Integration.Tests;

/// <summary>
/// O contrato publicado e a interface Swagger.
/// </summary>
/// <remarks>
/// Não precisam de banco: documentação não toca dados.
/// </remarks>
public sealed class OpenApiTests
{
    private static LedgerApiFactory Desenvolvimento() =>
        new() { EnvironmentName = "Development", AuthenticatedByDefault = false };

    private static async Task<JsonElement> DocumentoAsync(HttpClient cliente)
    {
        using var resposta = await cliente.GetAsync(
            new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.OK);

        return await resposta.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Documento_e_publico_em_desenvolvimento_mesmo_sem_token()
    {
        // A política padrão nega tudo. Sem AllowAnonymous o documento responderia 401
        // — e foi exatamente o que a introdução da autenticação teria causado.
        await using var api = Desenvolvimento();
        using var cliente = api.CreateClient();

        var documento = await DocumentoAsync(cliente);

        documento.GetProperty("info").GetProperty("title").GetString().ShouldBe("Ledger API");
    }

    [Fact]
    public async Task Documento_declara_o_esquema_bearer_jwt()
    {
        await using var api = Desenvolvimento();
        using var cliente = api.CreateClient();

        var esquema = (await DocumentoAsync(cliente))
            .GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");

        esquema.GetProperty("type").GetString().ShouldBe("http");
        esquema.GetProperty("scheme").GetString().ShouldBe("bearer");
        esquema.GetProperty("bearerFormat").GetString().ShouldBe("JWT");
    }

    [Fact]
    public async Task Toda_rota_de_negocio_exige_bearer_no_documento()
    {
        // O documento tem de dizer a verdade: a interface só envia o token nas
        // operações que declaram o requisito.
        await using var api = Desenvolvimento();
        using var cliente = api.CreateClient();

        var caminhos = (await DocumentoAsync(cliente)).GetProperty("paths");
        var operacoes = 0;

        foreach (var caminho in caminhos.EnumerateObject())
        {
            foreach (var operacao in caminho.Value.EnumerateObject())
            {
                operacoes++;
                operacao.Value.TryGetProperty("security", out var seguranca)
                    .ShouldBeTrue($"{operacao.Name.ToUpperInvariant()} {caminho.Name} sem requisito de segurança");
                seguranca.GetArrayLength().ShouldBeGreaterThan(0);
            }
        }

        operacoes.ShouldBeGreaterThanOrEqualTo(4);
    }

    [Fact]
    public async Task Registro_de_movimentacao_documenta_a_chave_de_idempotencia_obrigatoria()
    {
        await using var api = Desenvolvimento();
        using var cliente = api.CreateClient();

        var post = (await DocumentoAsync(cliente))
            .GetProperty("paths").GetProperty("/api/v1/accounts/{accountId}/movements").GetProperty("post");

        var chave = post.GetProperty("parameters").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "Idempotency-Key");

        chave.GetProperty("in").GetString().ShouldBe("header");
        chave.GetProperty("required").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Rotas_de_saude_nao_aparecem_na_documentacao()
    {
        await using var api = Desenvolvimento();
        using var cliente = api.CreateClient();

        var caminhos = (await DocumentoAsync(cliente)).GetProperty("paths").EnumerateObject().Select(p => p.Name);

        caminhos.ShouldNotContain(c => c.StartsWith("/health", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Interface_swagger_responde_em_desenvolvimento()
    {
        await using var api = Desenvolvimento();
        using var cliente = api.CreateClient();

        using var resposta = await cliente.GetAsync(
            new Uri("/swagger/index.html", UriKind.Relative), TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("<title>Ledger API</title>");
    }

    [Theory]
    [InlineData("/swagger/index.html")]
    [InlineData("/openapi/v1.json")]
    public async Task Fora_de_desenvolvimento_a_documentacao_nao_e_exposta(string rota)
    {
        // Documentação detalhada de uma API financeira é reconhecimento gratuito para
        // quem ataca. Em produção, nada disto é mapeado.
        await using var api = new LedgerApiFactory { EnvironmentName = "Production", AuthenticatedByDefault = false };
        using var cliente = api.CreateClient();

        using var resposta = await cliente.GetAsync(new Uri(rota, UriKind.Relative), TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Unauthorized);
        (await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldNotContain("Ledger API");
    }
}
