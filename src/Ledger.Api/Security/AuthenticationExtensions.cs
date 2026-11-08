using Ledger.Application.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Ledger.Api.Security;

/// <summary>Registro de autenticação e autorização.</summary>
public static class AuthenticationExtensions
{
    /// <summary>
    /// Registra JWT Bearer, as políticas por escopo e o chamador dos casos de uso.
    /// </summary>
    public static IServiceCollection AddLedgerSecurity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Lida pelo contêiner e validada no start: a aplicação não sobe sem chave, e
        // a configuração do host de teste (que chega depois) é respeitada.
        services.AddOptions<LedgerAuthOptions>()
            .BindConfiguration(LedgerAuthOptions.Section)
            .ValidateDataAnnotations()
            .Validate(TryDecode, "Ledger:Auth:SigningKey inválida: Base64 com no mínimo 32 bytes.")
            .ValidateOnStart();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<LedgerAuthOptions>>((jwt, auth) =>
            {
                var o = auth.Value;

                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = o.Issuer,
                    ValidateAudience = true,
                    ValidAudience = o.Audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(o.DecodeKey()),

                    // Um só algoritmo aceito. Sem a lista, o validador escolhe pelo
                    // cabeçalho do token — a porta dos ataques de confusão de
                    // algoritmo.
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = o.ClockSkew,
                };
            });

        services.AddHttpContextAccessor();
        services.AddScoped<ICallerAccess, HttpCallerAccess>();
        services.AddSingleton<IAuthorizationHandler, ScopeAuthorizationHandler>();

        services.AddAuthorizationBuilder()
            .AddPolicy(
                AuthScopes.ReadPolicy,
                p => p.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement(AuthScopes.Read)))
            .AddPolicy(
                AuthScopes.WritePolicy,
                p => p.RequireAuthenticatedUser().AddRequirements(new ScopeRequirement(AuthScopes.Write)))

            // Negar é o padrão: rota sem política explícita exige, no mínimo, um
            // token válido. Rotas públicas (saúde) precisam declarar AllowAnonymous.
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services;
    }

    private static bool TryDecode(LedgerAuthOptions options)
    {
        try
        {
            _ = options.DecodeKey();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
