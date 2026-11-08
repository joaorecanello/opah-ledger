using Ledger.Application.Abstractions;
using Ledger.Application.Observability;
using Ledger.Domain;

namespace Ledger.Application.Balances;

/// <summary>O que uma passada de consolidação fez.</summary>
/// <param name="AccountsProcessed">Contas que tiveram ao menos um dia consolidado.</param>
/// <param name="AccountsVisited">Contas examinadas.</param>
/// <param name="SnapshotsWritten">Fechamentos gravados ou substituídos.</param>
/// <param name="MaxLagDays">
/// Quantos dias a conta mais atrasada ainda tem por consolidar depois desta
/// passada. Zero significa em dia; diferente de zero significa que o limite de
/// dias por passada foi atingido e a próxima continua.
/// </param>
public sealed record ConsolidationReport(
    int AccountsVisited,
    int AccountsProcessed,
    int SnapshotsWritten,
    int MaxLagDays = 0);

/// <summary>Política da consolidação.</summary>
public sealed record ConsolidateSnapshotsOptions
{
    /// <summary>
    /// Máximo de dias consolidados por conta em cada passada.
    /// </summary>
    /// <remarks>
    /// Limita o custo de uma passada que encontre uma conta com histórico longo
    /// e nenhum snapshot — o caso do primeiro backfill. A consolidação avança
    /// nas passadas seguintes, em lugar de uma passada única e longa que ocupa o
    /// banco e não dá sinal de progresso.
    /// </remarks>
    public int MaxDaysPerAccountPerRun { get; init; } = 400;
}

