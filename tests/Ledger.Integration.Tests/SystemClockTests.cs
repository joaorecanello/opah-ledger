using Ledger.Infrastructure;

namespace Ledger.Integration.Tests;

/// <summary>
/// Guarda a precisão do relógio.
/// </summary>
/// <remarks>
/// Este invariante foi descoberto por uma falha <i>indireta</i>: dois testes de
/// integração comparavam o <c>PostedAt</c> devolvido no registro com o devolvido
/// no replay, e eles diferiam em nanossegundos porque o
/// <see cref="DateTimeOffset.UtcNow"/> tem resolução de 100 ns e o
/// <c>timestamptz</c> guarda microssegundos.
/// <para>
/// Depender daquela falha indireta como proteção seria frágil: se alguém mudasse
/// a asserção para uma comparação tolerante, o defeito voltaria em silêncio e a
/// resposta do replay deixaria de ser idêntica à original. Daí este teste
/// direto.
/// </para>
/// </remarks>
public sealed class SystemClockTests
{
    [Fact]
    public void Relogio_nao_devolve_fracao_de_microssegundo()
    {
        var relogio = new SystemClock();

        for (var i = 0; i < 1_000; i++)
        {
            var agora = relogio.UtcNow;
            (agora.Ticks % SystemClock.TicksPerMicrosecond).ShouldBe(
                0,
                "o instante precisa sobreviver à ida e volta ao PostgreSQL sem mudar");
        }
    }

    [Fact]
    public void Relogio_e_sempre_utc()
    {
        // Instante em hora local é ambíguo duas vezes por ano no horário de
        // verão: uma hora que acontece duas vezes produziria dois lançamentos
        // com o mesmo carimbo e ordem indeterminada.
        new SystemClock().UtcNow.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(9, 0)]
    [InlineData(10, 10)]
    [InlineData(19, 10)]
    [InlineData(1_234_567, 1_234_560)]
    public void Truncamento_descarta_apenas_a_fracao_de_microssegundo(long ticks, long esperado)
    {
        var instante = new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero)
            .AddTicks(ticks);

        var truncado = SystemClock.Truncate(instante);

        (truncado.Ticks - new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero).Ticks)
            .ShouldBe(esperado);
    }
}
