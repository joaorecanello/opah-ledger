using System.Diagnostics;
using OpenTelemetry;

namespace Ledger.Infrastructure.Telemetry;

/// <summary>
/// Troca o caminho cru da requisição pelo template da rota nos spans.
/// </summary>
/// <remarks>
/// <para>
/// A instrumentação do ASP.NET grava <c>url.path</c> em cada span de servidor, e o
/// caminho carrega o identificador da conta
/// (<c>/api/v1/accounts/01a1bc13-.../balance</c>). Traces são replicados para um
/// backend de terceiros, lidos por mais gente que o banco e retidos fora do
/// controle de acesso do razão — o mesmo motivo pelo qual o log não pode carregar
/// o identificador.
/// </para>
/// <para>
/// O span já traz <c>http.route</c> com o template
/// (<c>/api/v1/accounts/{accountId}/balance</c>), que diz qual operação foi e não
/// de quem. Este processador usa o template no lugar do caminho. Também mantém a
/// cardinalidade fechada: agrupar por <c>url.path</c> produziria uma série por
/// conta.
/// </para>
/// <para>
/// Roda no fim do span, quando a rota já foi resolvida pelo roteamento; no começo
/// ela ainda não existe.
/// </para>
/// </remarks>
public sealed class RouteRedactionProcessor : BaseProcessor<Activity>
{
    private static readonly string[] CamposComCaminho =
    [
        "url.path",
        "http.target",
        "url.full",
        "http.url",
    ];

    /// <inheritdoc/>
    public override void OnEnd(Activity data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.GetTagItem("http.route") is not string rota || string.IsNullOrEmpty(rota))
        {
            // Sem rota resolvida (404, por exemplo), o caminho é tudo o que há. Não
            // inventa um template: o caminho de uma rota que não existe não
            // identifica conta alguma.
            return;
        }

        foreach (var campo in CamposComCaminho)
        {
            if (data.GetTagItem(campo) is not null)
            {
                data.SetTag(campo, rota);
            }
        }
    }
}
