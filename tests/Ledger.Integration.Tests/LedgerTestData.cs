using Ledger.Application.Movements;
using Ledger.Domain;
using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ledger.Integration.Tests;

/// <summary>
/// Monta dados de teste no banco.
/// </summary>
/// <remarks>
/// Os testes compartilham um banco e se isolam usando contas e chaves próprias,
/// em lugar de um banco por teste. Subir container custa segundos; gerar um
/// identificador custa nada.
/// </remarks>
internal static class LedgerTestData
{
    public static readonly Currency Brl = Currency.Brl;

    /// <summary>Cria e persiste uma conta ativa.</summary>
    public static async Task<Account> CriarContaAsync(
        PostgresFixture fixture,
        AccountStatus situacao = AccountStatus.Active)
    {
        // O helper ignora o teste quando não há banco, em vez de deixar quem o
        // chama lembrar de fazê-lo antes. Cada teste que esqueceu transformava a
        // ausência de Docker numa falha — e a garantia de que "dotnet test" não
        // falha por ambiente é requisito do README, não conveniência.
        //
        // Foi descoberto em oito testes de uma só vez: todos chamavam este helper
        // antes de SkipIfUnavailable().
        ArgumentNullException.ThrowIfNull(fixture);
        fixture.SkipIfUnavailable();

        var conta = situacao == AccountStatus.Active
            ? Account.Open(AccountId.New(), CustomerId.New(), Brl)
            : Account.Rehydrate(AccountId.New(), CustomerId.New(), Brl, situacao);

        await using var contexto = fixture.CreateContext();
        contexto.Accounts.Add(PersistenceMapper.ToRecord(conta));
        await contexto.SaveChangesAsync();

        return conta;
    }

    /// <summary>Cria uma movimentação de domínio, sem persistir.</summary>
    public static Movement NovaMovimentacao(
        Account conta,
        decimal valor,
        string chave,
        MovementType tipo = MovementType.Credit,
        DateTimeOffset? ocorridaEm = null) =>
        conta.Register(
            tipo,
            Money.Of(valor, conta.Currency),
            ocorridaEm ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            IdempotencyKey.Of(chave),
            correlationId: $"corr-{Guid.CreateVersion7():N}");

    /// <summary>
    /// Grava a movimentação e o registro de idempotência na mesma transação.
    /// </summary>
    /// <remarks>
    /// Um único <c>SaveChangesAsync</c> é uma transação. É assim que o fluxo real
    /// funciona, e é a propriedade que os testes verificam: ou as duas linhas
    /// entram, ou nenhuma entra. Se fossem transações separadas, haveria uma
    /// janela em que a movimentação existe sem a chave — e o retry duplicaria.
    /// </remarks>
    public static async Task RegistrarAsync(LedgerDbContext contexto, Movement movimentacao)
    {
        ArgumentNullException.ThrowIfNull(contexto);
        ArgumentNullException.ThrowIfNull(movimentacao);

        contexto.Movements.Add(PersistenceMapper.ToRecord(movimentacao));
        contexto.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Key = movimentacao.IdempotencyKey.Value,
            AccountId = movimentacao.AccountId.Value,
            MovementId = movimentacao.Id.Value,
            RequestFingerprint = RequestFingerprint.Of(movimentacao),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        });

        await contexto.SaveChangesAsync();
    }

    /// <summary>Lê escalares de uma consulta no catálogo do PostgreSQL.</summary>
    public static async Task<List<string>> ConsultarCatalogoAsync(
        LedgerDbContext contexto,
        string sql)
    {
        ArgumentNullException.ThrowIfNull(contexto);

        var conexao = contexto.Database.GetDbConnection();

        if (conexao.State != System.Data.ConnectionState.Open)
        {
            await conexao.OpenAsync();
        }

        await using var comando = conexao.CreateCommand();
        comando.CommandText = sql;

        var resultado = new List<string>();
        await using var leitor = await comando.ExecuteReaderAsync();

        while (await leitor.ReadAsync())
        {
            resultado.Add(leitor.GetString(0));
        }

        return resultado;
    }
}
