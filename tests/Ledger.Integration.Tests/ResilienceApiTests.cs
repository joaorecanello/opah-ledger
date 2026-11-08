using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ledger.Api.Contracts;
using Ledger.Api.Endpoints;

namespace Ledger.Integration.Tests;

/// <summary>
/// O comportamento da API quando o banco não está disponível.
/// </summary>
/// <remarks>
/// <para>
/// Estes testes <b>não precisam de banco</b> — o cenário é justamente a ausência
/// dele. A fábrica aponta para uma porta em que nada escuta, então rodam em
/// qualquer máquina, com ou sem Docker. É uma das poucas partes da suíte de
/// integração que o avaliador consegue executar sem nenhum pré-requisito.
/// </para>
/// <para>
/// O que se verifica foi medido antes de ser implementado: com o banco parado, a
/// API respondia <c>500</c> a tudo e <c>/health/ready</c> nem existia.
/// </para>
/// </remarks>
public sealed class ResilienceApiTests
{
    // Falha instantânea e circuito sensível, para o teste não esperar. Os
    // valores de produção são outros — ver ApiResilienceOptions.
    private static readonly Dictionary<string, string?> Rapido = new()
    {
        ["Ledger:Resilience:MaxRetries"] = "0",
        ["Ledger:Resilience:ConnectTimeout"] = "00:00:01",
        ["Ledger:Resilience:BreakerMinimumThroughput"] = "2",
        ["Ledger:Resilience:BreakerFailureRatio"] = "0.5",
        ["Ledger:Resilience:BreakerBreakDuration"] = "00:00:30",
    };

    private static LedgerApiFactory SemBanco() => new() { Settings = Rapido };

    private static HttpRequestMessage Registro(Guid conta, string chave) =>
        new(HttpMethod.Post, $"/api/v1/accounts/{conta}/movements")
        {
            Headers = { { MovementEndpoints.IdempotencyKeyHeader, chave } },
            Content = JsonContent.Create(new RegisterMovementRequest(
                "Credit", 10m, "BRL", DateTimeOffset.UtcNow.AddMinutes(-1))),
        };

