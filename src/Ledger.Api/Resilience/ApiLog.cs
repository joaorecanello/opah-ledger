namespace Ledger.Api.Resilience;

/// <summary>
/// Mensagens de log geradas em tempo de compilação.
/// </summary>
/// <remarks>
/// <para>
/// O gerador do <see cref="LoggerMessageAttribute"/> verifica o nível antes de
/// avaliar os argumentos. Num caminho que, durante uma queda, roda a cada
/// requisição, isso é diferença entre custo zero e uma alocação por chamada com o
/// log desligado.
/// </para>
/// <para>
/// Rotas aparecem como <b>template</b> (<c>/api/v1/accounts/{accountId}/balance</c>),
/// nunca como o caminho recebido: o caminho carrega o identificador da conta, e o
/// log não pode. Nenhum método aqui aceita um <c>PathString</c>.
/// </para>
/// </remarks>
internal static partial class ApiLog
{
    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Warning,
        Message = "Dependência indisponível ao atender {Rota}. A requisição recebeu 503.")]
    public static partial void DependenciaIndisponivel(
        ILogger logger,
        Exception erro,
        string rota);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Warning,
        Message = "Requisição a {Rota} recusada sem consultar o banco: circuito aberto.")]
    public static partial void RecusadoComCircuitoAberto(ILogger logger, string rota);

    [LoggerMessage(
        EventId = 3003,
        Level = LogLevel.Error,
        Message = "Circuito ABERTO. O banco deixou de responder; novas requisições serão "
                + "recusadas por {Segundos} segundos antes de uma nova tentativa.")]
    public static partial void CircuitoAberto(ILogger logger, double segundos);

    [LoggerMessage(
        EventId = 3004,
        Level = LogLevel.Information,
        Message = "Circuito FECHADO. O banco voltou a responder.")]
    public static partial void CircuitoFechado(ILogger logger);

    [LoggerMessage(
        EventId = 3005,
        Level = LogLevel.Warning,
        Message = "Circuito SEMI-ABERTO. Uma requisição de teste vai verificar o banco.")]
    public static partial void CircuitoSemiAberto(ILogger logger);

    [LoggerMessage(
        EventId = 3006,
        Level = LogLevel.Warning,
        Message = "Banco não respondeu à sondagem de prontidão.")]
    public static partial void ProntidaoFalhou(ILogger logger);
}
