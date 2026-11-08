using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ledger.Domain;

namespace Ledger.Application.Movements;

/// <summary>
/// Impressão do conteúdo de uma requisição de registro de movimentação.
/// </summary>
/// <remarks>
/// <para>
/// Serve a uma decisão só: quando a mesma chave de idempotência chega duas
/// vezes, distinguir <b>repetição legítima</b> — mesmo conteúdo, devolve a
/// resposta original — de <b>erro do chamador</b>: conteúdo diferente, devolve
/// <c>409</c>.
/// </para>
/// <para>
/// Sem isto não haveria como saber qual dos dois casos é, e as duas
/// interpretações possíveis movimentam dinheiro. Adivinhar intenção em sistema
/// financeiro é inaceitável.
/// </para>
/// <para>
/// A impressão cobre apenas os campos que definem o <i>fato</i>: conta, tipo,
/// valor, moeda e o instante de negócio. Deliberadamente <b>não</b> cobre o
/// instante de registro nem a correlação, que mudam a cada tentativa por
/// natureza — incluí-los faria toda repetição parecer conteúdo divergente, e o
/// mecanismo recusaria com <c>409</c> exatamente o retry que deveria aceitar.
/// </para>
/// <para>
/// Vive na camada de aplicação, e não na de persistência, porque essa escolha de
/// campos <b>é</b> a definição de "a mesma requisição". Mudá-la muda quando o
/// sistema responde <c>200</c> e quando responde <c>409</c> — é regra de
/// negócio, não detalhe de armazenamento. A tabela só guarda o resultado.
/// </para>
/// </remarks>
public static class RequestFingerprint
{
    /// <summary>Tamanho da impressão em caracteres hexadecimais.</summary>
    public const int Length = 64;

    /// <summary>Calcula a impressão dos campos que definem o fato.</summary>
    public static string Of(
        AccountId accountId,
        MovementType type,
        Money amount,
        DateTimeOffset occurredAt,
        string? externalReference)
    {
        // Forma canônica: separador que não ocorre nos campos, cultura
        // invariante e instante em UTC. Sem canonicalização, o mesmo fato
        // produziria impressões diferentes conforme a cultura do processo ou o
        // fuso de quem chamou.
        var canonico = string.Join(
            '\u001f',
            accountId.Value.ToString("N", CultureInfo.InvariantCulture),
            type.ToString(),
            amount.Amount.ToString("F4", CultureInfo.InvariantCulture),
            amount.Currency.Code,
            occurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            externalReference ?? string.Empty);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonico));

        return Convert.ToHexStringLower(digest);
    }

    /// <summary>Calcula a impressão a partir de uma movimentação já criada.</summary>
    public static string Of(Movement movement)
    {
        ArgumentNullException.ThrowIfNull(movement);

        return Of(
            movement.AccountId,
            movement.Type,
            movement.Amount,
            movement.OccurredAt,
            movement.ExternalReference);
    }
}
