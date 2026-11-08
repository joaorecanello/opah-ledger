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
    /// Correto em qualquer volume, e o único caminho hoje. Quando os snapshots
    /// de período fechado entrarem, este passa a ser o caminho de exceção — e
    /// continua sendo o oráculo contra o qual a otimização é verificada.
    /// </remarks>
    DirectSum,
}

/// <summary>Resultado de uma consulta de saldo.</summary>
/// <param name="Status">Desfecho.</param>
/// <param name="AsOf">Instante efetivamente usado no corte.</param>
/// <param name="Balance">Saldo, quando calculado.</param>
/// <param name="Currency">Moeda da conta, quando conhecida.</param>
/// <param name="MovementCount">Movimentações consideradas.</param>
/// <param name="Source">Como o número foi obtido.</param>
/// <param name="Stale">
/// O número veio de um snapshot invalidado por movimentação retroativa. Hoje
/// sempre <c>false</c>, porque não há snapshot.
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
/// A soma acontece no banco, não aqui. Carregar o histórico para somar em
/// memória seria inviável no volume assumido, e é exatamente a varredura que o
/// índice por conta e data existe para evitar.
/// </para>
/// </remarks>
public sealed class GetBalanceHandler(
    ILedgerRepository repository,
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

        var soma = await repository.SumUpToAsync(contaId, corte, cancellationToken)
            .ConfigureAwait(false);

        // O domínio decide o que é um valor monetário válido. Passar pelo
        // Money.Of em lugar de devolver o decimal cru garante que uma soma com
        // escala inesperada — vinda de um esquema alterado à mão, por exemplo —
        // falhe aqui, em lugar de aparecer arredondada na resposta.
        var saldo = Money.Of(soma.SignedAmount, conta.Currency);

        return new GetBalanceResult(
            GetBalanceStatus.Found,
            AsOf: corte,
            Balance: saldo.Amount,
            Currency: saldo.Currency.Code,
            MovementCount: soma.MovementCount,
            Source: BalanceSource.DirectSum,
            Stale: false);
    }
}
