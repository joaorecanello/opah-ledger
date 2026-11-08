using System.Net.Http.Headers;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ledger.Integration.Tests;

/// <summary>Emite tokens para os testes, com controle sobre cada defeito possível.</summary>
internal static class TestTokens
{
    public const string Issuer = "ledger-teste";
    public const string Audience = "ledger-api-teste";

    /// <summary>Chave dos testes: 64 bytes (512 bits), em Base64 — serve também ao HS512 dos testes de confusão de algoritmo.</summary>
    public static readonly string Key = Convert.ToBase64String(
        System.Text.Encoding.UTF8.GetBytes("chave-somente-para-testes-automatizados-0123456789-abcdefghijklmnop"));

    public static string Create(
        string scopes = "ledger:read ledger:write",
        Guid? customer = null,
        bool crossCustomer = false,
        string? issuer = Issuer,
        string? audience = Audience,
        string? key = null,
        DateTime? expires = null,
        DateTime? notBefore = null,
        string algorithm = SecurityAlgorithms.HmacSha256)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = "cliente-de-teste",
            ["scope"] = crossCustomer ? scopes + " ledger:cross-customer" : scopes,
        };

        if (customer is { } c)
        {
            claims["customer_id"] = c.ToString();
        }

        var descritor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            NotBefore = notBefore,
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Convert.FromBase64String(key ?? Key)),
                algorithm),
        };

        return new JsonWebTokenHandler().CreateToken(descritor);
    }

    public static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);

    /// <summary>Token de um cliente restrito às contas de um titular.</summary>
    public static string ForCustomer(Guid customer, string scopes = "ledger:read ledger:write") =>
        Create(scopes, customer);

    /// <summary>Token com alcance sobre contas de qualquer cliente.</summary>
    public static string CrossCustomer(string scopes = "ledger:read ledger:write") =>
        Create(scopes, crossCustomer: true);
}
