namespace Ledger.Domain;

/// <summary>
/// Calcula saldo a partir de movimentações. Função pura, sem estado.
/// </summary>
/// <remarks>
/// <para>
/// Este é o cálculo que o ledger append-only torna necessário: o saldo não está
/// guardado em lugar nenhum, ele é derivado dos fatos. Vive no domínio, e não na
/// consulta SQL, por dois motivos: é regra de negócio, e é testável em
/// milissegundos sem banco.
/// </para>
/// <para>
/// Em produção a consulta de saldo <b>não</b> soma o histórico inteiro — ela
/// parte do snapshot do último período fechado e soma apenas o delta. Este tipo
/// é o que calcula ambos, e é também o oráculo contra o qual a otimização do
/// snapshot é verificada: o teste de equivalência exige que os dois caminhos
/// devolvam exatamente o mesmo número.
/// </para>
/// </remarks>
public static class BalanceCalculator
{
    /// <summary>
    /// Soma as movimentações a partir de um saldo de abertura.
    /// </summary>
    /// <param name="opening">
    /// Saldo de abertura — zero, no cálculo desde o início do histórico, ou o
    /// fechamento do snapshot anterior, no cálculo incremental.
    /// </param>
    /// <param name="movements">Movimentações a aplicar. A ordem é irrelevante.</param>
    /// <returns>O saldo resultante.</returns>
    /// <remarks>
    /// A ordem não importa porque a soma é comutativa, e isso não é um detalhe:
    /// significa que o saldo não depende de o ledger ter uma sequência contígua
    /// por conta, o que é o que permite escrever sem contenção. Ver o ADR-0007.
    /// </remarks>
    /// <exception cref="DomainException">
    /// Alguma movimentação está em moeda diferente da do saldo de abertura.
    /// </exception>
    public static Money From(Money opening, IEnumerable<Movement> movements)
    {
        ArgumentNullException.ThrowIfNull(movements);

        var saldo = opening;

        foreach (var movimentacao in movements)
        {
            ArgumentNullException.ThrowIfNull(movimentacao);
            saldo = saldo.Add(movimentacao.SignedAmount);
        }

        return saldo;
    }

    /// <summary>
    /// Soma as movimentações partindo de zero na moeda informada.
    /// </summary>
    /// <param name="currency">Moeda do saldo.</param>
    /// <param name="movements">Movimentações a aplicar.</param>
    /// <returns>
    /// O saldo resultante, que é zero quando não há movimentação — e zero, não
    /// erro: uma conta sem lançamentos tem saldo zero, e uma consulta anterior à
    /// primeira movimentação é pergunta legítima, não caso de borda inválido.
    /// </returns>
    public static Money From(Currency currency, IEnumerable<Movement> movements) =>
        From(Money.Zero(currency), movements);
}
