using System.Diagnostics;
using Ledger.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Ledger.Infrastructure.Resilience;

/// <summary>Resultado de uma sondagem do banco.</summary>
/// <param name="IsReachable">O banco aceitou conexão e respondeu a uma consulta.</param>
/// <param name="Elapsed">Quanto a sondagem levou.</param>
/// <remarks>
/// Deliberadamente sem a exceção. O corpo de uma resposta de saúde é visto por
/// quem não deveria ver o endereço do banco nem o texto de um erro de driver, e
/// o motivo detalhado já vai para o log.
/// </remarks>
public sealed record DatabaseProbeResult(bool IsReachable, TimeSpan Elapsed);

/// <summary>
/// Pergunta ao banco se ele está respondendo, o mais rápido possível.
/// </summary>
/// <remarks>
/// <para>
/// <b>Não usa o EF Core, e isso é proposital.</b> O contexto tem retry
/// configurado: uma sondagem por ele esperaria as tentativas e os intervalos
/// antes de admitir que o banco está fora. Readiness precisa responder dentro do
/// tempo que o orquestrador espera, e a resposta "não" tem que ser tão rápida
/// quanto a "sim".
/// </para>
/// <para>
/// A pergunta é uma consulta de verdade, não só a abertura da conexão. Um banco
/// que aceita a conexão e não executa nada — em recuperação, com disco cheio —
/// é um banco que não pode receber tráfego.
/// </para>
/// </remarks>
public sealed class LedgerDatabaseProbe(IConfiguration configuration)
{
    /// <summary>Tempo máximo que uma sondagem pode levar.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>Sonda o banco.</summary>
    public async Task<DatabaseProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        var relogio = Stopwatch.StartNew();

        try
        {
            var opcoes = LedgerResilienceOptions.From(configuration);

            // Conexão mais curta que a da aplicação: aqui a pergunta é "agora",
            // não "daqui a alguns segundos".
            var limitada = opcoes with { ConnectTimeout = Timeout };
            var conexao = LedgerDbContextOptions.ComTimeouts(
                LedgerConnectionString.Resolve(configuration),
                limitada);

            await using var banco = new NpgsqlConnection(conexao);

            using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limite.CancelAfter(Timeout);

            await banco.OpenAsync(limite.Token).ConfigureAwait(false);

            await using var comando = banco.CreateCommand();
            comando.CommandText = "SELECT 1";
            comando.CommandTimeout = (int)Timeout.TotalSeconds;
            await comando.ExecuteScalarAsync(limite.Token).ConfigureAwait(false);

            return new DatabaseProbeResult(true, relogio.Elapsed);
        }
        catch (Exception erro) when (erro is not OperationCanceledException
                                     || !cancellationToken.IsCancellationRequested)
        {
            // Qualquer falha significa "não está respondendo". O motivo é
            // problema do log, não da resposta.
            return new DatabaseProbeResult(false, relogio.Elapsed);
        }
    }
}
