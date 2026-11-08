using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ledger.Api.Contracts;
using Ledger.Api.Endpoints;
using Ledger.Api.Security;
using Ledger.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Ledger.Integration.Tests;

/// <summary>
/// Autenticação, autorização por escopo e titularidade da conta, pela API real.
/// </summary>
/// <remarks>
/// Os testes de rejeição não precisam de banco: a requisição deve morrer na
/// autenticação, antes de qualquer acesso a dados — e, se tocasse o banco
/// inalcançável, a resposta seria 503 e não 401, o que o teste detectaria.
/// </remarks>
[Collection(PostgresDatabase.Name)]
public sealed class SecurityTests(PostgresFixture fixture)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private LedgerApiFactory SemToken() =>
        new()
        {
            AuthenticatedByDefault = false,
            ConnectionString = fixture.ConnectionString ?? LedgerApiFactory.UnreachableConnectionString,
        };

    private static string NovaChave() => $"chave-{Guid.CreateVersion7():N}";

    private static HttpRequestMessage Pedido(HttpMethod metodo, string rota, string? token, object? corpo = null)
    {
        var pedido = new HttpRequestMessage(metodo, rota);

        if (token is not null)
        {
            pedido.Headers.Authorization = TestTokens.Bearer(token);
        }

        if (corpo is not null)
        {
            pedido.Content = JsonContent.Create(corpo);
        }

        return pedido;
    }

    private static HttpRequestMessage Registro(Guid conta, string? token) =>
        Pedido(
            HttpMethod.Post,
            $"/api/v1/accounts/{conta}/movements",
            token,
            new RegisterMovementRequest("Credit", 10m, "BRL", DateTimeOffset.UtcNow.AddMinutes(-2)))
            .Com(MovementEndpoints.IdempotencyKeyHeader, NovaChave());

    // ------------------------------------------------------ sem credencial

    public static TheoryData<string, string> RotasProtegidas() => new()
    {
        { "POST", "/api/v1/accounts" },
        { "GET", "/api/v1/accounts/01a1bc1e-ecd4-75a0-a5c4-fcead392da09/balance" },
        { "POST", "/api/v1/accounts/01a1bc1e-ecd4-75a0-a5c4-fcead392da09/movements" },
        { "GET", "/api/v1/accounts/01a1bc1e-ecd4-75a0-a5c4-fcead392da09/movements/01a1bc1e-ecd4-75a0-a5c4-fcead392da0a" },
    };

    [Theory]
    [MemberData(nameof(RotasProtegidas))]
    public async Task Sem_token_toda_rota_de_negocio_responde_401_com_desafio_bearer(string metodo, string rota)
    {
        await using var api = SemToken();
        using var cliente = api.CreateClient();

        using var resposta = await cliente.SendAsync(
            Pedido(new HttpMethod(metodo), rota, token: null, corpo: metodo == "POST" ? new { } : null),
            TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        resposta.Headers.WwwAuthenticate.Select(h => h.Scheme).ShouldContain("Bearer");
        resposta.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Vitalidade_continua_publica()
    {
        // O orquestrador não tem credencial. Se a vitalidade exigisse token, uma
        // chave expirada faria o orquestrador matar instâncias saudáveis.
        await using var api = SemToken();
        using var cliente = api.CreateClient();

        using var resposta = await cliente.GetAsync(
            new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Prontidao_continua_publica()
    {
        await using var api = SemToken();
        using var cliente = api.CreateClient();

        using var resposta = await cliente.GetAsync(
            new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        // Sem banco responde 503 — o que importa é que não é 401.
        resposta.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void Toda_rota_da_api_declara_politica_explicita()
    {
        // A política padrão (fallback) nega a quem não tem token, mas não distingue
        // leitura de escrita. Este teste garante que nenhuma rota nova nasça só com
        // o fallback: quem a escrever precisa escolher um escopo.
        using var api = SemToken();
        var rotas = api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/", StringComparison.Ordinal))
            .ToList();

        rotas.ShouldNotBeEmpty();

        foreach (var rota in rotas)
        {
            var politicas = rota.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy).ToList();

            politicas.ShouldContain(
                p => p == AuthScopes.ReadPolicy || p == AuthScopes.WritePolicy,
                $"{rota.RoutePattern.RawText} sem política de escopo");
        }
    }

    // ----------------------------------------------------- tokens inválidos

    public static TheoryData<string> TokensInvalidos()
    {
        var umaChaveOutra = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("outra-chave-que-a-api-nao-conhece-000000"));

        return new TheoryData<string>
        {
            "isto-nao-e-um-jwt",
            TestTokens.Create(crossCustomer: true, key: umaChaveOutra),
            TestTokens.Create(crossCustomer: true, expires: DateTime.UtcNow.AddMinutes(-5), notBefore: DateTime.UtcNow.AddMinutes(-10)),
            TestTokens.Create(crossCustomer: true, issuer: "emissor-desconhecido"),
            TestTokens.Create(crossCustomer: true, audience: "outro-servico"),

            // Mesma chave, outro algoritmo: a API aceita só HS256.
            TestTokens.Create(crossCustomer: true, algorithm: SecurityAlgorithms.HmacSha512),
            SemAssinatura(),
        };
    }

    private static string SemAssinatura()
    {
        static string B64(string json) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();

        return B64("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "."
            + B64($"{{\"iss\":\"{TestTokens.Issuer}\",\"aud\":\"{TestTokens.Audience}\",\"exp\":{exp},\"scope\":\"ledger:read ledger:write ledger:cross-customer\"}}")
            + ".";
    }

    [Theory]
    [MemberData(nameof(TokensInvalidos))]
    public async Task Token_invalido_e_recusado_com_401(string token)
    {
        await using var api = SemToken();
        using var cliente = api.CreateClient();

        using var resposta = await cliente.SendAsync(
            Registro(Guid.CreateVersion7(), token), TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Token_nao_aparece_em_nenhuma_linha_de_log()
    {
        // O token é credencial. Um log que o registre — numa mensagem de falha de
        // validação, por exemplo — o entrega a todos que leem logs, por toda a
        // validade dele.
        var captura = new LogCapture();
        var token = TestTokens.Create(crossCustomer: true, issuer: "emissor-desconhecido");

        await using var api = new LedgerApiFactory
        {
            AuthenticatedByDefault = false,
            OverrideServices = s => s.AddSingleton<ILoggerProvider>(captura),
        };
        using var cliente = api.CreateClient();

        using var resposta = await cliente.SendAsync(
            Registro(Guid.CreateVersion7(), token), TestContext.Current.CancellationToken);
        resposta.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var partes = token.Split('.');
        var tudo = captura.TextoCompleto;

        tudo.ShouldNotContain(token);
        tudo.ShouldNotContain(partes[1]);
        tudo.ShouldNotContain(partes[2]);
    }

    // --------------------------------------------------------------- escopo

    [Fact]
    public async Task Token_so_de_leitura_nao_registra_movimentacao()
    {
        await using var api = SemToken();
        using var cliente = api.CreateClient();
        var token = TestTokens.Create("ledger:read", crossCustomer: true);

        using var resposta = await cliente.SendAsync(
            Registro(Guid.CreateVersion7(), token), TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Token_so_de_escrita_nao_consulta_saldo()
    {
        await using var api = SemToken();
        using var cliente = api.CreateClient();
        var token = TestTokens.Create("ledger:write", crossCustomer: true);

        using var resposta = await cliente.SendAsync(
            Pedido(HttpMethod.Get, $"/api/v1/accounts/{Guid.CreateVersion7()}/balance", token),
            TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Token_com_escopo_mas_sem_alcance_e_recusado()
    {
        // ledger:read e ledger:write, porém nem customer_id nem cross-customer: o
        // token não diz a que contas chega. Tratar a omissão como "todas" seria
        // transformar esquecimento do emissor em acesso total.
        await using var api = SemToken();
        using var cliente = api.CreateClient();
        var token = TestTokens.Create("ledger:read ledger:write");

        using var leitura = await cliente.SendAsync(
            Pedido(HttpMethod.Get, $"/api/v1/accounts/{Guid.CreateVersion7()}/balance", token),
            TestContext.Current.CancellationToken);
        using var escrita = await cliente.SendAsync(
            Registro(Guid.CreateVersion7(), token), TestContext.Current.CancellationToken);

        leitura.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        escrita.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Escopo_com_nome_parecido_nao_vale()
    {
        // Comparação exata: "ledger:readonly" ou "xledger:read" não concedem
        // "ledger:read".
        await using var api = SemToken();
        using var cliente = api.CreateClient();
        var token = TestTokens.Create("ledger:readonly xledger:read ledger:", crossCustomer: true);

        using var resposta = await cliente.SendAsync(
            Pedido(HttpMethod.Get, $"/api/v1/accounts/{Guid.CreateVersion7()}/balance", token),
            TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ----------------------------------------------------------- titularidade

    private async Task<(Account Maria, Account Joao)> DuasContasAsync() =>
        (await LedgerTestData.CriarContaAsync(fixture), await LedgerTestData.CriarContaAsync(fixture));

    [Fact]
    public async Task Titular_consulta_o_proprio_saldo()
    {
        var (maria, _) = await DuasContasAsync();
        await using var api = SemToken();
        using var cliente = api.CreateClient();

        using var resposta = await cliente.SendAsync(
            Pedido(
                HttpMethod.Get,
                $"/api/v1/accounts/{maria.Id.Value}/balance",
                TestTokens.ForCustomer(maria.CustomerId.Value)),
            TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Titular_nao_consulta_saldo_de_outro_e_a_resposta_e_a_de_conta_inexistente()
    {
        // IDOR: o token de Maria é válido e tem ledger:read. Trocar o identificador
        // na URL pelo da conta de João não pode devolver nada — e a resposta não
        // pode diferir da de uma conta que não existe, senão a API revela quais
        // contas existem.
        var (maria, joao) = await DuasContasAsync();
        await using var api = SemToken();
        using var cliente = api.CreateClient();
        var token = TestTokens.ForCustomer(maria.CustomerId.Value);

        using var alheia = await cliente.SendAsync(
            Pedido(HttpMethod.Get, $"/api/v1/accounts/{joao.Id.Value}/balance", token),
            TestContext.Current.CancellationToken);
        var inexistenteId = Guid.CreateVersion7();
        using var inexistente = await cliente.SendAsync(
            Pedido(HttpMethod.Get, $"/api/v1/accounts/{inexistenteId}/balance", token),
            TestContext.Current.CancellationToken);

        alheia.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        inexistente.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var corpoAlheia = await alheia.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var corpoInexistente = await inexistente.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Mascarar(corpoAlheia, joao.Id.Value).ShouldBe(Mascarar(corpoInexistente, inexistenteId));
        corpoAlheia.ShouldNotContain(joao.CustomerId.Value.ToString());
    }

    [Fact]
    public async Task Titular_nao_registra_na_conta_de_outro_e_nada_e_gravado()
    {
        var (maria, joao) = await DuasContasAsync();
        await using var api = SemToken();
        using var cliente = api.CreateClient();

        using var resposta = await cliente.SendAsync(
            Registro(joao.Id.Value, TestTokens.ForCustomer(maria.CustomerId.Value)),
            TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await using var leitura = fixture.CreateContext();
        (await leitura.Movements.CountAsync(m => m.AccountId == joao.Id.Value, TestContext.Current.CancellationToken))
            .ShouldBe(0);
        (await leitura.IdempotencyRecords.CountAsync(r => r.AccountId == joao.Id.Value, TestContext.Current.CancellationToken))
            .ShouldBe(0);
    }

    [Fact]
    public async Task Titular_nao_le_movimentacao_de_outro_nem_com_o_identificador_certo()
    {
        var (maria, joao) = await DuasContasAsync();
        await using var api = SemToken();
        using var cliente = api.CreateClient();

        // João registra, por token próprio.
        using var criada = await cliente.SendAsync(
            Registro(joao.Id.Value, TestTokens.ForCustomer(joao.CustomerId.Value)),
            TestContext.Current.CancellationToken);
        criada.StatusCode.ShouldBe(HttpStatusCode.Created);
        var movimento = await criada.Content.ReadFromJsonAsync<MovementResponse>(Json, TestContext.Current.CancellationToken);

        var rota = $"/api/v1/accounts/{joao.Id.Value}/movements/{movimento!.MovementId}";

        using var deMaria = await cliente.SendAsync(
            Pedido(HttpMethod.Get, rota, TestTokens.ForCustomer(maria.CustomerId.Value)),
            TestContext.Current.CancellationToken);
        using var doJoao = await cliente.SendAsync(
            Pedido(HttpMethod.Get, rota, TestTokens.ForCustomer(joao.CustomerId.Value)),
            TestContext.Current.CancellationToken);

        deMaria.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        doJoao.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Repeticao_idempotente_por_outro_cliente_nao_devolve_a_resposta_original()
    {
        // A chave de idempotência é global. Se Maria descobrir a chave de João e a
        // repetir contra a conta DELE, a resposta original (com valor e
        // identificador) não pode vazar: a titularidade é checada antes do replay.
        var (maria, joao) = await DuasContasAsync();
        await using var api = SemToken();
        using var cliente = api.CreateClient();
        var chave = NovaChave();

        using var original = await cliente.SendAsync(
            Registro(joao.Id.Value, TestTokens.ForCustomer(joao.CustomerId.Value)).Com(MovementEndpoints.IdempotencyKeyHeader, chave, substituir: true),
            TestContext.Current.CancellationToken);
        original.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var intruso = await cliente.SendAsync(
            Registro(joao.Id.Value, TestTokens.ForCustomer(maria.CustomerId.Value)).Com(MovementEndpoints.IdempotencyKeyHeader, chave, substituir: true),
            TestContext.Current.CancellationToken);

        intruso.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Acesso_entre_clientes_alcanca_qualquer_conta()
    {
        var (_, joao) = await DuasContasAsync();
        await using var api = SemToken();
        using var cliente = api.CreateClient();

        using var resposta = await cliente.SendAsync(
            Pedido(HttpMethod.Get, $"/api/v1/accounts/{joao.Id.Value}/balance", TestTokens.CrossCustomer()),
            TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Token_com_cliente_e_acesso_irrestrito_vale_o_mais_restritivo()
    {
        var (maria, joao) = await DuasContasAsync();
        await using var api = SemToken();
        using var cliente = api.CreateClient();
        var token = TestTokens.Create(customer: maria.CustomerId.Value, crossCustomer: true);

        using var resposta = await cliente.SendAsync(
            Pedido(HttpMethod.Get, $"/api/v1/accounts/{joao.Id.Value}/balance", token),
            TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Cliente_restrito_abre_conta_apenas_para_si()
    {
        fixture.SkipIfUnavailable();
        await using var api = new LedgerApiFactory
        {
            AuthenticatedByDefault = false,
            ConnectionString = fixture.ConnectionString!,
        };
        using var http = api.CreateClient();
        var eu = Guid.CreateVersion7();
        var token = TestTokens.ForCustomer(eu);

        using var propria = await http.SendAsync(
            Pedido(HttpMethod.Post, "/api/v1/accounts", token, new OpenAccountRequest(eu, "BRL")),
            TestContext.Current.CancellationToken);
        using var alheia = await http.SendAsync(
            Pedido(HttpMethod.Post, "/api/v1/accounts", token, new OpenAccountRequest(Guid.CreateVersion7(), "BRL")),
            TestContext.Current.CancellationToken);

        propria.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Aqui 403 e não 404: quem pede informou o cliente, não o está descobrindo.
        alheia.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ----------------------------------------------------- configuração

    [Theory]
    [InlineData("")]
    [InlineData("nao-e-base64!!")]
    [InlineData("dG9vLXNob3J0")]
    public void Sem_chave_valida_a_aplicacao_nao_sobe(string chave)
    {
        // Não existe modo aberto. Uma chave ausente, malformada ou curta derruba a
        // inicialização — melhor que subir um razão financeiro sem validar quem
        // chama.
        using var api = new LedgerApiFactory
        {
            Settings = new Dictionary<string, string?> { ["Ledger:Auth:SigningKey"] = chave },
        };

        Should.Throw<Exception>(() => api.CreateClient().Dispose());
    }

    // Mascara o identificador pedido e descarta o que é único por requisição
    // (traceId, correlationId): o resto precisa ser idêntico.
    private static string Mascarar(string corpo, Guid id) =>
        System.Text.RegularExpressions.Regex.Replace(
            corpo.Replace(id.ToString(), "{id}", StringComparison.OrdinalIgnoreCase),
            "\"(traceId|correlationId)\":\"[^\"]*\"",
            string.Empty);
}

internal static class PedidoExtensions
{
    public static HttpRequestMessage Com(
        this HttpRequestMessage pedido,
        string cabecalho,
        string valor,
        bool substituir = false)
    {
        if (substituir)
        {
            pedido.Headers.Remove(cabecalho);
        }

        pedido.Headers.Add(cabecalho, valor);

        return pedido;
    }
}
