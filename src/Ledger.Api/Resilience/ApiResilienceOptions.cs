using Ledger.Infrastructure.Resilience;

namespace Ledger.Api.Resilience;

/// <summary>
/// Proteções da borda HTTP contra dependência lenta ou fora do ar.
/// </summary>
/// <remarks>
/// Complementa <see cref="LedgerResilienceOptions"/>, que cobre o acesso ao
/// banco. Aqui está o que só faz sentido no nível da requisição: quanto ela pode
/// durar, quando parar de tentar uma dependência que não responde, e quanto de
/// capacidade cada tipo de operação pode consumir.
/// </remarks>
public sealed record ApiResilienceOptions
{
    /// <summary>Seção da configuração de onde estes valores são lidos.</summary>
    public const string SectionName = "Ledger:Resilience";

    /// <summary>
    /// Duração máxima de uma requisição inteira.
    /// </summary>
    /// <remarks>
    /// Rede de segurança acima dos limites de conexão e de comando, não
    /// substituto deles. Existe para o caso que nenhum limite menor pega.
    /// </remarks>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Proporção de falhas que abre o circuito, entre 0 e 1.
    /// </summary>
    public double BreakerFailureRatio { get; init; } = 0.5;

    /// <summary>
    /// Mínimo de requisições na janela antes de o circuito poder abrir.
    /// </summary>
    /// <remarks>
    /// Sem mínimo, duas falhas seguidas num sistema quase parado — 100% de
    /// falha — abririam o circuito por ruído estatístico.
    /// </remarks>
    public int BreakerMinimumThroughput { get; init; } = 8;

    /// <summary>Janela em que as falhas são contadas.</summary>
    public TimeSpan BreakerSamplingDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Quanto tempo o circuito fica aberto antes de testar a dependência.</summary>
    /// <remarks>
    /// Tempo para o banco se recuperar sem receber a carga que o derrubou. Curto
    /// demais e o circuito reabre sob tráfego cheio; longo demais e o sistema
    /// continua recusando depois de o banco já ter voltado.
    /// </remarks>
    public TimeSpan BreakerBreakDuration { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Valor sugerido ao chamador em <c>Retry-After</c> numa falha de dependência.</summary>
    public TimeSpan RetryAfter { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Operações de escrita simultâneas aceitas por instância.</summary>
    public int WriteConcurrency { get; init; } = 200;

    /// <summary>Operações de leitura simultâneas aceitas por instância.</summary>
    /// <remarks>
    /// Maior que a de escrita porque a leitura é mais barata e mais frequente
    /// ([PR-02](../../../docs/premissas.md) assume cinco consultas para cada
    /// registro).
    /// </remarks>
    public int ReadConcurrency { get; init; } = 400;

    /// <summary>Tempo que o processo espera as requisições em voo ao encerrar.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Lê as opções da configuração, preservando os padrões do que faltar.</summary>
    public static ApiResilienceOptions From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var secao = configuration.GetSection(SectionName);
        var padrao = new ApiResilienceOptions();

        return new ApiResilienceOptions
        {
            RequestTimeout = ConfigurationValues.ReadDuration(secao, nameof(RequestTimeout), padrao.RequestTimeout),
            BreakerFailureRatio = ConfigurationValues.ReadFraction(secao, nameof(BreakerFailureRatio), padrao.BreakerFailureRatio),
            BreakerMinimumThroughput = ConfigurationValues.ReadInt32(secao, nameof(BreakerMinimumThroughput), padrao.BreakerMinimumThroughput),
            BreakerSamplingDuration = ConfigurationValues.ReadDuration(secao, nameof(BreakerSamplingDuration), padrao.BreakerSamplingDuration),
            BreakerBreakDuration = ConfigurationValues.ReadDuration(secao, nameof(BreakerBreakDuration), padrao.BreakerBreakDuration),
            RetryAfter = ConfigurationValues.ReadDuration(secao, nameof(RetryAfter), padrao.RetryAfter),
            WriteConcurrency = ConfigurationValues.ReadInt32(secao, nameof(WriteConcurrency), padrao.WriteConcurrency),
            ReadConcurrency = ConfigurationValues.ReadInt32(secao, nameof(ReadConcurrency), padrao.ReadConcurrency),
            ShutdownTimeout = ConfigurationValues.ReadDuration(secao, nameof(ShutdownTimeout), padrao.ShutdownTimeout),
        };
    }
}
