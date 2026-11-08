using Microsoft.Extensions.Logging;

namespace Ledger.Integration.Tests;

/// <summary>Uma linha de log capturada, com tudo o que um agregador receberia.</summary>
/// <param name="Categoria">Categoria do logger.</param>
/// <param name="Nivel">Nível.</param>
/// <param name="Texto">
/// Mensagem formatada, campos estruturados, escopos e exceção, num só texto — a
/// superfície completa em que um dado sensível poderia aparecer.
/// </param>
public sealed record LinhaDeLog(string Categoria, LogLevel Nivel, string Texto);

/// <summary>
/// Captura o que o host escreve em log, para provar o que <b>não</b> está nele.
/// </summary>
/// <remarks>
/// <para>
/// Inspecionar só a mensagem formatada deixaria passar o caminho mais provável de
/// vazamento: o dado sensível vir num campo estruturado, num escopo ou na exceção,
/// e nunca no texto. O que um agregador de logs recebe é tudo isso, então é tudo
/// isso que se verifica.
/// </para>
/// <para>
/// Respeita os filtros de nível da configuração do host — o provedor entra pela
/// coleção de serviços, como qualquer outro. Logo o que se captura é o que a
/// configuração de produção deixaria passar, e não um nível mais verboso que
/// ninguém usa.
/// </para>
/// </remarks>
internal sealed class LogCapture : ILoggerProvider, ISupportExternalScope
{
    private readonly List<LinhaDeLog> _linhas = [];
    private IExternalScopeProvider _escopos = new LoggerExternalScopeProvider();

    /// <summary>Linhas capturadas até agora.</summary>
    public IReadOnlyList<LinhaDeLog> Linhas
    {
        get
        {
            lock (_linhas)
            {
                return [.. _linhas];
            }
        }
    }

    /// <summary>Tudo o que foi logado, como um único texto.</summary>
    public string TextoCompleto => string.Join('\n', Linhas.Select(l => l.Texto));

    public ILogger CreateLogger(string categoryName) => new Capturador(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _escopos = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class Capturador(LogCapture dono, string categoria) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            dono._escopos.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            var partes = new List<string> { formatter(state, exception) };

            if (state is IEnumerable<KeyValuePair<string, object?>> campos)
            {
                partes.AddRange(campos.Select(c => $"{c.Key}={c.Value}"));
            }

            dono._escopos.ForEachScope(
                static (escopo, lista) =>
                {
                    if (escopo is IEnumerable<KeyValuePair<string, object>> pares)
                    {
                        lista.AddRange(pares.Select(p => $"{p.Key}={p.Value}"));
                    }
                    else
                    {
                        lista.Add(escopo?.ToString() ?? string.Empty);
                    }
                },
                partes);

            if (exception is not null)
            {
                partes.Add(exception.ToString());
            }

            lock (dono._linhas)
            {
                dono._linhas.Add(new LinhaDeLog(categoria, logLevel, string.Join(" | ", partes)));
            }
        }
    }
}
