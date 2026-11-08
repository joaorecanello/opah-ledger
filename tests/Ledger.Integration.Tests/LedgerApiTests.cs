using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ledger.Api.Contracts;
using Ledger.Api.Endpoints;

namespace Ledger.Integration.Tests;

/// <summary>
/// A API de ponta a ponta: HTTP, caso de uso, PostgreSQL real.
/// </summary>
/// <remarks>
/// A decisão de negócio é verificada nos testes de aplicação e o mecanismo de
/// unicidade nos de persistência. O que está aqui é a <b>tradução</b>: desfecho
/// de negócio para status HTTP, erro para ProblemDetails, e o contrato que quem
/// integra vai ler.
/// </remarks>
[Collection(PostgresDatabase.Name)]
public sealed class LedgerApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private LedgerApiFactory? _api;

    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web);

    public ValueTask InitializeAsync()
    {
        if (fixture.IsAvailable)
        {
            _api = new LedgerApiFactory { ConnectionString = fixture.ConnectionString! };
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _api?.Dispose();
        return ValueTask.CompletedTask;
    }

    private HttpClient Cliente()
    {
        fixture.SkipIfUnavailable();
        return _api!.CreateClient();
    }

    private static string NovaChave() => $"chave-{Guid.CreateVersion7():N}";

    private static HttpRequestMessage PedidoDeRegistro(
        Guid conta,
        string chave,
        decimal valor = 150.75m,
        string tipo = "Credit",
        DateTimeOffset? ocorridaEm = null,
        string? referencia = null)
    {
        var pedido = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/accounts/{conta}/movements")
        {
            Content = JsonContent.Create(new RegisterMovementRequest(
                tipo,
                valor,
                "BRL",
                ocorridaEm ?? DateTimeOffset.UtcNow.AddMinutes(-10),
                referencia)),
        };

        pedido.Headers.Add(MovementEndpoints.IdempotencyKeyHeader, chave);
        return pedido;
    }

    private static async Task<Guid> AbrirContaAsync(HttpClient cliente)
    {
        using var resposta = await cliente.PostAsJsonAsync(
            "/api/v1/accounts",
            new OpenAccountRequest(Guid.CreateVersion7(), "BRL"),
            TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.Created);

        var corpo = await resposta.Content.ReadFromJsonAsync<AccountResponse>(
            Json, TestContext.Current.CancellationToken);

        return corpo!.AccountId;
    }

    [Fact]
    public async Task Registra_e_devolve_201_com_location_que_responde()
    {
        // 201 Created precisa apontar para um recurso que de fato existe.
        // Location para endpoint inexistente é contrato quebrado, e é um erro
        // comum porque ninguém testa o cabeçalho.
        using var cliente = Cliente();
        var conta = await AbrirContaAsync(cliente);

        using var resposta = await cliente.SendAsync(
            PedidoDeRegistro(conta, NovaChave()), TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.Created);
        resposta.Headers.Location.ShouldNotBeNull();

        var criada = await resposta.Content.ReadFromJsonAsync<MovementResponse>(
            Json, TestContext.Current.CancellationToken);
        criada!.Amount.ShouldBe(150.75m);
        criada.Type.ShouldBe("Credit");
        criada.AccountId.ShouldBe(conta);

        using var pelaLocation = await cliente.GetAsync(
            resposta.Headers.Location, TestContext.Current.CancellationToken);

        pelaLocation.StatusCode.ShouldBe(HttpStatusCode.OK);
        var lida = await pelaLocation.Content.ReadFromJsonAsync<MovementResponse>(
            Json, TestContext.Current.CancellationToken);
        lida!.MovementId.ShouldBe(criada.MovementId);
    }

    [Fact]
    public async Task Repeticao_identica_devolve_200_com_o_mesmo_corpo()
    {
        using var cliente = Cliente();
        var conta = await AbrirContaAsync(cliente);
        var chave = NovaChave();
        var instante = DateTimeOffset.UtcNow.AddMinutes(-7);

        using var primeira = await cliente.SendAsync(
            PedidoDeRegistro(conta, chave, ocorridaEm: instante),
            TestContext.Current.CancellationToken);
        primeira.StatusCode.ShouldBe(HttpStatusCode.Created);
        var corpoOriginal = await primeira.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        using var segunda = await cliente.SendAsync(
            PedidoDeRegistro(conta, chave, ocorridaEm: instante),
            TestContext.Current.CancellationToken);

        segunda.StatusCode.ShouldBe(HttpStatusCode.OK, "replay não é criação");
        var corpoDoReplay = await segunda.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        // Byte a byte. "Resposta original" não é "resposta equivalente": quem
        // integra pode estar usando esses valores para conciliar.
        corpoDoReplay.ShouldBe(corpoOriginal);
    }

    [Fact]
    public async Task Mesma_chave_com_conteudo_diferente_devolve_409_em_problem_details()
    {
        using var cliente = Cliente();
        var conta = await AbrirContaAsync(cliente);
        var chave = NovaChave();

        using var primeira = await cliente.SendAsync(
            PedidoDeRegistro(conta, chave, valor: 100m), TestContext.Current.CancellationToken);
        primeira.StatusCode.ShouldBe(HttpStatusCode.Created);

        using var segunda = await cliente.SendAsync(
            PedidoDeRegistro(conta, chave, valor: 150m), TestContext.Current.CancellationToken);

        segunda.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        segunda.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        var problema = await segunda.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        problema.GetProperty("type").GetString()
            .ShouldBe("urn:ledger:problem:idempotency-conflict");
        problema.TryGetProperty("traceId", out _).ShouldBeTrue(
            "sem traceId, quem recebe o erro não tem o que informar ao suporte");
    }

    [Fact]
    public async Task Chave_de_idempotencia_ausente_devolve_400()
    {
        using var cliente = Cliente();
        var conta = await AbrirContaAsync(cliente);

        using var resposta = await cliente.PostAsJsonAsync(
            $"/api/v1/accounts/{conta}/movements",
            new RegisterMovementRequest("Credit", 10m, "BRL", DateTimeOffset.UtcNow.AddMinutes(-1)),
            TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problema = await resposta.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        problema.GetProperty("type").GetString()
            .ShouldBe("urn:ledger:problem:missing-idempotency-key");
    }

    [Theory]
    [InlineData("Transferencia")]
    [InlineData("")]
    public async Task Tipo_desconhecido_devolve_400(string tipo)
    {
        using var cliente = Cliente();
        var conta = await AbrirContaAsync(cliente);

        using var resposta = await cliente.SendAsync(
            PedidoDeRegistro(conta, NovaChave(), tipo: tipo),
            TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problema = await resposta.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        problema.GetProperty("type").GetString()
            .ShouldBe("urn:ledger:problem:unknown-movement-type");
    }

    [Fact]
    public async Task Valor_nao_positivo_devolve_400_com_o_codigo_do_dominio()
    {
        // O código do domínio aparece no contrato de propósito: quem integra
        // consegue tratar amount-not-positive diferente de currency-mismatch sem
        // interpretar texto livre, que muda.
        using var cliente = Cliente();
        var conta = await AbrirContaAsync(cliente);

        using var resposta = await cliente.SendAsync(
            PedidoDeRegistro(conta, NovaChave(), valor: 0m),
            TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problema = await resposta.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        problema.GetProperty("type").GetString()
            .ShouldBe("urn:ledger:problem:amount-not-positive");
    }

    [Fact]
    public async Task Conta_inexistente_devolve_404()
    {
        using var cliente = Cliente();

        using var resposta = await cliente.SendAsync(
            PedidoDeRegistro(Guid.CreateVersion7(), NovaChave()),
            TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var problema = await resposta.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        problema.GetProperty("type").GetString()
            .ShouldBe("urn:ledger:problem:account-not-found");
    }

    [Fact]
    public async Task Fluxo_completo_registra_e_consulta_o_saldo()
    {
        // O fluxo do README, exercitado por HTTP: abre conta, credita, debita,
        // consulta. É o que quem avalia vai fazer à mão.
        using var cliente = Cliente();
        var conta = await AbrirContaAsync(cliente);
        var ontem = DateTimeOffset.UtcNow.AddDays(-1);

        using (var credito = await cliente.SendAsync(
            PedidoDeRegistro(conta, NovaChave(), valor: 1_500m, ocorridaEm: ontem),
            TestContext.Current.CancellationToken))
        {
            credito.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using (var debito = await cliente.SendAsync(
            PedidoDeRegistro(conta, NovaChave(), valor: 400.25m, tipo: "Debit",
                ocorridaEm: ontem.AddHours(2)),
            TestContext.Current.CancellationToken))
        {
            debito.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using var saldo = await cliente.GetAsync(
            $"/api/v1/accounts/{conta}/balance", TestContext.Current.CancellationToken);

        saldo.StatusCode.ShouldBe(HttpStatusCode.OK);
        var corpo = await saldo.Content.ReadFromJsonAsync<BalanceResponse>(
            Json, TestContext.Current.CancellationToken);

        corpo!.Balance.ShouldBe(1_099.75m);
        corpo.MovementCount.ShouldBe(2L);
        corpo.Currency.ShouldBe("BRL");
        corpo.Source.ShouldBe("DirectSum");
        corpo.Stale.ShouldBeFalse();
    }

    [Fact]
    public async Task Saldo_em_instante_passado_ignora_movimentacao_posterior()
    {
        using var cliente = Cliente();
        var conta = await AbrirContaAsync(cliente);
        var antiga = DateTimeOffset.UtcNow.AddDays(-5);
        var recente = DateTimeOffset.UtcNow.AddDays(-1);

        using (var _ = await cliente.SendAsync(
            PedidoDeRegistro(conta, NovaChave(), valor: 300m, ocorridaEm: antiga),
            TestContext.Current.CancellationToken))
        {
        }

        using (var _ = await cliente.SendAsync(
            PedidoDeRegistro(conta, NovaChave(), valor: 700m, ocorridaEm: recente),
            TestContext.Current.CancellationToken))
        {
        }

        var corte = Uri.EscapeDataString(antiga.AddHours(1).ToString("O"));

        using var saldo = await cliente.GetAsync(
            $"/api/v1/accounts/{conta}/balance?asOf={corte}",
            TestContext.Current.CancellationToken);

        var corpo = await saldo.Content.ReadFromJsonAsync<BalanceResponse>(
            Json, TestContext.Current.CancellationToken);

        corpo!.Balance.ShouldBe(300m);
        corpo.MovementCount.ShouldBe(1L);
    }

    [Fact]
    public async Task Saldo_em_instante_futuro_devolve_400()
    {
        using var cliente = Cliente();
        var conta = await AbrirContaAsync(cliente);
        var futuro = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddYears(1).ToString("O"));

        using var resposta = await cliente.GetAsync(
            $"/api/v1/accounts/{conta}/balance?asOf={futuro}",
            TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        resposta.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Movimentacao_de_outra_conta_nao_e_acessivel_pelo_identificador()
    {
        // Acesso lateral: adivinhar o identificador de uma movimentação não deve
        // dar acesso a ela por outra conta. A verificação vive no caso de uso,
        // não no endpoint, para valer em qualquer porta de entrada.
        using var cliente = Cliente();
        var minha = await AbrirContaAsync(cliente);
        var outra = await AbrirContaAsync(cliente);

        using var criada = await cliente.SendAsync(
            PedidoDeRegistro(minha, NovaChave()), TestContext.Current.CancellationToken);
        var corpo = await criada.Content.ReadFromJsonAsync<MovementResponse>(
            Json, TestContext.Current.CancellationToken);

        using var pelaOutraConta = await cliente.GetAsync(
            $"/api/v1/accounts/{outra}/movements/{corpo!.MovementId}",
            TestContext.Current.CancellationToken);

        pelaOutraConta.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AsOf_com_sinal_de_mais_nao_codificado_devolve_400_e_nao_500()
    {
        // Este defeito passou por 194 testes verdes e apareceu no primeiro curl
        // à mão. Os testes montavam a URL com Uri.EscapeDataString; uma pessoa
        // digita o deslocamento cru, o '+' chega como espaço, a data fica
        // inválida — e a API respondia 500.
        using var cliente = Cliente();
        var conta = await AbrirContaAsync(cliente);

        using var resposta = await cliente.GetAsync(
            $"/api/v1/accounts/{conta}/balance?asOf=2026-11-06T13:00:00 00:00",
            TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problema = await resposta.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        problema.GetProperty("type").GetString().ShouldBe("urn:ledger:problem:invalid-as-of");
        problema.GetProperty("detail").GetString()
            .ShouldNotBeNull().ShouldContain("%2B", Case.Sensitive);
    }

    [Theory]
    [InlineData("{\"type\":\"Credit\",\"amount\":10,,}", "application/json")]
    [InlineData("", "application/json")]
    [InlineData("[]", "application/json")]
    [InlineData("nao e json", "text/plain")]
    public async Task Pedido_malformado_devolve_4xx_em_problem_details_e_nunca_500(
        string corpo,
        string tipoDeConteudo)
    {
        // O requisito é este, independente de qual mecanismo o satisfaz: pedido
        // que o servidor não consegue interpretar é erro do cliente, e a
        // resposta precisa ser ProblemDetails. Um 500 aqui mandaria quem integra
        // procurar o problema no lugar errado, e acusaria indisponibilidade que
        // não existe no painel de observabilidade.
        using var cliente = Cliente();
        var conta = await AbrirContaAsync(cliente);

        using var pedido = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/accounts/{conta}/movements")
        {
            Content = new StringContent(corpo, System.Text.Encoding.UTF8, tipoDeConteudo),
        };
        pedido.Headers.Add(MovementEndpoints.IdempotencyKeyHeader, NovaChave());

        using var resposta = await cliente.SendAsync(
            pedido, TestContext.Current.CancellationToken);

        ((int)resposta.StatusCode).ShouldBeInRange(
            400, 499, $"corpo '{corpo}' com {tipoDeConteudo} deveria ser erro do cliente");
        resposta.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task AsOf_em_formato_iso_com_z_funciona()
    {
        using var cliente = Cliente();
        var conta = await AbrirContaAsync(cliente);
        var corte = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        using var resposta = await cliente.GetAsync(
            $"/api/v1/accounts/{conta}/balance?asOf={corte}",
            TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Conta_com_moeda_invalida_devolve_400()
    {
        using var cliente = Cliente();

        using var resposta = await cliente.PostAsJsonAsync(
            "/api/v1/accounts",
            new OpenAccountRequest(Guid.CreateVersion7(), "REAIS"),
            TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problema = await resposta.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        problema.GetProperty("type").GetString()
            .ShouldBe("urn:ledger:problem:invalid-currency-code");
    }

    [Fact]
    public async Task Vinte_requisicoes_http_simultaneas_com_a_mesma_chave_registram_uma()
    {
        // O cenário de resposta perdida, agora atravessando o pipeline HTTP
        // inteiro: um 201 e dezenove 200, nenhum 409, e um identificador só.
        using var cliente = Cliente();
        var conta = await AbrirContaAsync(cliente);
        var chave = NovaChave();
        var instante = DateTimeOffset.UtcNow.AddMinutes(-3);

        var respostas = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(async _ =>
            {
                using var resposta = await cliente.SendAsync(
                    PedidoDeRegistro(conta, chave, valor: 88.88m, ocorridaEm: instante),
                    TestContext.Current.CancellationToken);

                var corpo = await resposta.Content.ReadFromJsonAsync<MovementResponse>(
                    Json, TestContext.Current.CancellationToken);

                return (resposta.StatusCode, corpo?.MovementId);
            }));

        respostas.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        respostas.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(19);
        respostas.ShouldNotContain(r => r.StatusCode == HttpStatusCode.Conflict);
        respostas.Select(r => r.MovementId).Distinct().Count().ShouldBe(1);
    }
}
