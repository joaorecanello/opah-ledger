using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ledger.Api.Contracts;
using Ledger.Api.Endpoints;
using Ledger.Api.Observability;
using Ledger.Application.Observability;
using Ledger.Domain;
using Ledger.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Ledger.Integration.Tests;

/// <summary>
/// Correlação, redação de dado sensível e métricas, pela API de verdade.
/// </summary>
[Collection(PostgresDatabase.Name)]
public sealed class ObservabilityTests(PostgresFixture fixture)
{
    private static string NovaChave() => $"chave-{Guid.CreateVersion7():N}";

    private LedgerApiFactory Api(LogCapture? captura = null) =>
        new()
        {
            ConnectionString = fixture.ConnectionString!,
            OverrideServices = captura is null
                ? null
                : servicos => servicos.AddSingleton<ILoggerProvider>(captura),
        };

    private static HttpRequestMessage Registro(
        Guid conta,
        string chave,
        decimal valor = 7531.42m,
        string? correlacao = null,
        string? referencia = "PIX-REF-CONFIDENCIAL")
    {
        var pedido = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/accounts/{conta}/movements")
        {
            Content = JsonContent.Create(new RegisterMovementRequest(
                "Credit", valor, "BRL", DateTimeOffset.UtcNow.AddMinutes(-3), referencia)),
        };
        pedido.Headers.Add(MovementEndpoints.IdempotencyKeyHeader, chave);

        if (correlacao is not null)
        {
            pedido.Headers.Add(CorrelationMiddleware.HeaderName, correlacao);
        }

        return pedido;
    }

    // ------------------------------------------------------------- correlação

    [Fact]
    public async Task Identificador_enviado_volta_na_resposta_e_fica_gravado_na_movimentacao()
    {
        // O elo que permite, meses depois, recuperar a chamada que originou um
        // lançamento contestado: o log sai da retenção quente, o fato não.
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var correlacao = $"suporte-chamado-{Guid.CreateVersion7():N}";

        await using var api = Api();
        using var cliente = api.CreateClient();

        using var resposta = await cliente.SendAsync(
            Registro(conta.Id.Value, NovaChave(), correlacao: correlacao),
            TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.Created);
        resposta.Headers.GetValues(CorrelationMiddleware.HeaderName).Single().ShouldBe(correlacao);

        var corpo = await resposta.Content.ReadFromJsonAsync<MovementResponse>(
            TestContext.Current.CancellationToken);

        await using var leitura = fixture.CreateContext();
        var gravada = await leitura.Movements.SingleAsync(
            m => m.Id == corpo!.MovementId, TestContext.Current.CancellationToken);

        gravada.CorrelationId.ShouldBe(correlacao);
    }

    [Fact]
    public async Task Sem_identificador_a_api_gera_um_e_devolve()
    {
        await using var api = Api();
        using var cliente = api.CreateClient();

        using var resposta = await cliente.GetAsync(
            new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);

        var gerado = resposta.Headers.GetValues(CorrelationMiddleware.HeaderName).Single();
        gerado.ShouldNotBeNullOrWhiteSpace();
        gerado.Length.ShouldBeLessThanOrEqualTo(CorrelationMiddleware.MaxLength);
    }

