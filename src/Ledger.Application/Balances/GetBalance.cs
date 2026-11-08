using Ledger.Application.Abstractions;
using Ledger.Domain;

namespace Ledger.Application.Balances;

/// <summary>Consulta da posição consolidada de uma conta.</summary>
/// <param name="AccountId">Conta.</param>
/// <param name="AsOf">
/// Instante da consulta. Ausente significa "agora", tomado do relógio do
/// servidor.
/// </param>
public sealed record GetBalanceQuery(Guid AccountId, DateTimeOffset? AsOf = null);

/// <summary>Desfecho de uma consulta de saldo.</summary>
public enum GetBalanceStatus
{
    /// <summary>Valor não atribuído. Nunca deve ocorrer.</summary>
    Unspecified = 0,

    /// <summary>Saldo calculado. A API responde <c>200</c>.</summary>
    Found,

    /// <summary>A conta não existe. A API responde <c>404</c>.</summary>
    AccountNotFound,

    /// <summary>A consulta é inválida. A API responde <c>400</c>.</summary>
    Rejected,
}

/// <summary>De onde veio o número devolvido.</summary>
/// <remarks>
/// Faz parte da resposta de propósito. Quando alguém contestar um saldo, a
/// primeira pergunta é de onde ele veio — e em finanças a rastreabilidade do
/// número vale mais que a elegância do payload.
/// </remarks>
public enum BalanceSource
{
    /// <summary>Valor não atribuído. Nunca deve ocorrer.</summary>
    Unspecified = 0,

    /// <summary>
    /// Soma de todas as movimentações até o instante consultado.
    /// </summary>
    /// <remarks>
    /// Correto em qualquer volume, e o caminho usado quando não há snapshot
    /// aproveitável. É também o oráculo contra o qual o caminho otimizado é
    /// verificado.
    /// </remarks>
    DirectSum,

    /// <summary>
    /// Fechamento de um dia já encerrado, mais as movimentações posteriores.
    /// </summary>
    /// <remarks>
    /// O caminho rápido: um <i>seek</i> no snapshot mais um <i>range scan</i>
    /// curto, em lugar de somar o histórico inteiro.
    /// </remarks>
    SnapshotPlusDelta,
}

/// <summary>Resultado de uma consulta de saldo.</summary>
/// <param name="Status">Desfecho.</param>
/// <param name="AsOf">Instante efetivamente usado no corte.</param>
/// <param name="Balance">Saldo, quando calculado.</param>
/// <param name="Currency">Moeda da conta, quando conhecida.</param>
/// <param name="MovementCount">
/// Movimentações consideradas. No caminho de snapshot, inclui as que já estavam
/// contabilizadas no fechamento.
/// </param>
/// <param name="Source">Como o número foi obtido.</param>
/// <param name="SnapshotDate">
/// Dia do fechamento usado como base, quando houve um.
/// </param>
/// <param name="Stale">
/// Havia um snapshot mais próximo do corte, porém invalidado por movimentação
/// retroativa, e ele foi ignorado. O número é correto; a consulta custou mais
/// caro.
/// </param>
/// <param name="ErrorCode">Invariante violada, quando a consulta foi recusada.</param>
/// <param name="Detail">Explicação legível, quando houver.</param>
public sealed record GetBalanceResult(
    GetBalanceStatus Status,
    DateTimeOffset? AsOf = null,
    decimal? Balance = null,
    string? Currency = null,
    long? MovementCount = null,
    BalanceSource? Source = null,
    DateOnly? SnapshotDate = null,
    bool? Stale = null,
    DomainErrorCode? ErrorCode = null,
    string? Detail = null);

/// <summary>Política da consulta de saldo.</summary>
public sealed record GetBalanceOptions
{
    /// <summary>
    /// Quanto o instante consultado pode estar à frente do relógio do servidor.
    /// </summary>
    public TimeSpan MaxFutureSkew { get; init; } = LedgerTimePolicy.DefaultFutureSkew;

    /// <summary>
    /// Usar o fechamento de período quando houver um aproveitável.
    /// </summary>
    /// <remarks>
    /// Desligar força o cálculo direto. Serve ao teste de equivalência, que
    /// precisa dos dois caminhos lado a lado, e serve à operação: se houver
    /// suspeita sobre o snapshot, desligar devolve o caminho obviamente correto
    /// sem nenhum deploy de código.
    /// </remarks>
    public bool UseSnapshots { get; init; } = true;
}

