using System.ComponentModel.DataAnnotations;

namespace Ledger.Api.Security;

/// <summary>
/// Configuração de autenticação por JWT.
/// </summary>
/// <remarks>
/// <para>
/// Não existe modo "sem autenticação": se a chave não estiver configurada, a
/// aplicação <b>não sobe</b>. Um padrão aberto "para facilitar o desenvolvimento"
/// é exatamente a configuração que chega a produção por esquecimento, e uma API
/// de razão financeiro que sobe aberta sem avisar é pior que uma que não sobe.
/// </para>
/// <para>
/// A chave é simétrica (HS256) por simplicidade de execução local: quem avalia o
/// desafio precisa subir tudo com um comando, sem um servidor de identidade. É a
/// concessão consciente deste projeto — ver o ADR-0016 para o que muda em produção
/// (chave assimétrica e validação por JWKS do emissor corporativo).
/// </para>
/// </remarks>
public sealed class LedgerAuthOptions
{
    /// <summary>Seção de configuração.</summary>
    public const string Section = "Ledger:Auth";

    /// <summary>Tamanho mínimo da chave, em bytes. 256 bits, o mínimo do HS256.</summary>
    public const int MinimumKeyBytes = 32;

    /// <summary>Emissor esperado no token (<c>iss</c>).</summary>
    [Required(AllowEmptyStrings = false)]
    public string Issuer { get; init; } = string.Empty;

    /// <summary>Destinatário esperado no token (<c>aud</c>).</summary>
    [Required(AllowEmptyStrings = false)]
    public string Audience { get; init; } = string.Empty;

    /// <summary>Chave de assinatura, em Base64.</summary>
    [Required(AllowEmptyStrings = false)]
    public string SigningKey { get; init; } = string.Empty;

    /// <summary>Tolerância de relógio entre emissor e API.</summary>
    public TimeSpan ClockSkew { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Decodifica a chave, validando o tamanho.</summary>
    public byte[] DecodeKey()
    {
        byte[] bytes;

        try
        {
            bytes = Convert.FromBase64String(SigningKey);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                $"{Section}:SigningKey precisa estar em Base64.");
        }

        return bytes.Length >= MinimumKeyBytes
            ? bytes
            : throw new InvalidOperationException(
                $"{Section}:SigningKey tem {bytes.Length} bytes; o mínimo é {MinimumKeyBytes}.");
    }
}
