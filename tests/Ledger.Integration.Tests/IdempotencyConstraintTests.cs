using Ledger.Application.Movements;
using Ledger.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ledger.Integration.Tests;

/// <summary>
/// Verifica, contra o PostgreSQL real, que a chave de idempotência impede
/// duplicata.
/// </summary>
/// <remarks>
/// <para>
/// Este é o cenário de maior risco financeiro do sistema. Duplicar dinheiro é
/// pior que indisponibilidade, porque é invisível até a conciliação.
/// </para>
/// <para>
/// E é a razão de a suíte usar PostgreSQL real: a garantia não vive no código de
/// aplicação, vive no constraint do banco — o único lugar que não mente sob
/// concorrência. Um provedor em memória deixaria estes testes passarem sem
/// verificar nada.
/// </para>
/// <para>
/// Aqui se verifica o <b>mecanismo</b>, no nível do esquema. A semântica
/// completa da API — <c>200</c> no replay, <c>409</c> em conteúdo divergente —
/// é do caso de uso de registro, e tem testes próprios.
/// </para>
/// </remarks>
[Collection(PostgresDatabase.Name)]
public sealed class IdempotencyConstraintTests(PostgresFixture fixture)
{
    private static string NovaChave() => $"chave-{Guid.CreateVersion7():N}";

    [Fact]
    public async Task Mesma_chave_duas_vezes_grava_uma_movimentacao_so()
    {
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var chave = NovaChave();

        await using (var primeira = fixture.CreateContext())
        {
            await LedgerTestData.RegistrarAsync(
                primeira, LedgerTestData.NovaMovimentacao(conta, 100m, chave));
        }

        await using var segunda = fixture.CreateContext();
        var erro = await Should.ThrowAsync<DbUpdateException>(
            () => LedgerTestData.RegistrarAsync(
                segunda, LedgerTestData.NovaMovimentacao(conta, 100m, chave)));

        // 23505 = unique_violation. A detecção acontece na violação do
        // constraint, e não numa consulta prévia do tipo "esta chave já
        // existe?" — a consulta prévia deixaria duas requisições simultâneas
        // passarem, porque as duas veriam "não existe".
        erro.InnerException.ShouldBeOfType<PostgresException>()
            .SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);

        await using var verificacao = fixture.CreateContext();
        var quantidade = await verificacao.Movements
            .CountAsync(m => m.IdempotencyKey == chave, TestContext.Current.CancellationToken);

        quantidade.ShouldBe(1);
    }

    [Fact]
    public async Task Tentativa_duplicada_nao_deixa_movimentacao_orfa()
    {
        // A movimentação e o registro de idempotência entram na mesma transação.
        // Se fossem separados, a segunda tentativa gravaria a movimentação e só
        // depois falharia na chave — deixando uma duplicata no razão com a
        // aparência de ter sido recusada.
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var chave = NovaChave();

        await using (var primeira = fixture.CreateContext())
        {
            await LedgerTestData.RegistrarAsync(
                primeira, LedgerTestData.NovaMovimentacao(conta, 250m, chave));
        }

        await using (var segunda = fixture.CreateContext())
        {
            await Should.ThrowAsync<DbUpdateException>(
                () => LedgerTestData.RegistrarAsync(
                    segunda, LedgerTestData.NovaMovimentacao(conta, 250m, chave)));
        }

        await using var verificacao = fixture.CreateContext();
        var movimentacoesDaConta = await verificacao.Movements
            .CountAsync(m => m.AccountId == conta.Id.Value, TestContext.Current.CancellationToken);

        movimentacoesDaConta.ShouldBe(1, "a transação da segunda tentativa deve ter sido inteira desfeita");
    }

    [Fact]
    public async Task Vinte_tentativas_simultaneas_com_a_mesma_chave_registram_uma_so()
    {
        // O caso que a consulta prévia não resolveria: todas as tentativas
        // começam juntas, nenhuma vê a outra, e só o constraint decide.
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var chave = NovaChave();
        const int Tentativas = 20;

        var resultados = await Task.WhenAll(
            Enumerable.Range(0, Tentativas).Select(async _ =>
            {
                try
                {
                    await using var contexto = fixture.CreateContext();
                    await LedgerTestData.RegistrarAsync(
                        contexto, LedgerTestData.NovaMovimentacao(conta, 75.25m, chave));
                    return true;
                }
                catch (DbUpdateException)
                {
                    return false;
                }
            }));

        resultados.Count(sucesso => sucesso).ShouldBe(1, "exatamente uma tentativa deve vencer");

        await using var verificacao = fixture.CreateContext();
        var movimentacoes = await verificacao.Movements
            .CountAsync(m => m.IdempotencyKey == chave, TestContext.Current.CancellationToken);

        movimentacoes.ShouldBe(1);
    }

    [Fact]
    public async Task Chaves_diferentes_com_mesmo_valor_sao_duas_movimentacoes()
    {
        // Duas transferências idênticas no mesmo minuto são plenamente válidas,
        // e só a chave distingue uma da outra. É a razão de a deduplicação não
        // poder ser por janela de tempo mais conteúdo: nenhum ajuste da janela
        // separa repetição de operação legítima repetida.
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var instante = DateTimeOffset.UtcNow.AddMinutes(-5);

        await using (var contexto = fixture.CreateContext())
        {
            await LedgerTestData.RegistrarAsync(
                contexto,
                LedgerTestData.NovaMovimentacao(conta, 50m, NovaChave(), ocorridaEm: instante));
        }

        await using (var contexto = fixture.CreateContext())
        {
            await LedgerTestData.RegistrarAsync(
                contexto,
                LedgerTestData.NovaMovimentacao(conta, 50m, NovaChave(), ocorridaEm: instante));
        }

        await using var verificacao = fixture.CreateContext();
        var quantidade = await verificacao.Movements
            .CountAsync(m => m.AccountId == conta.Id.Value, TestContext.Current.CancellationToken);

        quantidade.ShouldBe(2);
    }

    [Fact]
    public async Task Impressao_do_conteudo_distingue_repeticao_de_conteudo_divergente()
    {
        // É o que permite responder 200 no replay e 409 em conteúdo diferente,
        // em lugar de adivinhar a intenção do chamador.
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var chave = NovaChave();
        var instante = DateTimeOffset.UtcNow.AddMinutes(-3);

        var original = LedgerTestData.NovaMovimentacao(conta, 100m, chave, ocorridaEm: instante);

        await using (var contexto = fixture.CreateContext())
        {
            await LedgerTestData.RegistrarAsync(contexto, original);
        }

        await using var verificacao = fixture.CreateContext();
        var gravado = await verificacao.IdempotencyRecords.SingleAsync(r => r.Key == chave, TestContext.Current.CancellationToken);

        // Mesmo conteúdo, tentativa nova: impressão igual, logo é replay.
        var repeticao = LedgerTestData.NovaMovimentacao(conta, 100m, chave, ocorridaEm: instante);
        RequestFingerprint.Of(repeticao)
            .ShouldBe(gravado.RequestFingerprint);

        // Valor diferente: impressão diferente, logo é erro do chamador.
        var divergente = LedgerTestData.NovaMovimentacao(conta, 150m, chave, ocorridaEm: instante);
        RequestFingerprint.Of(divergente)
            .ShouldNotBe(gravado.RequestFingerprint);
    }
}
