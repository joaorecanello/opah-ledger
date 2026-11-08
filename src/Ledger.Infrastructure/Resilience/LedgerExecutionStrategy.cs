using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Ledger.Infrastructure.Resilience;

/// <summary>
/// Repete operações de banco que falharam por motivo transitório, dentro de um
/// <b>orçamento de tempo</b>.
/// </summary>
/// <remarks>
/// <para>
/// O retry embutido do EF Core limita o <i>número</i> de tentativas, não o
/// <i>tempo</i>. Isso é um defeito quando a falha em si é lenta: foi medido com o
/// banco parado, onde cada tentativa levava cerca de dois segundos para falhar,
/// e três tentativas faziam toda requisição esperar sete. Retry deveria ajudar, e
/// aqui só adiava o <c>503</c> e mantinha conexões e threads presas por mais
/// tempo.
/// </para>
/// <para>
/// A regra desta estratégia: <b>não começa uma tentativa que já nasce estourada</b>.
/// Se a falha foi rápida — conexão que o pool descartou, reinício de banco, rede
/// que piscou — sobra orçamento e a repetição acontece; é o caso para o qual
/// retry existe. Se a falha foi lenta, o orçamento acaba junto e a falha sobe na
/// hora para o circuito e para o <c>503</c>.
/// </para>
/// <para>
/// Usa <see cref="TransientFailure"/> para decidir o que repetir, e não a lista
/// do provider. Assim retry, circuito e mapeamento para <c>503</c> compartilham
/// uma só definição de falha transitória.
/// </para>
/// </remarks>
public sealed class LedgerExecutionStrategy : ExecutionStrategy
{
    private readonly TimeSpan _budget;
    private long _startedAt;

    /// <summary>Cria a estratégia.</summary>
    /// <param name="dependencies">Dependências fornecidas pelo EF Core.</param>
    /// <param name="maxRetryCount">Número máximo de repetições.</param>
    /// <param name="maxRetryDelay">Espera máxima entre tentativas.</param>
    /// <param name="budget">Tempo total após o qual nenhuma nova tentativa começa.</param>
    public LedgerExecutionStrategy(
        ExecutionStrategyDependencies dependencies,
        int maxRetryCount,
        TimeSpan maxRetryDelay,
        TimeSpan budget)
        : base(dependencies, maxRetryCount, maxRetryDelay)
    {
        _budget = budget;
    }

    /// <inheritdoc/>
    protected override void OnFirstExecution()
    {
        base.OnFirstExecution();
        _startedAt = Stopwatch.GetTimestamp();
    }

    /// <inheritdoc/>
    protected override bool ShouldRetryOn(Exception exception) =>
        TransientFailure.IsTransient(exception);

    /// <inheritdoc/>
    protected override TimeSpan? GetNextDelay(Exception lastException)
    {
        var espera = base.GetNextDelay(lastException);

        if (espera is null)
        {
            return null;
        }

        var decorrido = Stopwatch.GetElapsedTime(_startedAt);

        // Considera também a espera que viria: uma tentativa que só começaria
        // depois de o orçamento acabar já nasce estourada.
        return decorrido + espera.Value > _budget ? null : espera;
    }
}
