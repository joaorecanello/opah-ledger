namespace Ledger.Application.Abstractions;

/// <summary>Padrões de tolerância temporal compartilhados pelos casos de uso.</summary>
/// <remarks>
/// Centralizados aqui para que o mesmo número não seja escrito em dois lugares.
/// Cada caso de uso ainda pode ajustar o seu: registrar e consultar têm razões
/// diferentes para tolerar o futuro, e podem divergir se o negócio pedir.
/// </remarks>
public static class LedgerTimePolicy
{
    /// <summary>
    /// Quanto um instante pode estar à frente do relógio do servidor antes de
    /// ser tratado como erro.
    /// </summary>
    /// <remarks>
    /// Não é zero de propósito: relógios de servidores divergem por alguns
    /// segundos mesmo com NTP, e tolerância zero recusaria pedido legítimo. O
    /// que o limite impede é data absurda, não desvio normal.
    /// </remarks>
    public static readonly TimeSpan DefaultFutureSkew = TimeSpan.FromMinutes(1);

    /// <summary>Ticks por microssegundo (1 tick = 100 ns).</summary>
    public const long TicksPerMicrosecond = 10L;

    /// <summary>
    /// Descarta a fração de microssegundo de um instante.
    /// </summary>
    /// <remarks>
    /// <para>
    /// É a precisão que o armazenamento mantém: o <c>timestamptz</c> do
    /// PostgreSQL guarda microssegundos, e o <see cref="DateTimeOffset"/> tem
    /// resolução de 100 nanossegundos.
    /// </para>
    /// <para>
    /// Truncar na fronteira, e não deixar o banco truncar, é o que mantém o
    /// objeto em memória igual à linha gravada. Sem isso a API responderia um
    /// instante que o razão não guardou — e a resposta de um replay, lida do
    /// banco, discordaria da resposta do registro original. Dois carimbos
    /// diferentes para o mesmo lançamento é a divergência que a idempotência
    /// existe para evitar.
    /// </para>
    /// <para>
    /// A precisão descartada é real e aceitável: nenhum sistema financeiro
    /// distingue dois lançamentos por 300 nanossegundos. O que não é aceitável é
    /// afirmar uma precisão que não foi preservada.
    /// </para>
    /// </remarks>
    public static DateTimeOffset Truncate(DateTimeOffset instant) =>
        new(instant.Ticks - (instant.Ticks % TicksPerMicrosecond), instant.Offset);
}
