using Microsoft.Extensions.Configuration;

namespace Ledger.Infrastructure.Resilience;

/// <summary>
/// Limites de tempo e de repetição no acesso ao banco.
/// </summary>
/// <remarks>
/// <para>
/// <b>Todo I/O tem limite.</b> Timeout infinito transforma lentidão de
/// dependência em esgotamento de pool, que é como falha parcial vira queda
/// total: cada requisição presa ocupa uma conexão e uma thread, e em segundos
/// nada mais atende.
/// </para>
/// <para>
/// Os padrões são conservadores para falhar rápido. Um razão que responde
/// <c>503</c> em dois segundos é mais útil que um que responde <c>200</c> em
/// quarenta — o chamador tem retry, o pool não tem como se esvaziar sozinho.
/// </para>
/// </remarks>
public sealed record LedgerResilienceOptions
{
    /// <summary>Seção da configuração de onde estes valores são lidos.</summary>
    public const string SectionName = "Ledger:Resilience";

    /// <summary>Tempo máximo para abrir uma conexão com o banco.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Tempo máximo para um comando individual.</summary>
    /// <remarks>
    /// Cobre a consulta de saldo mais pesada prevista. Se uma consulta passa
    /// disso, o problema é a consulta, não o limite.
    /// </remarks>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Quantas vezes repetir uma operação após falha transitória.</summary>
    /// <remarks>
    /// Dois, não cinco. Retry demais amplifica a carga sobre uma dependência que
    /// já está sofrendo: com três tentativas por requisição, uma queda de banco
    /// triplica o tráfego que chega a ele no momento em que ele menos aguenta.
    /// </remarks>
    public int MaxRetries { get; init; } = 2;

    /// <summary>Espera máxima entre tentativas.</summary>
    /// <remarks>
    /// O EF aplica <i>backoff</i> exponencial com <i>jitter</i> até este teto. O
    /// jitter não é refinamento: sem ele, N clientes que falharam juntos repetem
    /// juntos, e o retry sincronizado reproduz o pico que causou a falha.
    /// </remarks>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Tempo total após o qual nenhuma nova tentativa começa.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Existe porque limitar o <i>número</i> de tentativas não limita o
    /// <i>tempo</i>. Medido com o banco parado: cada tentativa levava cerca de
    /// dois segundos para falhar, e três faziam toda requisição esperar sete.
    /// </para>
    /// <para>
    /// Falha rápida — conexão descartada pelo pool, reinício, rede que piscou —
    /// cabe no orçamento e é repetida. Falha lenta o consome sozinha e sobe na
    /// hora. É a diferença entre retry que ajuda e retry que só adia o erro.
    /// </para>
    /// </remarks>
    public TimeSpan RetryBudget { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Lê as opções da configuração, preservando os padrões do que faltar.</summary>
    public static LedgerResilienceOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var secao = configuration.GetSection(SectionName);
        var padrao = new LedgerResilienceOptions();

        return new LedgerResilienceOptions
        {
            ConnectTimeout = ConfigurationValues.ReadDuration(secao, nameof(ConnectTimeout), padrao.ConnectTimeout),
            CommandTimeout = ConfigurationValues.ReadDuration(secao, nameof(CommandTimeout), padrao.CommandTimeout),
            MaxRetries = ConfigurationValues.ReadInt32(secao, nameof(MaxRetries), padrao.MaxRetries),
            MaxRetryDelay = ConfigurationValues.ReadDuration(secao, nameof(MaxRetryDelay), padrao.MaxRetryDelay),
            RetryBudget = ConfigurationValues.ReadDuration(secao, nameof(RetryBudget), padrao.RetryBudget),
        };
    }
}