    [Fact]
    public async Task Identificador_gerado_e_diferente_a_cada_requisicao()
    {
        await using var api = Api();
        using var cliente = api.CreateClient();

        var a = (await cliente.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken))
            .Headers.GetValues(CorrelationMiddleware.HeaderName).Single();
        var b = (await cliente.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken))
            .Headers.GetValues(CorrelationMiddleware.HeaderName).Single();

        a.ShouldNotBe(b);
    }

    [Theory]
    [InlineData("com espaco")]
    [InlineData("com;ponto-e-virgula")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("acento-çã")]
    public async Task Identificador_fora_do_alfabeto_e_descartado_e_nao_corrigido(string hostil)
    {
        // O valor vem do cliente e acaba em log. Aceitar qualquer coisa deixaria
        // um chamador forjar linhas de log ou quebrar um parser de log
        // estruturado. Descartar e substituir é mais seguro que "corrigir":
        // adivinhar o que o chamador quis dizer sobre um valor malformado é pior
        // que ignorá-lo.
        await using var api = Api();
        using var cliente = api.CreateClient();

        using var pedido = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        pedido.Headers.TryAddWithoutValidation(CorrelationMiddleware.HeaderName, hostil);

        using var resposta = await cliente.SendAsync(pedido, TestContext.Current.CancellationToken);

        var devolvido = resposta.Headers.GetValues(CorrelationMiddleware.HeaderName).Single();
        devolvido.ShouldNotBe(hostil);
        devolvido.ShouldNotContain(" ");
        devolvido.ShouldNotContain("<");
    }

    [Fact]
    public async Task Identificador_longo_demais_e_descartado()
    {
        // O limite é o da coluna persistida: um valor maior faria o registro
        // falhar no banco, depois de a requisição ter sido aceita.
        await using var api = Api();
        using var cliente = api.CreateClient();

        using var pedido = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        pedido.Headers.TryAddWithoutValidation(
            CorrelationMiddleware.HeaderName, new string('a', CorrelationMiddleware.MaxLength + 1));

        using var resposta = await cliente.SendAsync(pedido, TestContext.Current.CancellationToken);

        resposta.Headers.GetValues(CorrelationMiddleware.HeaderName).Single()
            .Length.ShouldBeLessThanOrEqualTo(CorrelationMiddleware.MaxLength);
    }

    [Fact]
    public async Task Erro_em_problem_details_carrega_o_identificador_de_correlacao()
    {
        // É o que se informa ao suporte: ele encontra a requisição no log, no
        // trace e na própria movimentação.
        await using var api = Api();
        using var cliente = api.CreateClient();

        using var pedido = new HttpRequestMessage(HttpMethod.Get, "/rota-que-nao-existe");
        pedido.Headers.Add(CorrelationMiddleware.HeaderName, "chamado-4711");

        using var resposta = await cliente.SendAsync(pedido, TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var problema = await resposta.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        problema.GetProperty("correlationId").GetString().ShouldBe("chamado-4711");
    }

    // -------------------------------------------------------------- redação

    [Fact]
    public async Task Nenhum_dado_sensivel_aparece_em_log_num_fluxo_completo()
    {
        // O que um agregador de logs recebe — mensagem, campos estruturados,
        // escopos e exceção — não pode conter valor, conta, chave de idempotência
        // nem referência externa. Log é replicado, lido por mais gente que o
        // razão e retido fora do controle de acesso do banco.
        //
        // Medido antes de corrigir: o identificador da conta aparecia em quatro
        // linhas, vindo do log de requisição do framework em nível Information.
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var captura = new LogCapture();
        var chave = $"pix-e3012-{Guid.CreateVersion7():N}";

        await using var api = Api(captura);
        using var cliente = api.CreateClient();

        // Fluxo completo: registro, repetição, conflito, recusa, saldo.
        (await cliente.SendAsync(Registro(conta.Id.Value, chave), TestContext.Current.CancellationToken)).Dispose();
        (await cliente.SendAsync(Registro(conta.Id.Value, chave), TestContext.Current.CancellationToken)).Dispose();
        (await cliente.SendAsync(Registro(conta.Id.Value, chave, 9999.99m), TestContext.Current.CancellationToken)).Dispose();
        (await cliente.SendAsync(Registro(conta.Id.Value, NovaChave(), 0m), TestContext.Current.CancellationToken)).Dispose();
        (await cliente.GetAsync($"/api/v1/accounts/{conta.Id.Value}/balance", TestContext.Current.CancellationToken)).Dispose();
        (await cliente.GetAsync($"/api/v1/accounts/{Guid.CreateVersion7()}/balance", TestContext.Current.CancellationToken)).Dispose();

        var tudo = captura.TextoCompleto;

        tudo.ShouldNotContain(conta.Id.Value.ToString(), Case.Insensitive);
        tudo.ShouldNotContain(conta.Id.Value.ToString("N"), Case.Insensitive);
        tudo.ShouldNotContain(conta.CustomerId.Value.ToString(), Case.Insensitive);
        tudo.ShouldNotContain("7531");
        tudo.ShouldNotContain("9999");
        tudo.ShouldNotContain(chave);
        tudo.ShouldNotContain("PIX-REF-CONFIDENCIAL");
    }

    [Fact]
    public async Task Falha_de_dependencia_nao_vaza_conta_nem_endereco_do_banco_no_log()
    {
        // O caminho de erro é o que mais tenta logar demais: a exceção do driver
        // traz host e porta, e a requisição tem a conta no caminho.
        var captura = new LogCapture();
        var conta = Guid.CreateVersion7();

        await using var api = new LedgerApiFactory
        {
            Settings = new Dictionary<string, string?>
            {
                ["Ledger:Resilience:MaxRetries"] = "0",
                ["Ledger:Resilience:ConnectTimeout"] = "00:00:01",
            },
            OverrideServices = servicos => servicos.AddSingleton<ILoggerProvider>(captura),
        };
        using var cliente = api.CreateClient();

        using var resposta = await cliente.SendAsync(
            Registro(conta, NovaChave(), correlacao: "chamado-503"), TestContext.Current.CancellationToken);
        resposta.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        var aplicacao = string.Join(
            '\n',
            captura.Linhas
                .Where(l => l.Categoria.StartsWith("Ledger", StringComparison.Ordinal))
                .Select(l => l.Texto));

        aplicacao.ShouldNotBeNullOrWhiteSpace("a falha precisa ter sido registrada em algum lugar");
        aplicacao.ShouldNotContain(conta.ToString(), Case.Insensitive);
        aplicacao.ShouldNotContain("7531");

        // O identificador chega por escopo a toda linha da requisição, inclusive
        // às de camadas que nunca ouviram falar dele.
        aplicacao.ShouldContain("chamado-503");

        // A rota aparece como template: diz qual operação foi, não de quem.
        aplicacao.ShouldContain("/api/v1/accounts/{accountId");
    }

    [Fact]
    public void Referencia_de_log_e_estavel_opaca_e_distinta_por_conta()
    {
        var a = AccountId.From(Guid.CreateVersion7());
        var b = AccountId.From(Guid.CreateVersion7());

        LogRef.For(a).ShouldBe(LogRef.For(a), "estável: várias linhas da mesma conta se correlacionam");
        LogRef.For(a).ShouldNotBe(LogRef.For(b));
        LogRef.For(a).Length.ShouldBe(LogRef.Length);
        LogRef.For(a).ShouldMatch("^[0-9a-f]+$");

        // Unidirecional: nada do identificador sobra na referência.
        var id = a.Value.ToString("N");
        LogRef.For(a).ShouldNotContain(id[..6]);
        LogRef.For(a).ShouldNotContain(id[^6..]);
    }

    // ---------------------------------------------------------------- traces

    [Fact]
    public void Processador_troca_o_caminho_pelo_template_quando_a_rota_foi_resolvida()
    {
        using var fonte = new ActivitySource("teste.redacao");
        using var ouvinte = new ActivityListener
        {
            ShouldListenTo = origem => origem.Name == "teste.redacao",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(ouvinte);

        using var span = fonte.StartActivity("GET")!;
        var conta = Guid.CreateVersion7();
        span.SetTag("url.path", $"/api/v1/accounts/{conta}/balance");
        span.SetTag("http.target", $"/api/v1/accounts/{conta}/balance");
        span.SetTag("http.route", "/api/v1/accounts/{accountId}/balance");

        new RouteRedactionProcessor().OnEnd(span);

        span.GetTagItem("url.path").ShouldBe("/api/v1/accounts/{accountId}/balance");
        span.GetTagItem("http.target").ShouldBe("/api/v1/accounts/{accountId}/balance");
        span.Tags.Select(t => t.Value).ShouldNotContain(v => v!.Contains(conta.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void Processador_nao_inventa_template_quando_nao_ha_rota()
    {
        // Um 404 não tem rota resolvida. O caminho de uma rota que não existe não
        // identifica conta alguma, e inventar um template seria registrar uma
        // operação que não aconteceu.
        using var fonte = new ActivitySource("teste.redacao.sem-rota");
        using var ouvinte = new ActivityListener
        {
            ShouldListenTo = origem => origem.Name == "teste.redacao.sem-rota",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(ouvinte);

        using var span = fonte.StartActivity("GET")!;
        span.SetTag("url.path", "/qualquer/coisa");

        new RouteRedactionProcessor().OnEnd(span);

        span.GetTagItem("url.path").ShouldBe("/qualquer/coisa");
    }

    // ------------------------------------------------- métricas pela API real

    [Fact]
    public async Task Registro_pela_api_incrementa_a_metrica_de_negocio_do_proprio_host()
    {
        // Verifica a FIAÇÃO, não só a lógica: as métricas só existem em produção
        // se o contêiner de serviços entregar o LedgerMetrics aos casos de uso. Os
        // testes de aplicação passam o objeto à mão e não pegariam um registro
        // esquecido.
        var conta = await LedgerTestData.CriarContaAsync(fixture);

        await using var api = Api();
        using var cliente = api.CreateClient();

        var metricas = api.Services.GetRequiredService<LedgerMetrics>();
        var postadas = 0L;

        using var ouvinte = new MeterListener();
        ouvinte.InstrumentPublished = (instrumento, o) =>
        {
            if (ReferenceEquals(instrumento.Meter, metricas.Meter)
                && instrumento.Name == "ledger.movements.posted")
            {
                o.EnableMeasurementEvents(instrumento);
            }
        };
        ouvinte.SetMeasurementEventCallback<long>(
            (_, valor, _, _) => Interlocked.Add(ref postadas, valor));
        ouvinte.Start();

        (await cliente.SendAsync(Registro(conta.Id.Value, NovaChave()), TestContext.Current.CancellationToken)).Dispose();
        (await cliente.SendAsync(Registro(conta.Id.Value, NovaChave()), TestContext.Current.CancellationToken)).Dispose();

        postadas.ShouldBe(2);
    }
}
