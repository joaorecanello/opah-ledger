using Ledger.Application.Abstractions;
using Ledger.Application.Movements;
using Ledger.Application.Security;
using Ledger.Domain;
using Ledger.Infrastructure;
using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Integration.Tests;

/// <summary>
/// O caso de uso de registro contra PostgreSQL real.
/// </summary>
/// <remarks>
/// A ramificação do caso de uso — registrou, replay, conflito, recusa — é
/// verificada exaustivamente nos testes de aplicação, com portas falsas.
/// <para>
/// O que está aqui é o que o falso <b>não</b> pode provar: que a garantia
/// funciona quando o árbitro é o unique constraint do PostgreSQL, sob
/// concorrência real, e que a transação da tentativa perdedora é inteira
/// desfeita.
/// </para>
/// </remarks>
[Collection(PostgresDatabase.Name)]
public sealed class RegisterMovementIntegrationTests(PostgresFixture fixture)
{
    private static string NovaChave() => $"chave-{Guid.CreateVersion7():N}";

    private static RegisterMovementHandler CriarHandler(LedgerDbContext contexto) =>
        new(new LedgerRepository(contexto), FixedCallerAccess.Unrestricted, new SystemClock());

    private static RegisterMovementCommand Comando(
        Account conta,
        string chave,
        decimal valor = 100m,
        MovementType tipo = MovementType.Credit) =>
        new(
            conta.Id.Value,
            tipo,
            valor,
            conta.Currency.Code,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            chave,
            CorrelationId: $"corr-{Guid.CreateVersion7():N}");

    [Fact]
    public async Task Registra_e_a_movimentacao_esta_no_banco()
    {
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var chave = NovaChave();

        await using var contexto = fixture.CreateContext();
        var resultado = await CriarHandler(contexto)
            .HandleAsync(Comando(conta, chave), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.Registered);

        await using var leitura = fixture.CreateContext();
        var gravada = await leitura.Movements.SingleAsync(
            m => m.Id == resultado.MovementId, TestContext.Current.CancellationToken);

        gravada.Amount.ShouldBe(100m);
        gravada.IdempotencyKey.ShouldBe(chave);
        gravada.PostedAt.ShouldBe(resultado.PostedAt!.Value);
    }

    [Fact]
    public async Task Replay_devolve_o_identificador_e_o_instante_originais()
    {
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var pedido = Comando(conta, NovaChave());

        RegisterMovementResult primeira;
        await using (var contexto = fixture.CreateContext())
        {
            primeira = await CriarHandler(contexto)
                .HandleAsync(pedido, TestContext.Current.CancellationToken);
        }

        // Contexto novo: a segunda tentativa não vê nada em cache, como uma
        // requisição de verdade.
        await using var outroContexto = fixture.CreateContext();
        var segunda = await CriarHandler(outroContexto)
            .HandleAsync(pedido, TestContext.Current.CancellationToken);

        segunda.Status.ShouldBe(RegisterMovementStatus.Replayed);
        segunda.MovementId.ShouldBe(primeira.MovementId);
        segunda.PostedAt.ShouldBe(primeira.PostedAt);

        await using var leitura = fixture.CreateContext();
        var quantidade = await leitura.Movements.CountAsync(
            m => m.AccountId == conta.Id.Value, TestContext.Current.CancellationToken);
        quantidade.ShouldBe(1);
    }

    [Fact]
    public async Task Conteudo_divergente_e_conflito_e_nada_e_gravado()
    {
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var chave = NovaChave();

        await using (var contexto = fixture.CreateContext())
        {
            await CriarHandler(contexto).HandleAsync(
                Comando(conta, chave, valor: 100m), TestContext.Current.CancellationToken);
        }

        await using var outroContexto = fixture.CreateContext();
        var resultado = await CriarHandler(outroContexto).HandleAsync(
            Comando(conta, chave, valor: 150m), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.ConflictingPayload);

        await using var leitura = fixture.CreateContext();
        var movimentacoes = await leitura.Movements
            .Where(m => m.AccountId == conta.Id.Value)
            .ToListAsync(TestContext.Current.CancellationToken);

        movimentacoes.Count.ShouldBe(1, "a transação da tentativa recusada deve ter sido desfeita");
        movimentacoes[0].Amount.ShouldBe(100m);
    }