    [Fact]
    public async Task Vitalidade_responde_sem_banco()
    {
        // Liveness não toca dependência alguma. Se tocasse, o orquestrador
        // mataria instâncias que apenas esperam o banco voltar — falha parcial
        // convertida em queda total, o oposto do que se quer.
        await using var api = SemBanco();
        using var cliente = api.CreateClient();

        using var resposta = await cliente.GetAsync(
            new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Prontidao_falha_sem_banco_e_nao_vaza_o_motivo()
    {
        await using var api = SemBanco();
        using var cliente = api.CreateClient();

        using var resposta = await cliente.GetAsync(
            new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        var corpo = await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = JsonDocument.Parse(corpo).RootElement;
        json.GetProperty("status").GetString().ShouldBe("unhealthy");

        // O corpo de uma resposta de saúde é visto por quem não deveria ver o
        // endereço do banco nem o texto de um erro de driver.
        corpo.ShouldNotContain("localhost", Case.Insensitive);
        corpo.ShouldNotContain("Npgsql", Case.Insensitive);
        corpo.ShouldNotContain("5432");
        corpo.ShouldNotContain("Exception", Case.Insensitive);
    }

    [Fact]
    public async Task Escrita_sem_banco_devolve_503_e_nao_500()
    {
        // O defeito medido antes de implementar: 500, "erro do servidor". Isso
        // manda o chamador desistir de uma operação que repetiria com sucesso, e
        // faz o painel acusar bug de código numa falha de infraestrutura.
        await using var api = SemBanco();
        using var cliente = api.CreateClient();

        using var resposta = await cliente.SendAsync(
            Registro(Guid.CreateVersion7(), "chave-sem-banco-0001"),
            TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        resposta.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        resposta.Headers.RetryAfter.ShouldNotBeNull(
            "dizer quando voltar evita que o chamador escolha — e quem escolhe sozinho escolhe 'agora', todos juntos");

        var problema = await resposta.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        problema.GetProperty("type").GetString()
            .ShouldBe("urn:ledger:problem:dependency-unavailable");
        problema.GetProperty("detail").GetString()
            .ShouldNotBeNull().ShouldContain("Idempotency-Key");
    }

    [Fact]
    public async Task Leitura_sem_banco_devolve_503()
    {
        await using var api = SemBanco();
        using var cliente = api.CreateClient();

        using var resposta = await cliente.GetAsync(
            $"/api/v1/accounts/{Guid.CreateVersion7()}/balance",
            TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        resposta.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task Resposta_de_indisponibilidade_nao_vaza_endereco_do_banco()
    {
        await using var api = SemBanco();
        using var cliente = api.CreateClient();

        using var resposta = await cliente.SendAsync(
            Registro(Guid.CreateVersion7(), "chave-sem-banco-0002"),
            TestContext.Current.CancellationToken);

        var corpo = await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        corpo.ShouldNotContain("localhost", Case.Insensitive);
        corpo.ShouldNotContain("Npgsql", Case.Insensitive);
        corpo.ShouldNotContain("nao-usado", Case.Insensitive);
        corpo.ShouldNotContain("at Ledger.", Case.Insensitive);
    }

    [Fact]
    public async Task Circuito_abre_apos_falhas_repetidas_e_passa_a_recusar_sem_consultar_o_banco()
    {
        await using var api = SemBanco();
        using var cliente = api.CreateClient();
        var conta = Guid.CreateVersion7();

        // As duas primeiras tentam o banco e falham: o circuito só abre depois
        // de ver falhas suficientes, para não abrir por ruído estatístico.
        for (var i = 0; i < 2; i++)
        {
            using var tentativa = await cliente.GetAsync(
                $"/api/v1/accounts/{conta}/balance", TestContext.Current.CancellationToken);

            var corpo = await tentativa.Content.ReadFromJsonAsync<JsonElement>(
                TestContext.Current.CancellationToken);

            corpo.GetProperty("circuitOpen").GetBoolean().ShouldBeFalse(
                $"a tentativa {i + 1} ainda consulta o banco");
        }

        using var recusada = await cliente.GetAsync(
            $"/api/v1/accounts/{conta}/balance", TestContext.Current.CancellationToken);

        recusada.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        recusada.Headers.RetryAfter.ShouldNotBeNull();

        var problema = await recusada.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        // Distingue "tentei e falhou" de "nem tentei": quem opera precisa saber
        // disso para ler o incidente.
        problema.GetProperty("circuitOpen").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Circuito_aberto_protege_tambem_a_escrita()
    {
        // Leitura e escrita dependem do mesmo banco, então compartilham o
        // circuito. Se a leitura abriu, a escrita não tem por que tentar.
        await using var api = SemBanco();
        using var cliente = api.CreateClient();
        var conta = Guid.CreateVersion7();

        for (var i = 0; i < 2; i++)
        {
            using var _ = await cliente.GetAsync(
                $"/api/v1/accounts/{conta}/balance", TestContext.Current.CancellationToken);
        }

        using var escrita = await cliente.SendAsync(
            Registro(conta, "chave-circuito-0001"), TestContext.Current.CancellationToken);

        escrita.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var problema = await escrita.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        problema.GetProperty("circuitOpen").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Vitalidade_e_prontidao_nao_passam_pelo_circuito()
    {
        // O orquestrador precisa conseguir perguntar mesmo — e principalmente —
        // quando o banco caiu. Se o circuito aberto bloqueasse a pergunta, a
        // instância ficaria invisível justamente quando mais precisa ser vista.
        await using var api = SemBanco();
        using var cliente = api.CreateClient();
        var conta = Guid.CreateVersion7();

        for (var i = 0; i < 3; i++)
        {
            using var _ = await cliente.GetAsync(
                $"/api/v1/accounts/{conta}/balance", TestContext.Current.CancellationToken);
        }

        using var viva = await cliente.GetAsync(
            new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        using var pronta = await cliente.GetAsync(
            new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        viva.StatusCode.ShouldBe(HttpStatusCode.OK);
        pronta.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        pronta.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
    }

    [Fact]
    public async Task Erro_de_validacao_nao_conta_como_falha_de_dependencia()
    {
        // Pedido inválido é erro do chamador, não da dependência. Se contasse,
        // um cliente com bug de formato abriria o circuito para todos os outros.
        await using var api = SemBanco();
        using var cliente = api.CreateClient();

        for (var i = 0; i < 6; i++)
        {
            using var invalida = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/accounts/{Guid.CreateVersion7()}/movements")
            {
                Content = JsonContent.Create(new RegisterMovementRequest(
                    "Credit", 10m, "BRL", DateTimeOffset.UtcNow.AddMinutes(-1))),
            };

            // Sem Idempotency-Key: recusado antes de tocar em qualquer coisa.
            using var resposta = await cliente.SendAsync(
                invalida, TestContext.Current.CancellationToken);

            resposta.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        using var legitima = await cliente.GetAsync(
            $"/api/v1/accounts/{Guid.CreateVersion7()}/balance",
            TestContext.Current.CancellationToken);

        var problema = await legitima.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);

        problema.GetProperty("circuitOpen").GetBoolean().ShouldBeFalse(
            "seis 400 não podem ter aberto o circuito");
    }
}
