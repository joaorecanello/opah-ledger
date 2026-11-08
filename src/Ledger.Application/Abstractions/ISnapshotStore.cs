using Ledger.Domain;

namespace Ledger.Application.Abstractions;

/// <summary>
/// O que a consulta de saldo encontrou de snapshot utilizável.
/// </summary>
/// <param name="Usable">
/// O snapshot mais recente <b>não obsoleto</b> antes do corte, ou <c>null</c> se
/// não houver nenhum.
/// </param>
/// <param name="SkippedStale">
/// Havia um snapshot mais próximo do corte, porém obsoleto, e ele foi ignorado.
/// </param>
/// <remarks>
/// A distinção importa para a resposta da API. O número é correto nos dois
/// casos — a diferença é que, quando um snapshot mais próximo foi ignorado, a
/// consulta somou um intervalo maior de movimentações. Sinalizar isso diz a
/// quem consome que aquela resposta custou mais caro, e que houve atividade
/// retroativa naquele período.
/// </remarks>
public sealed record SnapshotLookup(BalanceSnapshot? Usable, bool SkippedStale);

/// <summary>
/// Acesso ao cache de saldo de fechamento.
/// </summary>
/// <remarks>
/// <para>
/// Porta separada de <see cref="ILedgerRepository"/> de propósito. O razão é
/// permanente e é a fonte da verdade; o snapshot é cache descartável, com outro
/// ciclo de vida e outro conjunto de operações. Juntar os dois na mesma
/// interface sugeriria que têm o mesmo peso.
/// </para>
/// <para>
/// Nada aqui é obrigatório para a corretude do saldo: se esta tabela estiver
/// vazia, a consulta soma o histórico inteiro e devolve o mesmo número.
/// </para>
/// </remarks>
public interface ISnapshotStore
{
    /// <summary>
    /// Busca o snapshot a usar numa consulta de saldo.
    /// </summary>
    /// <param name="accountId">Conta.</param>
    /// <param name="beforeDate">
    /// Data do corte. Só snapshots de dias <b>anteriores</b> a esta data são
    /// considerados: o dia do corte ainda não está fechado do ponto de vista da
    /// consulta, e o delta cobre o que aconteceu nele.
    /// </param>
    /// <param name="cancellationToken">Token de cancelamento.</param>
    Task<SnapshotLookup> FindForBalanceAsync(
        AccountId accountId,
        DateOnly beforeDate,
        CancellationToken cancellationToken);

    /// <summary>Grava ou substitui o snapshot de um dia.</summary>
    /// <remarks>
    /// Substituir é o caso normal, não a exceção: a consolidação recalcula dias
    /// que foram invalidados por movimentação retroativa.
    /// </remarks>
    Task SaveAsync(BalanceSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>
    /// Marca como obsoletos os snapshots da conta a partir de uma data.
    /// </summary>
    /// <returns>Quantos snapshots foram marcados.</returns>
    Task<int> MarkStaleFromAsync(
        AccountId accountId,
        DateOnly fromDate,
        CancellationToken cancellationToken);

    /// <summary>Lista os snapshots de uma conta, do mais antigo ao mais recente.</summary>
    Task<IReadOnlyList<BalanceSnapshot>> ListAsync(
        AccountId accountId,
        CancellationToken cancellationToken);

    /// <summary>Data do snapshot mais recente da conta, ou <c>null</c>.</summary>
    /// <remarks>
    /// Inclui obsoletos: a consolidação precisa saber até onde já processou,
    /// independente de o resultado ainda valer.
    /// </remarks>
    Task<DateOnly?> FindLatestDateAsync(
        AccountId accountId,
        CancellationToken cancellationToken);
}
