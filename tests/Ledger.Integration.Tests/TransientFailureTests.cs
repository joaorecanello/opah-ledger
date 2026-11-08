using System.Net.Sockets;
using Ledger.Infrastructure.Persistence;
using Ledger.Infrastructure.Resilience;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Ledger.Integration.Tests;

/// <summary>
/// O que conta como falha transitória, e como o retry se comporta diante dela.
/// </summary>
/// <remarks>
/// Não precisam de banco. A definição de "transitório" é compartilhada pelo
/// retry, pelo circuito e pelo mapeamento para <c>503</c>, então um erro aqui se
/// propaga para os três.
/// </remarks>
public sealed class TransientFailureTests
{
    [Fact]
    public void Conexao_que_caiu_e_transitoria()
    {
        var erro = new NpgsqlException("conexão perdida", new IOException("reset"));

        TransientFailure.IsTransient(erro).ShouldBeTrue();
    }

    [Fact]
    public void Banco_inalcancavel_e_transitorio()
    {
        var erro = new NpgsqlException("sem rota", new SocketException());

        TransientFailure.IsTransient(erro).ShouldBeTrue();
    }

    [Fact]
    public void Falha_embrulhada_pelo_ef_continua_sendo_reconhecida()
    {
        // O EF embrulha a falha do driver, e o retry do EF embrulha de novo. A
        // causa que interessa costuma estar duas ou três camadas abaixo.
        var interna = new NpgsqlException("caiu", new IOException());
        var embrulhada = new DbUpdateException("falha ao salvar", new InvalidOperationException("x", interna));

        TransientFailure.IsTransient(embrulhada).ShouldBeTrue();
    }

    [Fact]
    public void Violacao_de_constraint_nao_e_transitoria()
    {
        // O ponto mais importante da definição. Violação de unicidade é
        // determinística: repetir devolve o mesmo erro. Tratá-la como transitória
        // faria o sistema responder 503 — "repita" — a algo que nunca se
        // resolve, e esconderia atrás de uma queda de infraestrutura o que é
        // regra de negócio.
        var violacao = new PostgresException(
            "duplicate key value violates unique constraint",
            "ERROR",
            "ERROR",
            PostgresErrorCodes.UniqueViolation);

        TransientFailure.IsTransient(new DbUpdateException("falha ao salvar", violacao))
            .ShouldBeFalse();
    }

    [Fact]
    public void Defeito_de_codigo_nao_e_transitorio()
    {
        // Um 503 aqui mentiria sobre quem precisa agir: é defeito nosso, e
        // precisa continuar sendo um 500 visível no painel.
        TransientFailure.IsTransient(new InvalidOperationException("bug")).ShouldBeFalse();
        TransientFailure.IsTransient(new ArgumentNullException("x")).ShouldBeFalse();
    }

    [Fact]
    public void Excecao_nula_nao_e_transitoria()
    {
        TransientFailure.IsTransient(null).ShouldBeFalse();
    }

    // -----------------------------------------------------------------------
    // Orçamento de tempo do retry
    // -----------------------------------------------------------------------

    private static IExecutionStrategy CriarEstrategia(
        int maxRetries,
        TimeSpan maxDelay,
        TimeSpan budget)
    {
        var opcoes = new LedgerResilienceOptions
        {
            MaxRetries = maxRetries,
            MaxRetryDelay = maxDelay,
            RetryBudget = budget,
        };

        var builder = new DbContextOptionsBuilder<LedgerDbContext>();
        LedgerDbContextOptions.Apply(builder, LedgerApiFactory.UnreachableConnectionString, opcoes);

        // O contexto só fornece as dependências do EF. Nenhuma conexão é aberta:
        // o delegado executado abaixo não toca o banco.
        var contexto = new LedgerDbContext(builder.Options);
        return contexto.Database.CreateExecutionStrategy();
    }

    private static NpgsqlException FalhaTransitoria() =>
        new("conexão perdida", new IOException("reset"));

    [Fact]
    public async Task Falha_rapida_usa_todas_as_tentativas()
    {
        // O caso para o qual o retry existe: conexão que o pool descartou, rede
        // que piscou. A falha é instantânea, sobra orçamento, e a repetição
        // acontece.
        var estrategia = CriarEstrategia(
            maxRetries: 2, maxDelay: TimeSpan.FromMilliseconds(20), budget: TimeSpan.FromSeconds(5));
        var tentativas = 0;

        await Should.ThrowAsync<RetryLimitExceededException>(
            () => estrategia.ExecuteAsync(
                (CancellationToken _) =>
                {
                    tentativas++;
                    throw FalhaTransitoria();
#pragma warning disable CS0162
                    return Task.FromResult(0);
#pragma warning restore CS0162
                },
                TestContext.Current.CancellationToken));

        tentativas.ShouldBe(3, "a primeira tentativa mais as duas repetições");
    }

    [Fact]
    public async Task Falha_lenta_consome_o_orcamento_e_nao_e_repetida()
    {
        // O defeito que este orçamento corrige, medido com o banco parado: cada
        // tentativa levava cerca de dois segundos para falhar, e três faziam
        // toda requisição esperar sete. Retry deveria ajudar, e só adiava o 503.
        var estrategia = CriarEstrategia(
            maxRetries: 2, maxDelay: TimeSpan.FromMilliseconds(20), budget: TimeSpan.FromMilliseconds(300));
        var tentativas = 0;

        await Should.ThrowAsync<RetryLimitExceededException>(
            () => estrategia.ExecuteAsync(
                async (CancellationToken ct) =>
                {
                    tentativas++;
                    await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
                    throw FalhaTransitoria();
#pragma warning disable CS0162
                    return 0;
#pragma warning restore CS0162
                },
                TestContext.Current.CancellationToken));

        tentativas.ShouldBe(1, "a primeira falha já estourou o orçamento");
    }

    [Fact]
    public async Task Falha_nao_transitoria_sobe_na_hora_sem_embrulho()
    {
        // Defeito de código ou regra de negócio: repetir devolveria o mesmo erro.
        // E a exceção original precisa chegar intacta, para o tratamento padrão
        // responder 500.
        var estrategia = CriarEstrategia(
            maxRetries: 2, maxDelay: TimeSpan.FromMilliseconds(20), budget: TimeSpan.FromSeconds(5));
        var tentativas = 0;

        var erro = await Should.ThrowAsync<InvalidOperationException>(
            () => estrategia.ExecuteAsync(
                (CancellationToken _) =>
                {
                    tentativas++;
                    throw new InvalidOperationException("defeito de código");
#pragma warning disable CS0162
                    return Task.FromResult(0);
#pragma warning restore CS0162
                },
                TestContext.Current.CancellationToken));

        tentativas.ShouldBe(1);
        erro.Message.ShouldBe("defeito de código");
    }

    [Fact]
    public async Task Sucesso_na_segunda_tentativa_devolve_o_resultado()
    {
        var estrategia = CriarEstrategia(
            maxRetries: 2, maxDelay: TimeSpan.FromMilliseconds(20), budget: TimeSpan.FromSeconds(5));
        var tentativas = 0;

        var resultado = await estrategia.ExecuteAsync(
            (CancellationToken _) =>
            {
                tentativas++;

                return tentativas == 1
                    ? throw FalhaTransitoria()
                    : Task.FromResult(42);
            },
            TestContext.Current.CancellationToken);

        resultado.ShouldBe(42);
        tentativas.ShouldBe(2);
    }
}
