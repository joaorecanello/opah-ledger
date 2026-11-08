using Ledger.Application.Abstractions;

namespace Ledger.Infrastructure;

/// <summary>Relógio do sistema, em UTC e truncado ao microssegundo.</summary>
/// <remarks>
/// <para>
/// <b>Sempre UTC, nunca hora local.</b> O razão registra instantes, e instante em
/// hora local é ambíguo duas vezes por ano no horário de verão — uma hora que
/// acontece duas vezes produziria dois lançamentos com o mesmo carimbo e ordem
/// indeterminada.
/// </para>
/// <para>
/// <b>Truncado ao microssegundo, de propósito.</b> O
/// <see cref="DateTimeOffset.UtcNow"/> tem resolução de 100 nanossegundos; o
/// <c>timestamptz</c> do PostgreSQL guarda microssegundos. Sem truncar, o
/// instante que a API devolve no registro não é o mesmo que volta do banco no
/// replay — eles diferem em até 999 nanossegundos.
/// </para>
/// <para>
/// Parece irrelevante e não é: a resposta do replay tem que ser <b>idêntica</b> à
/// original, porque quem chama pode estar usando esses valores para conciliar.
/// Dois carimbos diferentes para o mesmo lançamento são exatamente a divergência
/// que a idempotência existe para evitar. Foi um teste de integração que
/// encontrou isso.
/// </para>
/// <para>
/// O truncamento vive aqui, na infraestrutura, porque a precisão disponível é
/// característica do armazenamento. Qualquer implementação de
/// <see cref="IClock"/> precisa respeitar o mesmo contrato.
/// </para>
/// </remarks>
public sealed class SystemClock : IClock
{
    /// <summary>Ticks por microssegundo (1 tick = 100 ns).</summary>
    public const long TicksPerMicrosecond = 10L;

    /// <inheritdoc/>
    public DateTimeOffset UtcNow => Truncate(DateTimeOffset.UtcNow);

    /// <summary>
    /// Descarta a fração de microssegundo, para que o valor sobreviva à ida e
    /// volta ao PostgreSQL sem mudar.
    /// </summary>
    public static DateTimeOffset Truncate(DateTimeOffset instant) =>
        new(instant.Ticks - (instant.Ticks % TicksPerMicrosecond), instant.Offset);
}
