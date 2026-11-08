using System.Diagnostics.Metrics;
using Ledger.Application.Observability;

namespace Ledger.Application.Tests;

/// <summary>
/// Fábrica de medidores isolada por teste.
/// </summary>
/// <remarks>
/// É a razão de <see cref="LedgerMetrics"/> usar <see cref="IMeterFactory"/> em vez
/// de um <c>Meter</c> estático. Um medidor estático é estado global do processo:
/// os testes rodam em paralelo, e todos somariam nas mesmas séries, de modo que
/// nenhum conseguiria afirmar "exatamente uma movimentação registrada" sem ser
/// contaminado pelos outros. Aqui cada teste tem o seu, e o escopo do medidor é a
/// própria fábrica.
/// </remarks>
internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _medidores = [];

    public Meter Create(MeterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var medidor = new Meter(options.Name, options.Version, options.Tags, scope: this);

        lock (_medidores)
        {
            _medidores.Add(medidor);
        }

        return medidor;
    }

    public void Dispose()
    {
        lock (_medidores)
        {
            foreach (var medidor in _medidores)
            {
                medidor.Dispose();
            }

            _medidores.Clear();
        }
    }
}

/// <summary>Uma medição capturada, com seu nome, valor e etiquetas.</summary>
internal sealed record Medicao(
    string Instrumento,
    double Valor,
    IReadOnlyDictionary<string, object?> Etiquetas);

/// <summary>
/// Captura as medições de um único <see cref="LedgerMetrics"/>.
/// </summary>
internal sealed class ColetorDeMetricas : IDisposable
{
    private readonly MeterListener _ouvinte = new();
    private readonly List<Medicao> _medicoes = [];

    public ColetorDeMetricas(LedgerMetrics metricas)
    {
        ArgumentNullException.ThrowIfNull(metricas);

        var medidor = metricas.Meter;

        // Só o medidor deste teste: sem o filtro, o ouvinte receberia medições de
        // qualquer outro host que esteja rodando em paralelo no processo.
        _ouvinte.InstrumentPublished = (instrumento, ouvinte) =>
        {
            if (ReferenceEquals(instrumento.Meter, medidor))
            {
                ouvinte.EnableMeasurementEvents(instrumento);
            }
        };

        _ouvinte.SetMeasurementEventCallback<long>(
            (instrumento, valor, etiquetas, _) => Registrar(instrumento, valor, etiquetas));
        _ouvinte.SetMeasurementEventCallback<double>(
            (instrumento, valor, etiquetas, _) => Registrar(instrumento, valor, etiquetas));

        _ouvinte.Start();
    }

    /// <summary>Todas as medições capturadas até agora.</summary>
    public IReadOnlyList<Medicao> Medicoes
    {
        get
        {
            lock (_medicoes)
            {
                return [.. _medicoes];
            }
        }
    }

    /// <summary>Soma os valores de um instrumento, opcionalmente filtrando uma etiqueta.</summary>
    public double Soma(string instrumento, string? etiqueta = null, object? valor = null) =>
        Medicoes
            .Where(m => m.Instrumento == instrumento)
            .Where(m => etiqueta is null
                     || (m.Etiquetas.TryGetValue(etiqueta, out var atual) && Equals(atual, valor)))
            .Sum(m => m.Valor);

    /// <summary>Quantas medições um instrumento produziu.</summary>
    public int Contagem(string instrumento) =>
        Medicoes.Count(m => m.Instrumento == instrumento);

    public void Dispose() => _ouvinte.Dispose();

    private void Registrar<T>(
        Instrument instrumento,
        T valor,
        ReadOnlySpan<KeyValuePair<string, object?>> etiquetas)
        where T : struct
    {
        var mapa = new Dictionary<string, object?>();

        foreach (var etiqueta in etiquetas)
        {
            mapa[etiqueta.Key] = etiqueta.Value;
        }

        lock (_medicoes)
        {
            _medicoes.Add(new Medicao(instrumento.Name, Convert.ToDouble(valor, System.Globalization.CultureInfo.InvariantCulture), mapa));
        }
    }
}
