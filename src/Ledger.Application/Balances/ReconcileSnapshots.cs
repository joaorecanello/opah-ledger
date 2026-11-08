using Ledger.Application.Abstractions;
using Ledger.Domain;

namespace Ledger.Application.Balances;

/// <summary>Um fechamento que não corresponde ao razão.</summary>
/// <param name="AccountId">Conta.</param>
/// <param name="AsOfDate">Dia do fechamento.</param>
/// <param name="StoredBalance">Saldo gravado no snapshot.</param>
/// <param name="RecomputedBalance">Saldo recalculado do razão.</param>
/// <param name="StoredCount">Contagem gravada.</param>
/// <param name="RecomputedCount">Contagem recalculada.</param>
/// <remarks>
/// A contagem está aqui junto do valor porque ela <b>localiza</b> o problema.
/// Divergência de valor com contagem igual aponta para um lançamento com valor
/// errado; divergência nas duas aponta para lançamento faltando ou sobrando. É a
/// diferença entre saber que algo está errado e saber onde olhar.
/// </remarks>
public sealed record SnapshotDivergence(
    Guid AccountId,
    DateOnly AsOfDate,
    decimal StoredBalance,
    decimal RecomputedBalance,
    long StoredCount,
    long RecomputedCount);

/// <summary>O que uma passada de reconciliação encontrou.</summary>
/// <param name="SnapshotsChecked">Fechamentos verificados.</param>
/// <param name="Divergences">Fechamentos que não correspondem ao razão.</param>
/// <param name="MarkedStale">Quantos foram marcados para recálculo.</param>
public sealed record ReconciliationReport(
    int SnapshotsChecked,
    IReadOnlyList<SnapshotDivergence> Divergences,
    int MarkedStale)
{
    /// <summary>Não houve divergência alguma.</summary>
    /// <remarks>
    /// O único valor aceitável. Não existe limiar tolerável para divergência de
    /// saldo — qualquer valor diferente de zero é incidente e, num banco, evento
    /// de conformidade.
    /// </remarks>
    public bool IsClean => Divergences.Count == 0;
}

/// <summary>Política da reconciliação.</summary>
public sealed record ReconcileSnapshotsOptions
{
    /// <summary>
    /// Marcar como obsoleto todo fechamento divergente, para que a consolidação
    /// o recalcule.
    /// </summary>
    /// <remarks>
    /// Ligado por padrão. O sistema se corrige com a maquinaria que já existe
    /// para movimentação retroativa, em lugar de um caminho de reparo separado
    /// que seria exercitado só em incidente — e por isso mesmo não confiável.
    /// <para>
    /// Desligar serve à investigação: às vezes é preciso olhar o estado
    /// divergente antes de ele ser substituído.
    /// </para>
    /// </remarks>
    public bool MarkDivergentAsStale { get; init; } = true;
}

/// <summary>
/// Confere cada fechamento contra o razão.
/// </summary>
/// <remarks>
/// <para>
/// Otimização sem verificação automática, em sistema financeiro, é passivo e não
/// ativo. O snapshot é cache: ele pode divergir por defeito de código, por
/// alteração manual no banco, por uma migração mal aplicada. Sem esta rotina, a
/// divergência apareceria quando um cliente contestasse o extrato.
/// </para>
/// <para>
/// E ela é possível justamente porque o razão é append-only: existe uma fonte da
/// verdade imutável contra a qual comparar. Num sistema com saldo mutável não
/// haveria o que reconciliar — o número guardado seria a única versão da
/// história.
/// </para>
/// </remarks>
public sealed class ReconcileSnapshotsHandler(
    ILedgerRepository repository,
    ISnapshotStore snapshots,
    ReconcileSnapshotsOptions? options = null)
{
    private readonly ReconcileSnapshotsOptions _options =
        options ?? new ReconcileSnapshotsOptions();

    /// <summary>Reconcilia todas as contas.</summary>
    public async Task<ReconciliationReport> HandleAsync(
        CancellationToken cancellationToken = default)
    {
        var contas = await repository.ListAccountIdsAsync(cancellationToken).ConfigureAwait(false);

        var verificados = 0;
        var marcados = 0;
        var divergencias = new List<SnapshotDivergence>();

        foreach (var conta in contas)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var resultado = await ReconciliarContaAsync(conta, cancellationToken)
                .ConfigureAwait(false);

            verificados += resultado.SnapshotsChecked;
            marcados += resultado.MarkedStale;
            divergencias.AddRange(resultado.Divergences);
        }

        return new ReconciliationReport(verificados, divergencias, marcados);
    }

    /// <summary>Reconcilia uma conta.</summary>
    public async Task<ReconciliationReport> ReconciliarContaAsync(
        AccountId accountId,
        CancellationToken cancellationToken = default)
    {
        var existentes = await snapshots.ListAsync(accountId, cancellationToken)
            .ConfigureAwait(false);

        var divergencias = new List<SnapshotDivergence>();
        var verificados = 0;

        foreach (var snapshot in existentes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Obsoleto já está marcado para recálculo; conferir seria acusar uma
            // divergência que o sistema sabe que existe.
            if (snapshot.IsStale)
            {
                continue;
            }

            verificados++;

            var recalculado = await repository.SumRangeAsync(
                accountId,
                null,
                LedgerTimePolicy.EndOfDay(snapshot.AsOfDate),
                cancellationToken).ConfigureAwait(false);

            if (snapshot.ClosingBalance.Amount == recalculado.SignedAmount
                && snapshot.MovementCount == recalculado.MovementCount)
            {
                continue;
            }

            divergencias.Add(new SnapshotDivergence(
                accountId.Value,
                snapshot.AsOfDate,
                snapshot.ClosingBalance.Amount,
                recalculado.SignedAmount,
                snapshot.MovementCount,
                recalculado.MovementCount));
        }

        var marcados = 0;

        if (_options.MarkDivergentAsStale && divergencias.Count > 0)
        {
            // Marca a partir do dia divergente mais antigo, não apenas os dias
            // divergentes: um erro num dia contamina todos os posteriores,
            // porque o fechamento é acumulado.
            marcados = await snapshots.MarkStaleFromAsync(
                accountId,
                divergencias.Min(d => d.AsOfDate),
                cancellationToken).ConfigureAwait(false);
        }

        return new ReconciliationReport(verificados, divergencias, marcados);
    }
}
