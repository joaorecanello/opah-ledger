using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Ledger.Api.Observability;

/// <summary>
/// Dá a cada requisição um identificador de correlação, e o espalha por log,
/// trace, resposta e registro persistido.
/// </summary>
/// <remarks>
/// <para>
/// É o que permite, meses depois, recuperar a chamada que originou um lançamento
/// contestado: o mesmo identificador está no log da requisição, no trace, no
/// cabeçalho que o chamador recebeu e na própria movimentação
/// (<c>movements.correlation_id</c>). O log sai da retenção quente; o fato não.
/// </para>
/// <para>
/// <b>O valor vindo do cliente é entrada hostil.</b> Ele acaba em log, e um
/// cabeçalho com quebra de linha forjaria entradas falsas num log em texto, ou
/// quebraria o parser de um log estruturado. Por isso só passa o que casa com
/// um alfabeto estreito e tem tamanho limitado; qualquer outra coisa é
/// <b>descartada e substituída</b>, não corrigida — adivinhar o que o chamador
/// "quis dizer" sobre um valor malformado é pior que ignorá-lo.
/// </para>
/// <para>
/// Quando o cliente não manda nenhum, usa-se o identificador do trace atual, e
/// não um GUID novo: assim o identificador de correlação e o trace coincidem, e
/// quem tem um encontra o outro sem tabela de tradução.
/// </para>
/// </remarks>
public sealed partial class CorrelationMiddleware(
    RequestDelegate next,
    ILogger<CorrelationMiddleware> logger)
{
    /// <summary>Cabeçalho que transporta o identificador, na ida e na volta.</summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>Tamanho máximo aceito, igual ao limite da coluna persistida.</summary>
    public const int MaxLength = 128;

    /// <summary>Chave em <c>HttpContext.Items</c>.</summary>
    internal const string ItemKey = "ledger.correlation-id";

    [GeneratedRegex(@"^[A-Za-z0-9._:\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Aceitavel();

    /// <summary>Executa o middleware.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var identificador = Resolver(context.Request.Headers[HeaderName].ToString());

        context.Items[ItemKey] = identificador;

        // OnStarting e não uma atribuição direta: o cabeçalho precisa sair mesmo
        // quando um tratador de exceção reescreve a resposta, e antes de o corpo
        // começar a ser enviado.
        context.Response.OnStarting(
            static estado =>
            {
                var (resposta, valor) = ((HttpResponse, string))estado;
                resposta.Headers[HeaderName] = valor;
                return Task.CompletedTask;
            },
            (context.Response, identificador));

        Activity.Current?.SetTag("ledger.correlation_id", identificador);

        // O escopo faz o identificador aparecer em TODA linha de log escrita
        // durante a requisição, inclusive as de camadas que nunca ouviram falar
        // dele.
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = identificador }))
        {
            await next(context).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Devolve o valor recebido, se for aceitável, ou um identificador novo.
    /// </summary>
    public static string Resolver(string? recebido)
    {
        var candidato = recebido?.Trim();

        return !string.IsNullOrEmpty(candidato)
            && candidato.Length <= MaxLength
            && Aceitavel().IsMatch(candidato)
            ? candidato
            : (Activity.Current?.TraceId.ToString() ?? Guid.CreateVersion7().ToString("N"));
    }
}

/// <summary>Acesso ao identificador de correlação da requisição.</summary>
public static class CorrelationExtensions
{
    /// <summary>
    /// Devolve o identificador de correlação resolvido para esta requisição.
    /// </summary>
    /// <remarks>
    /// Se o middleware não rodou — um teste que monta o contexto à mão —, devolve o
    /// identificador de rastreio do ASP.NET, que nunca é vazio: o campo é
    /// obrigatório no domínio, e uma movimentação sem ele não pode ser criada.
    /// </remarks>
    public static string GetCorrelationId(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items[CorrelationMiddleware.ItemKey] as string ?? context.TraceIdentifier;
    }
}
