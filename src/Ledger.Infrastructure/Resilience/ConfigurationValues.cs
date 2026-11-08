using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Ledger.Infrastructure.Resilience;

/// <summary>
/// Leitura tolerante de valores de configuração.
/// </summary>
/// <remarks>
/// Pelo indexador e com conversão explícita, em lugar de <c>GetValue</c>: isso
/// dispensa o pacote de binding só por causa de alguns métodos e deixa o formato
/// aceito à vista. <c>TimeSpan</c> usa a notação <c>hh:mm:ss</c>.
/// <para>
/// Valor ausente <b>ou inválido</b> cai no padrão. É deliberado: um limite de
/// tempo mal digitado não deve derrubar a inicialização do serviço — o padrão é
/// seguro, e o sintoma de um valor ignorado (comportamento padrão) é muito menos
/// grave que o de um processo que não sobe.
/// </para>
/// </remarks>
public static class ConfigurationValues
{
    /// <summary>Lê um intervalo de tempo.</summary>
    public static TimeSpan ReadDuration(IConfigurationSection section, string key, TimeSpan fallback)
    {
        ArgumentNullException.ThrowIfNull(section);

        return System.TimeSpan.TryParse(section[key], CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    /// <summary>Lê um inteiro.</summary>
    public static int ReadInt32(IConfigurationSection section, string key, int fallback)
    {
        ArgumentNullException.ThrowIfNull(section);

        return int.TryParse(
            section[key],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : fallback;
    }

    /// <summary>Lê um número decimal.</summary>
    public static double ReadFraction(IConfigurationSection section, string key, double fallback)
    {
        ArgumentNullException.ThrowIfNull(section);

        return double.TryParse(
            section[key],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : fallback;
    }
}
