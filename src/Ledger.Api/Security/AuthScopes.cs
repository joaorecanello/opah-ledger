namespace Ledger.Api.Security;

/// <summary>Escopos e claims que a API reconhece.</summary>
public static class AuthScopes
{
    /// <summary>Claim com os escopos concedidos, separados por espaço (RFC 9068).</summary>
    public const string ScopeClaim = "scope";

    /// <summary>Claim que restringe o token às contas de um cliente.</summary>
    public const string CustomerClaim = "customer_id";

    /// <summary>Consultar saldo e movimentações.</summary>
    public const string Read = "ledger:read";

    /// <summary>Provisionar contas e registrar movimentações.</summary>
    public const string Write = "ledger:write";

    /// <summary>
    /// Alcançar contas de qualquer cliente. Sem ele, o token precisa trazer
    /// <see cref="CustomerClaim"/>.
    /// </summary>
    public const string CrossCustomer = "ledger:cross-customer";

    /// <summary>Política que exige <see cref="Read"/>.</summary>
    public const string ReadPolicy = "ledger-read";

    /// <summary>Política que exige <see cref="Write"/>.</summary>
    public const string WritePolicy = "ledger-write";
}
