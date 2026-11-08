using System.Security.Cryptography;
using System.Text;
using Ledger.Domain;

namespace Ledger.Application.Observability;

/// <summary>
/// Referência opaca e estável de uma conta, para uso em log.
/// </summary>
/// <remarks>
/// <para>
/// O log não carrega o identificador da conta ([ADR-0009](../../../docs/adr/0009-protecao-de-dados-sensiveis.md)):
/// ele é replicado para um agregador, lido por mais gente e retido por menos
/// tempo que o razão, e fica fora do controle de acesso do banco. Mas um log que
/// diz "alguma conta divergiu" é inútil num incidente.
/// </para>
/// <para>
/// Esta referência resolve os dois lados. É <b>determinística</b> — a mesma conta
/// produz sempre a mesma referência, então várias linhas se correlacionam — e
/// <b>unidirecional</b>: de uma referência não se chega ao identificador. Quem
/// tem o identificador (o suporte, com o número da conta do cliente) calcula a
/// referência e procura no log; quem só tem o log não consegue o caminho inverso.
/// </para>
/// <para>
/// Não é uma defesa contra quem pode testar todos os identificadores possíveis,
/// e não se propõe a ser: é redução de exposição acidental. Um UUID v7 tem 74
/// bits aleatórios, o que torna a enumeração inviável na prática, mas a proteção
/// real continua sendo o controle de acesso ao agregador de logs.
/// </para>
/// </remarks>
public static class LogRef
{
    /// <summary>Tamanho da referência, em caracteres hexadecimais.</summary>
    public const int Length = 12;

    /// <summary>Calcula a referência de log de uma conta.</summary>
    public static string For(AccountId accountId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(accountId.Value.ToString("N")));
        return Convert.ToHexStringLower(hash)[..Length];
    }

    /// <summary>Calcula a referência de log de um identificador de conta.</summary>
    public static string For(Guid accountId) => For(AccountId.From(accountId));
}
