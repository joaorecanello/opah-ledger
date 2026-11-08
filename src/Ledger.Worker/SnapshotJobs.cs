using Ledger.Application.Balances;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ledger.Worker;

/// <summary>Cadência dos jobs de snapshot.</summary>
public sealed record SnapshotJobOptions
{
    /// <summary>Intervalo entre passadas de consolidação.</summary>
    /// <remarks>
    /// Curto de propósito. A consolidação não é um fechamento contábil diário:
    /// ela avança o cache até o último dia encerrado e refaz o que movimentação
    /// retroativa invalidou. Rodar de hora em hora mantém a janela de degradação
    /// pequena — e uma passada sem trabalho a fazer custa uma consulta por
    /// conta.
    /// </remarks>
    public TimeSpan ConsolidationInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Intervalo entre passadas de reconciliação.</summary>
    /// <remarks>
    /// Mais espaçado porque é mais caro: recalcula cada fechamento do razão.
    /// Em produção rodaria em janela de baixo uso.
    /// </remarks>
    public TimeSpan ReconciliationInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Espera antes da primeira passada, após o processo subir.</summary>
    /// <remarks>
    /// Evita que N instâncias subindo juntas após um deploy disparem a mesma
    /// varredura no mesmo instante.
    /// </remarks>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Mantém o cache de saldo de fechamento em dia.
/// </summary>
/// <remarks>
/// <para>
/// Este serviço <b>não está no caminho crítico de nada</b>. Se ele estiver
/// parado, a escrita funciona normalmente e a consulta de saldo recua para o
/// fechamento mais antigo que ainda vale — ou para o cálculo direto, se não
/// houver nenhum. Mais lenta, igualmente correta.
/// </para>
/// <para>
/// É essa propriedade que justifica rodar isto num processo separado e sem
/// garantia de execução: o que ele produz é cache descartável.
/// </para>
/// </remarks>
public sealed class SnapshotConsolidationService(
    IServiceScopeFactory scopeFactory,
    SnapshotJobOptions options,
    ILogger<SnapshotConsolidationService> logger) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(options.StartupDelay, stoppingToken).ConfigureAwait(false);

        using var cadencia = new PeriodicTimer(options.ConsolidationInterval);

        do
        {
            try
            {
                using var escopo = scopeFactory.CreateScope();
                var consolidacao = escopo.ServiceProvider
                    .GetRequiredService<ConsolidateSnapshotsHandler>();

                var relatorio = await consolidacao.HandleAsync(stoppingToken)
                    .ConfigureAwait(false);

                if (relatorio.SnapshotsWritten > 0)
                {
                    WorkerLog.ConsolidacaoGravou(
                        logger,
                        relatorio.SnapshotsWritten,
                        relatorio.AccountsProcessed,
                        relatorio.AccountsVisited);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception erro)
            {
                // Uma passada que falha não derruba o serviço: o que ela produz é
                // cache, e a próxima passada refaz o trabalho desde onde parou.
                // Derrubar o processo transformaria uma degradação de latência
                // numa indisponibilidade.
                WorkerLog.ConsolidacaoFalhou(logger, erro);
            }
        }
        while (await cadencia.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

/// <summary>
/// Confere cada fechamento contra o razão e marca os divergentes para recálculo.
/// </summary>
/// <remarks>
/// <para>
/// É o detector de o dinheiro ter divergido. Todas as outras medições dizem se o
/// sistema está rápido ou se está devolvendo erro; nenhuma delas pega o modo de
/// falha que importa num razão contábil — responder <c>200 OK</c>, em cinco
/// milissegundos, com o número errado.
/// </para>
/// <para>
/// Divergência <b>não tem limiar tolerável</b>. Qualquer valor diferente de zero
/// é incidente e, num banco, evento de conformidade.
/// </para>
/// </remarks>
public sealed class SnapshotReconciliationService(
    IServiceScopeFactory scopeFactory,
    SnapshotJobOptions options,
    ILogger<SnapshotReconciliationService> logger) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Começa depois da consolidação para não reconciliar um cache que a
        // primeira passada ainda está construindo.
        await Task.Delay(options.StartupDelay + TimeSpan.FromSeconds(30), stoppingToken)
            .ConfigureAwait(false);

        using var cadencia = new PeriodicTimer(options.ReconciliationInterval);

        do
        {
            try
            {
                using var escopo = scopeFactory.CreateScope();
                var reconciliacao = escopo.ServiceProvider
                    .GetRequiredService<ReconcileSnapshotsHandler>();

                var relatorio = await reconciliacao.HandleAsync(stoppingToken)
                    .ConfigureAwait(false);

                if (relatorio.IsClean)
                {
                    WorkerLog.ReconciliacaoLimpa(logger, relatorio.SnapshotsChecked);
                    continue;
                }

                var primeira = relatorio.Divergences[0];

                WorkerLog.ReconciliacaoDivergente(
                    logger,
                    relatorio.Divergences.Count,
                    relatorio.SnapshotsChecked,
                    relatorio.MarkedStale,
                    primeira.AccountId,
                    primeira.AsOfDate,
                    primeira.StoredBalance,
                    primeira.RecomputedBalance);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception erro)
            {
                WorkerLog.ReconciliacaoFalhou(logger, erro);
            }
        }
        while (await cadencia.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
