using Microsoft.Extensions.Logging;

namespace Ledger.Worker;

/// <summary>
/// Mensagens de log geradas em tempo de compilação.
/// </summary>
/// <remarks>
/// <para>
/// O gerador do <see cref="LoggerMessageAttribute"/> produz, para cada método,
/// uma verificação de nível <b>antes</b> de avaliar os argumentos. Chamar
/// <c>LogInformation</c> direto avalia tudo e aloca o array de parâmetros mesmo
/// quando o nível está desligado — e num job que roda a cada hora sobre todas as
/// contas isso não é hipotético.
/// </para>
/// <para>
/// Ganho lateral que importa mais: os nomes dos campos ficam fixos no
/// atributo, logo o log estruturado tem esquema estável. Quem consulta o
/// agregador procura por <c>Divergencias</c>, não por uma posição no texto.
/// </para>
/// </remarks>
internal static partial class WorkerLog
{
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Consolidação gravou {Fechamentos} fechamentos em {Contas} de {Visitadas} contas.")]
    public static partial void ConsolidacaoGravou(
        ILogger logger,
        int fechamentos,
        int contas,
        int visitadas);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Error,
        Message = "Passada de consolidação falhou. Será repetida na próxima cadência.")]
    public static partial void ConsolidacaoFalhou(ILogger logger, Exception erro);

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Reconciliação conferiu {Verificados} fechamentos, sem divergência.")]
    public static partial void ReconciliacaoLimpa(ILogger logger, int verificados);

    /// <remarks>
    /// Nível de erro, não aviso. Saldo divergente é o pior defeito possível
    /// deste sistema: as outras medições dizem se ele está rápido ou se devolve
    /// erro, e nenhuma pega responder <c>200 OK</c> com o número errado.
    /// <para>
    /// Em E12 isto também incrementa <c>ledger_snapshot_divergence_total</c>,
    /// cujo alerta dispara em qualquer valor diferente de zero — não há limiar
    /// tolerável para divergência de saldo.
    /// </para>
    /// </remarks>
    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Error,
        Message = "Reconciliação encontrou {Divergencias} fechamentos divergentes em "
                + "{Verificados} verificados; {Marcados} marcados para recálculo. "
                + "Primeira: conta {Conta} em {Dia}, gravado {Gravado}, recalculado {Recalculado}.")]
    public static partial void ReconciliacaoDivergente(
        ILogger logger,
        int divergencias,
        int verificados,
        int marcados,
        Guid conta,
        DateOnly dia,
        decimal gravado,
        decimal recalculado);

    [LoggerMessage(
        EventId = 2003,
        Level = LogLevel.Error,
        Message = "Passada de reconciliação falhou. Será repetida na próxima cadência.")]
    public static partial void ReconciliacaoFalhou(ILogger logger, Exception erro);
}