    [Fact]
    public async Task Vinte_retries_simultaneos_identicos_registram_um_e_replicam_o_resto()
    {
        // Este é o teste que o falso em memória não substitui, e o cenário real:
        // o cliente perdeu a resposta e várias tentativas chegam juntas.
        //
        // O desfecho correto NÃO é 409. Todas as tentativas se referem ao mesmo
        // fato, logo uma registra e as outras recebem a resposta original —
        // mesmo identificador, mesmo instante. Recusar as perdedoras seria
        // punir o chamador por uma falha de rede.
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var chave = NovaChave();
        var pedido = Comando(conta, chave, valor: 77.77m);
        const int Tentativas = 20;

        var resultados = await Task.WhenAll(
            Enumerable.Range(0, Tentativas).Select(async _ =>
            {
                await using var contexto = fixture.CreateContext();
                return await CriarHandler(contexto)
                    .HandleAsync(pedido, TestContext.Current.CancellationToken);
            }));

        resultados.Count(r => r.Status == RegisterMovementStatus.Registered)
            .ShouldBe(1, "exatamente uma tentativa registra");
        resultados.Count(r => r.Status == RegisterMovementStatus.Replayed)
            .ShouldBe(Tentativas - 1, "as outras recebem a resposta original");
        resultados.ShouldNotContain(
            r => r.Status == RegisterMovementStatus.ConflictingPayload,
            "retry do mesmo fato nunca é conflito");

        // Todas devolvem o mesmo identificador: é um fato, não vinte.
        resultados.Select(r => r.MovementId).Distinct().Count().ShouldBe(1);

        await using var leitura = fixture.CreateContext();
        var quantidade = await leitura.Movements.CountAsync(
            m => m.IdempotencyKey == chave, TestContext.Current.CancellationToken);
        quantidade.ShouldBe(1);
    }

    [Fact]
    public async Task Conta_inexistente_nao_grava_nada()
    {
        fixture.SkipIfUnavailable();

        await using var contexto = fixture.CreateContext();
        var inexistente = Account.Open(AccountId.New(), CustomerId.New(), Currency.Brl);

        var resultado = await CriarHandler(contexto).HandleAsync(
            Comando(inexistente, NovaChave()), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.AccountNotFound);

        await using var leitura = fixture.CreateContext();
        var existe = await leitura.Movements.AnyAsync(
            m => m.AccountId == inexistente.Id.Value, TestContext.Current.CancellationToken);
        existe.ShouldBeFalse();
    }

    [Fact]
    public async Task Conta_bloqueada_aceita_registro()
    {
        // PR-13: bloqueio impede originar operação, não registrar fato que já
        // aconteceu.
        fixture.SkipIfUnavailable();
        var bloqueada = await LedgerTestData.CriarContaAsync(fixture, AccountStatus.Blocked);

        await using var contexto = fixture.CreateContext();
        var resultado = await CriarHandler(contexto).HandleAsync(
            Comando(bloqueada, NovaChave()), TestContext.Current.CancellationToken);

        resultado.Status.ShouldBe(RegisterMovementStatus.Registered);
    }

    [Fact]
    public async Task Instante_do_registro_de_idempotencia_e_o_mesmo_da_movimentacao()
    {
        // É o que permite responder ao replay com o instante original sem uma
        // segunda leitura. Se divergirem, o replay devolveria um carimbo que
        // não corresponde a nenhum lançamento.
        fixture.SkipIfUnavailable();
        var conta = await LedgerTestData.CriarContaAsync(fixture);
        var chave = NovaChave();

        await using var contexto = fixture.CreateContext();
        var resultado = await CriarHandler(contexto)
            .HandleAsync(Comando(conta, chave), TestContext.Current.CancellationToken);

        await using var leitura = fixture.CreateContext();
        var movimentacao = await leitura.Movements.SingleAsync(
            m => m.Id == resultado.MovementId, TestContext.Current.CancellationToken);
        var registro = await leitura.IdempotencyRecords.SingleAsync(
            r => r.Key == chave, TestContext.Current.CancellationToken);

        registro.CreatedAt.ShouldBe(movimentacao.PostedAt);
        registro.MovementId.ShouldBe(movimentacao.Id);
        registro.ExpiresAt.ShouldBeGreaterThan(registro.CreatedAt);
    }

    [Fact]
    public async Task Colisao_que_nao_e_de_idempotencia_nao_e_tratada_como_replay()
    {
        // O adaptador verifica o NOME do constraint, não só o código 23505.
        // Tratar uma violação diferente como "chave repetida" faria o sistema
        // responder 200 a um pedido que não foi registrado — a pior resposta
        // possível, porque o chamador acreditaria que o lançamento existe.
        fixture.SkipIfUnavailable();

        var conta = Account.Open(AccountId.New(), CustomerId.New(), Currency.Brl);

        await using (var primeiro = fixture.CreateContext())
        {
            primeiro.Accounts.Add(PersistenceMapper.ToRecord(conta));
            await primeiro.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Contexto novo de propósito: inserir a mesma conta no mesmo contexto
        // falharia no rastreador do EF, antes de chegar ao banco — e o que este
        // teste verifica é a reação do adaptador ao erro do PostgreSQL.
        await using var segundo = fixture.CreateContext();
        segundo.Accounts.Add(PersistenceMapper.ToRecord(conta));

        var erro = await Should.ThrowAsync<DbUpdateException>(
            () => segundo.SaveChangesAsync(TestContext.Current.CancellationToken));

        erro.InnerException.ShouldBeOfType<Npgsql.PostgresException>()
            .ConstraintName.ShouldBe("pk_accounts");
    }
}