/// <summary>
/// Responde a posição consolidada de uma conta num instante.
/// </summary>
/// <remarks>
/// <para>
/// O saldo não está guardado em lugar nenhum: ele é derivado dos fatos. Esta é a
/// dívida que o ledger append-only contrai de propósito, e este caso de uso é
/// quem a paga.
/// </para>
/// <para>
/// Há dois caminhos. Com um fechamento de dia aproveitável, a consulta parte
/// dele e soma apenas o delta. Sem, soma o histórico. <b>Os dois devolvem o
/// mesmo número</b> — há teste de equivalência que gera centenas de
/// movimentações em datas aleatórias e exige igualdade em vários instantes.
/// </para>
/// <para>
/// A soma, em qualquer um dos caminhos, acontece no banco. Carregar o histórico
/// para somar em memória seria a varredura que este desenho existe para evitar.
/// </para>
/// </remarks>
public sealed class GetBalanceHandler(
    ILedgerRepository repository,
    ISnapshotStore snapshots,
    IClock clock,
    GetBalanceOptions? options = null)
{
    private readonly GetBalanceOptions _options = options ?? new GetBalanceOptions();

    /// <summary>Executa a consulta.</summary>
    public async Task<GetBalanceResult> HandleAsync(
        GetBalanceQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var agora = clock.UtcNow;
        var corte = query.AsOf ?? agora;

        AccountId contaId;

        try
        {
            contaId = AccountId.From(query.AccountId);
        }
        catch (DomainException erro)
        {
            return new GetBalanceResult(
                GetBalanceStatus.Rejected,
                ErrorCode: erro.Code,
                Detail: erro.Message);
        }

        if (corte > agora.Add(_options.MaxFutureSkew))
        {
            // Saldo "em" uma data futura não é conhecível: movimentações vão
            // chegar depois. Devolver um número aqui apresentaria uma resposta
            // incompleta como se fosse final — pior que recusar, porque quem
            // consome não teria como saber.
            return new GetBalanceResult(
                GetBalanceStatus.Rejected,
                ErrorCode: DomainErrorCode.MissingTimestamp,
                Detail: "Não é possível consultar saldo em um instante futuro: "
                      + "movimentações ainda podem ser registradas para esse período.");
        }

        var conta = await repository.FindAccountAsync(contaId, cancellationToken)
            .ConfigureAwait(false);

        if (conta is null)
        {
            return new GetBalanceResult(
                GetBalanceStatus.AccountNotFound,
                Detail: $"Conta {query.AccountId} não encontrada.");
        }

        var baseDeCalculo = _options.UseSnapshots
            ? await snapshots.FindForBalanceAsync(
                contaId,
                DateOnly.FromDateTime(corte.UtcDateTime),
                cancellationToken).ConfigureAwait(false)
            : new SnapshotLookup(null, SkippedStale: false);

        var fechamento = baseDeCalculo.Usable;

        // Com fechamento, o delta começa no primeiro instante do dia seguinte ao
        // dia fechado. Sem fechamento, soma desde o começo do histórico.
        var inicioDoDelta = fechamento is null
            ? (DateTimeOffset?)null
            : LedgerTimePolicy.StartOfDay(fechamento.AsOfDate.AddDays(1));

        var delta = await repository
            .SumRangeAsync(contaId, inicioDoDelta, corte, cancellationToken)
            .ConfigureAwait(false);

        var aberturaBruta = fechamento?.ClosingBalance.Amount ?? 0m;

        // O domínio decide o que é um valor monetário válido. Passar pelo
        // Money em lugar de devolver o decimal cru garante que uma soma com
        // escala inesperada — vinda de um esquema alterado à mão, por exemplo —
        // falhe aqui, em lugar de aparecer arredondada na resposta.
        var saldo = Money.Of(aberturaBruta, conta.Currency)
            .Add(Money.Of(delta.SignedAmount, conta.Currency));

        return new GetBalanceResult(
            GetBalanceStatus.Found,
            AsOf: corte,
            Balance: saldo.Amount,
            Currency: saldo.Currency.Code,
            MovementCount: (fechamento?.MovementCount ?? 0L) + delta.MovementCount,
            Source: fechamento is null ? BalanceSource.DirectSum : BalanceSource.SnapshotPlusDelta,
            SnapshotDate: fechamento?.AsOfDate,
            Stale: baseDeCalculo.SkippedStale);
    }
}