/// <summary>
/// Calcula e grava o saldo de fechamento dos dias já encerrados.
/// </summary>
/// <remarks>
/// <para>
/// <b>Somente dias encerrados.</b> O dia corrente nunca é consolidado: ele ainda
/// pode receber movimentação, e um fechamento parcial seria um número errado com
/// aparência de definitivo. Como o passado é imutável, o fechamento de um dia
/// encerrado só muda se chegar movimentação retroativa — e aí ele é marcado
/// obsoleto e recalculado aqui.
/// </para>
/// <para>
/// <b>O que isto grava é cache, não verdade.</b> A tabela pode ser truncada por
/// inteiro e esta rotina a reconstrói do razão. É essa propriedade que torna a
/// otimização aceitável em dados financeiros: na dúvida sobre um número, joga
/// fora e recalcula.
/// </para>
/// <para>
/// O cálculo parte de uma soma do histórico até o dia anterior ao primeiro dia a
/// consolidar, e daí acumula o delta de cada dia. Uma soma completa mais uma por
/// dia, em lugar de uma soma completa por dia — a diferença aparece no backfill,
/// onde a forma ingênua seria quadrática.
/// </para>
/// </remarks>
public sealed class ConsolidateSnapshotsHandler(
    ILedgerRepository repository,
    ISnapshotStore snapshots,
    IClock clock,
    ConsolidateSnapshotsOptions? options = null,
    LedgerMetrics? metrics = null)
{
    private readonly ConsolidateSnapshotsOptions _options =
        options ?? new ConsolidateSnapshotsOptions();

    /// <summary>Consolida todas as contas.</summary>
    public async Task<ConsolidationReport> HandleAsync(CancellationToken cancellationToken = default)
    {
        var contas = await repository.ListAccountIdsAsync(cancellationToken).ConfigureAwait(false);

        var processadas = 0;
        var gravados = 0;
        var piorAtraso = 0;

        foreach (var conta in contas)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (escritos, atraso) = await ConsolidarComAtrasoAsync(conta, cancellationToken)
                .ConfigureAwait(false);

            if (escritos > 0)
            {
                processadas++;
                gravados += escritos;
            }

            piorAtraso = Math.Max(piorAtraso, atraso);
        }

        metrics?.SnapshotsConsolidated(gravados);

        // Publicado mesmo quando é zero: um gauge que só aparece quando há atraso
        // não distingue "em dia" de "o worker parou de reportar".
        metrics?.SnapshotLag(TimeSpan.FromDays(piorAtraso));

        return new ConsolidationReport(contas.Count, processadas, gravados, piorAtraso);
    }

    /// <summary>Consolida uma conta. Devolve quantos fechamentos foram gravados.</summary>
    public async Task<int> ConsolidarContaAsync(
        AccountId accountId,
        CancellationToken cancellationToken = default)
    {
        var (escritos, _) = await ConsolidarComAtrasoAsync(accountId, cancellationToken)
            .ConfigureAwait(false);

        return escritos;
    }

    /// <summary>
    /// Consolida uma conta e informa quantos dias ainda faltam depois da passada.
    /// </summary>
    /// <remarks>
    /// O atraso volta como valor, e não como estado do handler, porque a mesma
    /// instância atende várias contas em sequência: um campo entre chamadas seria
    /// estado escondido, e este código é imutável e explícito no resto.
    /// </remarks>
    private async Task<(int Escritos, int AtrasoEmDias)> ConsolidarComAtrasoAsync(
        AccountId accountId,
        CancellationToken cancellationToken)
    {
        var ultimoDiaFechado = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(-1);

        var inicio = await DeterminarInicioAsync(accountId, cancellationToken)
            .ConfigureAwait(false);

        if (inicio is null || inicio > ultimoDiaFechado)
        {
            return (0, 0);
        }

        var fim = inicio.Value.AddDays(_options.MaxDaysPerAccountPerRun - 1);

        if (fim > ultimoDiaFechado)
        {
            fim = ultimoDiaFechado;
        }

        // O que sobra depois desta passada: zero quando a conta chega ao último dia
        // encerrado, positivo quando o limite de dias por passada a deteve.
        var atraso = ultimoDiaFechado.DayNumber - fim.DayNumber;

        // Abertura recalculada do razão, e não lida do snapshot anterior.
        //
        // Parece desperdício e não é: o snapshot anterior pode não existir —
        // há lacunas quando uma conta fica sem movimentação por dias — e pode
        // ele mesmo estar errado, que é justamente o caso que a reconciliação
        // conserta marcando-o como obsoleto. Partir do razão torna cada passada
        // independente do estado do cache.
        var fimDoDiaAnterior = LedgerTimePolicy.EndOfDay(inicio.Value.AddDays(-1));
        var abertura = await repository
            .SumRangeAsync(accountId, null, fimDoDiaAnterior, cancellationToken)
            .ConfigureAwait(false);

        var saldoAcumulado = abertura.SignedAmount;
        var contagemAcumulada = abertura.MovementCount;
        var moeda = await ResolverMoedaAsync(accountId, cancellationToken).ConfigureAwait(false);

        if (moeda is null)
        {
            return (0, 0);
        }

        var gravados = 0;

        for (var dia = inicio.Value; dia <= fim; dia = dia.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var doDia = await repository.SumRangeAsync(
                accountId,
                LedgerTimePolicy.StartOfDay(dia),
                LedgerTimePolicy.EndOfDay(dia),
                cancellationToken).ConfigureAwait(false);

            saldoAcumulado += doDia.SignedAmount;
            contagemAcumulada += doDia.MovementCount;

            await snapshots.SaveAsync(
                BalanceSnapshot.Create(
                    accountId,
                    dia,
                    Money.Of(saldoAcumulado, moeda.Value),
                    contagemAcumulada),
                cancellationToken).ConfigureAwait(false);

            gravados++;
        }

        return (gravados, atraso);
    }

    /// <summary>
    /// Primeiro dia que precisa ser consolidado, ou <c>null</c> se não há nada a
    /// fazer.
    /// </summary>
    /// <remarks>
    /// A ordem das três perguntas é a lógica inteira desta rotina:
    /// <list type="number">
    ///   <item>
    ///     há snapshot obsoleto? Então começa no <b>mais antigo</b> deles:
    ///     movimentação retroativa invalida aquele dia e todos os posteriores.
    ///   </item>
    ///   <item>
    ///     há snapshot? Então começa no dia seguinte ao último.
    ///   </item>
    ///   <item>
    ///     nenhum snapshot? Então começa na primeira movimentação da conta — e
    ///     se ela não tem movimentação, não há o que consolidar.
    ///   </item>
    /// </list>
    /// </remarks>
    private async Task<DateOnly?> DeterminarInicioAsync(
        AccountId accountId,
        CancellationToken cancellationToken)
    {
        var existentes = await snapshots.ListAsync(accountId, cancellationToken)
            .ConfigureAwait(false);

        var obsoletos = existentes.Where(s => s.IsStale).ToArray();

        if (obsoletos.Length > 0)
        {
            return obsoletos.Min(s => s.AsOfDate);
        }

        if (existentes.Count > 0)
        {
            return existentes.Max(s => s.AsOfDate).AddDays(1);
        }

        return await repository.FindFirstMovementDateAsync(accountId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Currency?> ResolverMoedaAsync(
        AccountId accountId,
        CancellationToken cancellationToken)
    {
        var conta = await repository.FindAccountAsync(accountId, cancellationToken)
            .ConfigureAwait(false);

        return conta?.Currency;
    }
}
