using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ledger.Api.Contracts;
using Ledger.Api.Endpoints;
using Ledger.Domain;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Integration.Tests;

/// <summary>
/// A escala do cenário 3: mil lançamentos na mesma conta, concorrentes, com cada
/// um enviado duas vezes ao mesmo tempo.
/// </summary>
/// <remarks>
/// <para>
/// É o teste que dá lastro empírico à decisão central
/// ([ADR-0002](../../docs/adr/0002-ledger-append-only.md)). A hipótese sobre o legado
/// é que o saldo, guardado como coluna mutável, perde atualizações sob
/// concorrência. Aqui a mesma conta recebe tudo ao mesmo tempo, pela API de
/// verdade, e a soma tem de fechar ao centavo.
/// </para>
/// <para>
/// Cada lançamento é enviado <b>duas vezes simultâneas</b> com a mesma chave —
/// o retry de um cliente com timeout. Duas coisas precisam valer juntas: nenhuma
/// duplicata (a soma não pode contar o lançamento duas vezes) e nenhuma perda (os
/// mil existem).
/// </para>
/// </remarks>
[Collection(PostgresDatabase.Name)]
public sealed class ConcurrencyAtScaleTests(PostgresFixture fixture)
{
    private const int Lancamentos = 1000;
    private const int Paralelismo = 64;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Mil_lancamentos_concorrentes_com_retry_simultaneo_fecham_ao_centavo()
    {
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var prefixo = Guid.CreateVersion7().ToString("N");

        await using var api = new LedgerApiFactory { ConnectionString = fixture.ConnectionString! };
        using var cliente = api.CreateClient();

        // Pares de crédito e débito com centavos que não são representáveis em
        // ponto flutuante binário (10,01 e 3,33): se algum caminho convertesse
        // para double, a soma não fecharia.
        static (string Tipo, decimal Valor) Operacao(int i) =>
            i % 2 == 0 ? ("Credit", 10.01m) : ("Debit", 3.33m);

        var tempos = new System.Collections.Concurrent.ConcurrentBag<double>();
        var fila = new SemaphoreSlim(Paralelismo);
        var cronometro = Stopwatch.StartNew();

        // Fixo: o retry reenvia o MESMO pedido. Um instante recalculado a cada envio
        // faria as duas cópias divergirem, e a API (corretamente) responderia 409.
        var ocorridaEm = DateTimeOffset.UtcNow.AddMinutes(-1);

        async Task<(int Indice, HttpStatusCode Status, Guid? MovimentoId)> Enviar(int i)
        {
            await fila.WaitAsync(TestContext.Current.CancellationToken);

            try
            {
                var (tipo, valor) = Operacao(i);
                using var pedido = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/accounts/{conta.Id.Value}/movements")
                {
                    Content = JsonContent.Create(new RegisterMovementRequest(
                        tipo, valor, "BRL", ocorridaEm)),
                };
                pedido.Headers.Add(MovementEndpoints.IdempotencyKeyHeader, $"escala-{prefixo}-{i:D4}");

                var inicio = Stopwatch.GetTimestamp();
                using var resposta = await cliente.SendAsync(pedido, TestContext.Current.CancellationToken);
                tempos.Add(Stopwatch.GetElapsedTime(inicio).TotalMilliseconds);

                Guid? id = null;

                if (resposta.IsSuccessStatusCode)
                {
                    var corpo = await resposta.Content.ReadFromJsonAsync<MovementResponse>(
                        Json, TestContext.Current.CancellationToken);
                    id = corpo!.MovementId;
                }

                return (i, resposta.StatusCode, id);
            }
            finally
            {
                fila.Release();
            }
        }

        // Cada índice duas vezes, intercalados: 0,0,1,1,2,2...
        var resultados = await Task.WhenAll(
            Enumerable.Range(0, Lancamentos).SelectMany(i => new[] { Enviar(i), Enviar(i) }));

        cronometro.Stop();

        // 1) Nada foi recusado ou falhou: nem 409, nem 429, nem 5xx.
        resultados.Select(r => r.Status).Distinct().Order()
            .ShouldBe([HttpStatusCode.OK, HttpStatusCode.Created]);

        // 2) Exatamente um 201 e um 200 por lançamento, e o MESMO movimento nos dois.
        resultados.Count(r => r.Status == HttpStatusCode.Created).ShouldBe(Lancamentos);
        resultados.Count(r => r.Status == HttpStatusCode.OK).ShouldBe(Lancamentos);
        resultados.GroupBy(r => r.Indice)
            .ShouldAllBe(g => g.Select(r => r.MovimentoId).Distinct().Count() == 1);

        // 3) No banco: mil movimentações, nenhuma a mais, nenhuma a menos.
        await using var leitura = fixture.CreateContext();
        (await leitura.Movements.CountAsync(m => m.AccountId == conta.Id.Value, TestContext.Current.CancellationToken))
            .ShouldBe(Lancamentos);

        // 4) A soma fecha ao centavo: 500 x 10,01 - 500 x 3,33 = 3.340,00.
        using var resposta = await cliente.GetAsync(
            $"/api/v1/accounts/{conta.Id.Value}/balance", TestContext.Current.CancellationToken);
        resposta.StatusCode.ShouldBe(HttpStatusCode.OK);
        var saldo = await resposta.Content.ReadFromJsonAsync<BalanceResponse>(Json, TestContext.Current.CancellationToken);

        saldo!.Balance.ShouldBe(3340.00m);
        saldo.MovementCount.ShouldBe(Lancamentos);

        // Números da execução, para o registro. Não são asserção: dependem da
        // máquina, e um limite de latência em teste automatizado é flaky por
        // definição. O que o teste afirma é a correção.
        var ordenados = tempos.Order().ToArray();
        var relatorio =
            $"escala: {resultados.Length} requisições em {cronometro.Elapsed.TotalSeconds:F1}s "
            + $"({resultados.Length / cronometro.Elapsed.TotalSeconds:F0} req/s), "
            + $"p50={Percentil(ordenados, 0.50):F0}ms p95={Percentil(ordenados, 0.95):F0}ms "
            + $"p99={Percentil(ordenados, 0.99):F0}ms, paralelismo {Paralelismo}";

        TestContext.Current.SendDiagnosticMessage(relatorio);

        // O executor não exibe mensagens de diagnóstico por padrão; esta variável
        // permite gravar a linha em arquivo quando se quer o número.
        if (Environment.GetEnvironmentVariable("LEDGER_SCALE_REPORT") is { Length: > 0 } caminho)
        {
            await File.AppendAllTextAsync(caminho, relatorio + Environment.NewLine, TestContext.Current.CancellationToken);
        }
    }

    private static double Percentil(double[] ordenados, double p) =>
        ordenados[(int)Math.Min(ordenados.Length - 1, Math.Ceiling(p * ordenados.Length) - 1)];
}
