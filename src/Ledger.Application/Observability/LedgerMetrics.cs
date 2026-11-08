using System.Diagnostics.Metrics;
using Ledger.Domain;

namespace Ledger.Application.Observability;

/// <summary>
/// Métricas de <b>negócio</b> do razão.
/// </summary>
/// <remarks>
/// <para>
/// Métrica técnica mede o veículo; métrica de negócio mede se a carga chegou. Um
/// painel com latência saudável e taxa de erro zero pode estar descrevendo um
/// sistema que perdeu movimentações em silêncio — e só a segunda coisa é
/// emergência. Sem estas métricas, "sistema lento" e "sistema registrando errado"
/// são indistinguíveis.
/// </para>
/// <para>
/// <b>Por que uma instância criada por <see cref="IMeterFactory"/>, e não um
/// <c>Meter</c> estático.</b> Um medidor estático é estado global do processo: dois
/// hosts no mesmo processo — dois testes em paralelo, por exemplo — somam nas
/// mesmas séries, e nenhum teste consegue afirmar uma contagem sem ser
/// contaminado pelos outros. Com a fábrica, cada host tem o seu, e a descoberta
/// pelo nome continua funcionando para o OpenTelemetry.
/// </para>
/// <para>
/// <b>Nenhuma etiqueta tem cardinalidade alta.</b> Não há identificador de conta,
/// de cliente nem de movimentação em etiqueta alguma. Cada combinação de valores
/// de etiqueta vira uma série nova no backend, e uma etiqueta por conta
/// significaria dez milhões de séries — o que derruba o sistema de métricas, não o
/// razão. Os valores possíveis são enumerações fechadas.
/// </para>
/// </remarks>
public sealed class LedgerMetrics : IDisposable
{
    /// <summary>
    /// Nome do medidor, usado pelo OpenTelemetry para descobri-lo.
    /// </summary>
    public const string MeterName = "Ledger";

    private readonly Meter _meter;
    private readonly Counter<long> _posted;
    private readonly Counter<long> _rejected;
    private readonly Counter<long> _replays;
    private readonly Counter<long> _conflicts;
    private readonly Histogram<double> _balanceDuration;
    private readonly Counter<long> _snapshotsWritten;
    private readonly Gauge<double> _snapshotLag;
    private readonly Counter<long> _divergences;

    /// <summary>Cria as métricas a partir da fábrica de medidores do host.</summary>
    public LedgerMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        _meter = meterFactory.Create(MeterName);

        _posted = _meter.CreateCounter<long>(
            "ledger.movements.posted",
            unit: "{movement}",
            description: "Movimentações registradas no razão, por tipo.");

        _rejected = _meter.CreateCounter<long>(
            "ledger.movements.rejected",
            unit: "{request}",
            description: "Pedidos de registro recusados por regra de negócio, por motivo. "
                       + "Alta concentração num motivo costuma indicar um cliente com defeito.");

        _replays = _meter.CreateCounter<long>(
            "ledger.idempotency.replays",
            unit: "{request}",
            description: "Repetições respondidas com a resposta original. Alta não é erro do "
                       + "razão: indica cliente com timeout mal calibrado.");

        _conflicts = _meter.CreateCounter<long>(
            "ledger.idempotency.conflicts",
            unit: "{request}",
            description: "Chaves reaproveitadas com conteúdo diferente. É defeito no chamador.");

        // Os limites padrão do SDK do OpenTelemetry são pensados para
        // milissegundos (0, 5, 10, 25 ... 10000). Aplicados a uma duração em
        // segundos, toda consulta cairia no primeiro balde e o histograma não
        // diria nada. Daí as fronteiras explícitas, em segundos.
        _balanceDuration = _meter.CreateHistogram(
            "ledger.balance.query.duration",
            unit: "s",
            description: "Duração da consulta de saldo, por caminho de cálculo.",
            tags: null,
            advice: new InstrumentAdvice<double>
            {
                HistogramBucketBoundaries =
                    [0.001, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10],
            });

        _snapshotsWritten = _meter.CreateCounter<long>(
            "ledger.snapshot.consolidated",
            unit: "{snapshot}",
            description: "Fechamentos de saldo gravados pela consolidação.");

        _snapshotLag = _meter.CreateGauge<double>(
            "ledger.snapshot.lag",
            unit: "s",
            description: "Quão atrás do último dia encerrado está o fechamento mais "
                       + "atrasado. Alto significa consultas caindo no caminho lento.");

        _divergences = _meter.CreateCounter<long>(
            "ledger.snapshot.divergence",
            unit: "{snapshot}",
            description: "Fechamentos que NÃO correspondem ao razão. Qualquer valor acima "
                       + "de zero é incidente.");
    }

    /// <summary>O medidor subjacente. Exposto para que testes o isolem.</summary>
    public Meter Meter => _meter;

    /// <summary>Uma movimentação foi registrada.</summary>
    public void MovementPosted(MovementType type) =>
        _posted.Add(1, new KeyValuePair<string, object?>("type", type.ToString()));

    /// <summary>Um pedido de registro foi recusado por regra de negócio.</summary>
    public void MovementRejected(DomainErrorCode? reason) =>
        _rejected.Add(
            1,
            new KeyValuePair<string, object?>("reason", reason?.ToString() ?? "Unspecified"));

    /// <summary>Uma repetição foi respondida com a resposta original.</summary>
    public void IdempotentReplay() => _replays.Add(1);

    /// <summary>Uma chave foi reaproveitada com conteúdo diferente.</summary>
    public void IdempotencyConflict() => _conflicts.Add(1);

    /// <summary>Registra a duração de uma consulta de saldo.</summary>
    /// <param name="duration">Duração.</param>
    /// <param name="source">Caminho de cálculo usado.</param>
    /// <param name="stale">A consulta recuou de um fechamento obsoleto.</param>
    /// <remarks>
    /// Recebe <see cref="TimeSpan"/> e não um número. O tipo carrega a unidade: com
    /// <c>double</c>, nada impede passar milissegundos a uma métrica em segundos, e
    /// o histograma ficaria mil vezes errado sem que nenhum erro aparecesse. Também
    /// mantém a camada de aplicação sem <c>double</c> em assinatura alguma, que é a
    /// regra do teste de arquitetura — e ela se aplica a duração tanto quanto a
    /// dinheiro, porque um teste que abre exceções por "esta é só uma duração"
    /// deixa de proteger o que importa.
    /// </remarks>
    public void BalanceQueried(TimeSpan duration, string source, bool stale) =>
        _balanceDuration.Record(
            duration.TotalSeconds,
            new KeyValuePair<string, object?>("source", source),
            new KeyValuePair<string, object?>("stale", stale));

    /// <summary>A consolidação gravou fechamentos.</summary>
    public void SnapshotsConsolidated(int count)
    {
        if (count > 0)
        {
            _snapshotsWritten.Add(count);
        }
    }

    /// <summary>Atualiza quão atrasado está o fechamento mais atrasado.</summary>
    public void SnapshotLag(TimeSpan lag) => _snapshotLag.Record(lag.TotalSeconds);

    /// <summary>A reconciliação encontrou fechamentos divergentes do razão.</summary>
    public void SnapshotDivergences(int count)
    {
        if (count > 0)
        {
            _divergences.Add(count);
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _meter.Dispose();
}
