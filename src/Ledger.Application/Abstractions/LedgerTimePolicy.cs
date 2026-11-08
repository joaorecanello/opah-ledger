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

    /// <summary>Primeiro instante de um dia, em UTC.</summary>
    public static DateTimeOffset StartOfDay(DateOnly day) =>
        new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>
    /// Último instante representável de um dia, em UTC.
    /// </summary>
    /// <remarks>
    /// <para>
    /// É o começo do dia seguinte menos <b>um microssegundo</b>, não menos um
    /// tick. A precisão do armazenamento é o microssegundo, e usar um tick
    /// deixaria uma fenda de 900 nanossegundos entre este instante e
    /// <see cref="StartOfDay"/> do dia seguinte — uma movimentação que caísse
    /// nela não entraria em nenhum dos dois intervalos e desapareceria do saldo.
    /// </para>
    /// <para>
    /// A regra vive aqui, e não em cada chamador, porque ela aparece em três
    /// lugares — consulta de saldo, consolidação e reconciliação — e os três
    /// precisam concordar exatamente. Fronteira de intervalo calculada de forma
    /// ligeiramente diferente em dois lugares é um lançamento contado duas vezes
    /// ou nenhuma.
    /// </para>
    /// </remarks>
    public static DateTimeOffset EndOfDay(DateOnly day) =>
        StartOfDay(day.AddDays(1)).AddTicks(-TicksPerMicrosecond);
}
