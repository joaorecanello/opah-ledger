using System.Security.Claims;
using Ledger.Application.Security;
using Microsoft.AspNetCore.Authorization;

namespace Ledger.Api.Security;

/// <summary>Exige um escopo e uma identidade de acesso definida.</summary>
/// <param name="Scope">Escopo exigido.</param>
public sealed record ScopeRequirement(string Scope) : IAuthorizationRequirement;

/// <summary>
/// Atende <see cref="ScopeRequirement"/>.
/// </summary>
/// <remarks>
/// Além do escopo pedido, exige que o token diga <b>a que contas</b> ele chega:
/// ou traz <c>customer_id</c>, ou traz o escopo <c>ledger:cross-customer</c>. Um
/// token com <c>ledger:read</c> e nada mais não é recusado por falta de escopo —
/// é recusado por não ter alcance definido. O contrário (tratá-lo como irrestrito)
/// seria o erro clássico: ausência de restrição virando permissão total.
/// </remarks>
public sealed class ScopeAuthorizationHandler : AuthorizationHandler<ScopeRequirement>
{
    /// <summary>Escopos concedidos ao usuário.</summary>
    public static HashSet<string> Scopes(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return user.FindAll(AuthScopes.ScopeClaim)
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ScopeRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (Scopes(context.User).Contains(requirement.Scope)
            && CallerAccess.Resolve(context.User) is not null)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Traduz o token no escopo de acesso do caso de uso.</summary>
public sealed class HttpCallerAccess(IHttpContextAccessor accessor) : ICallerAccess
{
    /// <inheritdoc/>
    public AccessScope Scope =>
        CallerAccess.Resolve(accessor.HttpContext?.User)
        ?? throw new InvalidOperationException(
            "Caso de uso acionado sem um chamador com alcance definido. "
            + "A política de autorização deveria ter barrado a requisição.");
}

internal static class CallerAccess
{
    /// <summary>O alcance do token, ou <c>null</c> quando ele não define nenhum.</summary>
    public static AccessScope? Resolve(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated is not true)
        {
            return null;
        }

        // O cliente vence o escopo irrestrito: se o token traz as duas coisas, vale
        // a mais restritiva. Na dúvida, o menor alcance.
        if (Guid.TryParse(user.FindFirstValue(AuthScopes.CustomerClaim), out var cliente)
            && cliente != Guid.Empty)
        {
            return AccessScope.ForCustomer(cliente);
        }

        return ScopeAuthorizationHandler.Scopes(user).Contains(AuthScopes.CrossCustomer)
            ? AccessScope.Unrestricted
            : null;
    }
}
