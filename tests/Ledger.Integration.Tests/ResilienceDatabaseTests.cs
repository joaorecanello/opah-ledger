using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ledger.Api.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ledger.Integration.Tests;

/// <summary>
/// Proteções da borda HTTP com o banco disponível.
/// </summary>
[Collection(PostgresDatabase.Name)]
public sealed class ResilienceDatabaseTests(PostgresFixture fixture)
{
    private LedgerApiFactory ApiComBanco(Dictionary<string, string?>? extras = null)
    {
        fixture.SkipIfUnavailable();

        return new LedgerApiFactory
        {
            ConnectionString = fixture.ConnectionString!,
            Settings = extras ?? new Dictionary<string, string?>(),
        };
    }

    [Fact]
    public async Task Prontidao_responde_200_com_banco_disponivel()
    {
        await using var api = ApiComBanco();
        using var cliente = api.CreateClient();

        using var resposta = await cliente.GetAsync(
            new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        resposta.StatusCode.ShouldBe(HttpStatusCode.OK);

        var json = JsonDocument.Parse(
            await resposta.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        json.GetProperty("status").GetString().ShouldBe("healthy");
        json.GetProperty("checks")[0].GetProperty("name").GetString().ShouldBe("database");
    }

    private static HttpRequestMessage PedidoDeEscrita(Guid conta, string chave)
    {
        var pedido = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/accounts/{conta}/movements")
        {
            Content = JsonContent.Create(new RegisterMovementRequest(
                "Credit", 10m, "BRL", DateTimeOffset.UtcNow.AddMinutes(-1))),
        };
        pedido.Headers.Add("Idempotency-Key", chave);
        return pedido;
    }

    [Fact]
    public async Task Escrita_saturada_nao_derruba_a_leitura()
    {
        // O isolamento de recursos (bulkhead) em ação. Uma escrita fica em voo
        // ocupando a única vaga de escrita; a seguinte é recusada na hora; e a
        // leitura, com limite próprio, continua atendendo.
        //
        // É a propriedade que importa: com um limite compartilhado, uma rajada
        // de consultas esgotaria a capacidade de registrar — a operação cuja
        // falha custa dinheiro. Aqui acontece o inverso.
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        using var relogio = new GatedClock();

        await using var api = new LedgerApiFactory
        {
            ConnectionString = fixture.ConnectionString!,
            Settings = new Dictionary<string, string?> { ["Ledger:Resilience:WriteConcurrency"] = "1" },
            OverrideServices = servicos =>
            {
                servicos.RemoveAll<Ledger.Application.Abstractions.IClock>();
                servicos.AddSingleton<Ledger.Application.Abstractions.IClock>(relogio);
            },
        };
        fixture.SkipIfUnavailable();
        using var cliente = api.CreateClient();

        relogio.Arm();
        var segurada = cliente.SendAsync(
            PedidoDeEscrita(conta.Id.Value, $"seg-{Guid.CreateVersion7():N}"),
            TestContext.Current.CancellationToken);

        try
        {
            await relogio.Entered.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            using var recusada = await cliente.SendAsync(
                PedidoDeEscrita(conta.Id.Value, $"rec-{Guid.CreateVersion7():N}"),
                TestContext.Current.CancellationToken);

            recusada.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
            recusada.Headers.RetryAfter.ShouldNotBeNull();
            recusada.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

            var problema = await recusada.Content.ReadFromJsonAsync<JsonElement>(
                TestContext.Current.CancellationToken);
            problema.GetProperty("type").GetString().ShouldBe("urn:ledger:problem:too-many-requests");
            problema.GetProperty("detail").GetString().ShouldNotBeNull().ShouldContain("escrita");

            using var leitura = await cliente.GetAsync(
                $"/api/v1/accounts/{conta.Id.Value}/balance", TestContext.Current.CancellationToken);

            leitura.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            relogio.Release();
        }

        using var concluida = await segurada;
        concluida.StatusCode.ShouldBe(
            HttpStatusCode.Created,
            "a requisição segurada termina normalmente quando liberada");
    }

    [Fact]
    public async Task Leitura_saturada_nao_derruba_a_escrita_nem_as_sondas_de_saude()
    {
        // O isolamento vale nos dois sentidos. E as sondas de saúde ficam FORA da
        // capacidade: o orquestrador precisa conseguir perguntar mesmo quando a
        // instância está saturada. Se a pergunta fosse recusada com 429, uma
        // instância sobrecarregada pareceria morta e seria reiniciada —
        // piorando a sobrecarga que a deixou assim.
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        using var relogio = new GatedClock();

        await using var api = new LedgerApiFactory
        {
            ConnectionString = fixture.ConnectionString!,
            Settings = new Dictionary<string, string?> { ["Ledger:Resilience:ReadConcurrency"] = "1" },
            OverrideServices = servicos =>
            {
                servicos.RemoveAll<Ledger.Application.Abstractions.IClock>();
                servicos.AddSingleton<Ledger.Application.Abstractions.IClock>(relogio);
            },
        };
        fixture.SkipIfUnavailable();
        using var cliente = api.CreateClient();

        relogio.Arm();
        var segurada = cliente.GetAsync(
            $"/api/v1/accounts/{conta.Id.Value}/balance", TestContext.Current.CancellationToken);

        try
        {
            await relogio.Entered.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            using var recusada = await cliente.GetAsync(
                $"/api/v1/accounts/{conta.Id.Value}/balance", TestContext.Current.CancellationToken);
            recusada.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

            using var escrita = await cliente.PostAsJsonAsync(
                "/api/v1/accounts",
                new OpenAccountRequest(Guid.CreateVersion7(), "BRL"),
                TestContext.Current.CancellationToken);
            escrita.StatusCode.ShouldBe(HttpStatusCode.Created);

            using var viva = await cliente.GetAsync(
                new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
            using var pronta = await cliente.GetAsync(
                new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

            viva.StatusCode.ShouldBe(HttpStatusCode.OK);
            pronta.StatusCode.ShouldBe(HttpStatusCode.OK);
        }
        finally
        {
            relogio.Release();
        }

        using var concluida = await segurada;
        concluida.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Estouro_do_tempo_da_requisicao_devolve_503_com_orientacao_de_repeticao()
    {
        // Rede de segurança acima dos limites de conexão e de comando.
        //
        // Determinístico de propósito. A primeira versão usava um timeout de um
        // milissegundo e passava isolada e falhava na suíte inteira: a resolução
        // de um timer de 1 ms é pior que isso no Windows, e uma requisição que
        // termina antes de ele disparar devolve 200. Aqui a requisição é segurada
        // por mais que o dobro do limite, então o estouro acontece sempre.
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        using var relogio = new GatedClock();

        await using var api = new LedgerApiFactory
        {
            ConnectionString = fixture.ConnectionString!,
            Settings = new Dictionary<string, string?>
            {
                ["Ledger:Resilience:RequestTimeout"] = "00:00:00.300",
            },
            OverrideServices = servicos =>
            {
                servicos.RemoveAll<Ledger.Application.Abstractions.IClock>();
                servicos.AddSingleton<Ledger.Application.Abstractions.IClock>(relogio);
            },
        };
        using var cliente = api.CreateClient();

        relogio.Arm();
        var tarefa = cliente.GetAsync(
            $"/api/v1/accounts/{conta.Id.Value}/balance", TestContext.Current.CancellationToken);

        await relogio.Entered.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(800), TestContext.Current.CancellationToken);
        relogio.Release();

        using var resposta = await tarefa;

        resposta.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        resposta.Headers.RetryAfter.ShouldNotBeNull();

        var problema = await resposta.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        problema.GetProperty("detail").GetString()
            .ShouldNotBeNull().ShouldContain("tempo máximo");
    }
}
