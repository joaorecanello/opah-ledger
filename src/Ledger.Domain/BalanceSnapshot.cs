namespace Ledger.Domain;

/// <summary>
/// Saldo de fechamento de uma conta num dia já encerrado.
/// </summary>
/// <remarks>
/// <para>
/// <b>Isto é cache, nunca fonte da verdade.</b> A tabela de snapshots pode ser
/// truncada por inteiro a qualquer momento e reconstruída a partir do ledger. É
/// essa propriedade que torna a otimização aceitável em dados financeiros: se
/// houver dúvida sobre um número, joga-se fora e recalcula.
/// </para>
/// <para>
/// <b>Somente período encerrado.</b> Como o passado é imutável, o snapshot de um
/// dia fechado nunca fica errado — não existe invalidação por mudança de estado,
/// porque não existe mudança de estado.
/// </para>
/// <para>
/// A exceção é movimentação retroativa, cujo <c>occurredAt</c> cai antes de
/// snapshots já calculados. Aí os posteriores ficam obsoletos e são marcados com
/// <see cref="IsStale"/>; a consulta que os alcança cai no cálculo direto —
/// correto e mais lento — enquanto o worker recalcula. O princípio é correto
/// sempre, rápido quase sempre, nunca o contrário.
/// </para>
/// </remarks>
public sealed class BalanceSnapshot
{
    private BalanceSnapshot(
        AccountId accountId,
        DateOnly asOfDate,
        Money closingBalance,
        long movementCount,
        bool isStale)
    {
        AccountId = accountId;
        AsOfDate = asOfDate;
        ClosingBalance = closingBalance;
        MovementCount = movementCount;
        IsStale = isStale;
    }

    /// <summary>Conta a que o snapshot se refere.</summary>
    public AccountId AccountId { get; }

    /// <summary>Dia encerrado que o snapshot consolida.</summary>
    public DateOnly AsOfDate { get; }

    /// <summary>Saldo ao fim do dia.</summary>
    public Money ClosingBalance { get; }

    /// <summary>
    /// Quantidade de movimentações consideradas até o fechamento.
    /// </summary>
    /// <remarks>
    /// Guardado para a reconciliação: divergência de contagem localiza o
    /// problema mais rápido que divergência de valor, porque aponta se faltou
    /// lançamento ou se o valor de algum está errado.
    /// </remarks>
    public long MovementCount { get; }

    /// <summary>
    /// O snapshot foi invalidado por movimentação retroativa e não deve ser
    /// usado.
    /// </summary>
    public bool IsStale { get; }

    /// <summary>Cria um snapshot válido.</summary>
    /// <exception cref="DomainException">
    /// A conta não foi informada, a moeda do saldo está ausente, ou a contagem
    /// de movimentações é negativa.
    /// </exception>
    public static BalanceSnapshot Create(
        AccountId accountId,
        DateOnly asOfDate,
        Money closingBalance,
        long movementCount)
    {
        if (accountId.IsUnset)
        {
            throw DomainException.For(
                DomainErrorCode.MissingIdentifier,
                "Snapshot exige identificador de conta.");
        }

        if (closingBalance.Currency.IsUnset)
        {
            throw DomainException.For(
                DomainErrorCode.MissingCurrency,
                "Snapshot exige moeda no saldo de fechamento.");
        }

        if (movementCount < 0)
        {
            throw DomainException.For(
                DomainErrorCode.AmountNotPositive,
                $"Contagem de movimentações não pode ser negativa. Recebida: {movementCount}.");
        }

        return new BalanceSnapshot(accountId, asOfDate, closingBalance, movementCount, isStale: false);
    }

    /// <summary>Reconstrói um snapshot já persistido.</summary>
    public static BalanceSnapshot Rehydrate(
        AccountId accountId,
        DateOnly asOfDate,
        Money closingBalance,
        long movementCount,
        bool isStale) =>
        new(accountId, asOfDate, closingBalance, movementCount, isStale);

    /// <summary>
    /// Devolve uma cópia marcada como obsoleta.
    /// </summary>
    /// <remarks>
    /// Devolve nova instância em lugar de alterar esta: o tipo é imutável, como
    /// todo o resto do domínio. Marcar e não apagar é deliberado — o valor
    /// antigo continua disponível para diagnóstico de quanto a retroatividade
    /// mudou o histórico.
    /// </remarks>
    public BalanceSnapshot MarkStale() =>
        IsStale
            ? this
            : new BalanceSnapshot(AccountId, AsOfDate, ClosingBalance, MovementCount, isStale: true);
}
