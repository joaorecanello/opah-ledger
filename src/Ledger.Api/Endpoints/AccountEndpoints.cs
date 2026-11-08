using Ledger.Api.Contracts;
using Ledger.Api.Resilience;
using Ledger.Application.Accounts;
using Ledger.Application.Balances;

namespace Ledger.Api.Endpoints;

/// <summary>Endpoints de conta e de posição consolidada.</summary>
public static class AccountEndpoints
{
    /// <summary>Registra as rotas de conta no grupo informado.</summary>
    public static RouteGroupBuilder MapAccounts(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/accounts", AbrirConta)
            .WithName("AbrirConta")
            .WithSummary("Provisiona uma conta.")
            .WithDescription(
                "Existe para que o projeto seja executável sem depender do "
                + "contexto de Clientes. O cadastro de contas não pertence ao "
                + "razão; ver a premissa PR-07 na documentação.")
            .RequireRateLimiting(RateLimitPolicies.Write)
            .Produces<AccountResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/accounts/{accountId:guid}/balance", ConsultarSaldo)
            .WithName("ConsultarSaldo")
            .WithSummary("Devolve a posição consolidada da conta num instante.")
            .WithDescription(
                "O parâmetro asOf é o instante de corte, inclusivo, e usa o tempo "
                + "de negócio da movimentação. Ausente significa agora. Instante "
                + "futuro é recusado, porque movimentações ainda podem ser "
                + "registradas para aquele período. A resposta informa de onde o "
                + "número veio.")
            .RequireRateLimiting(RateLimitPolicies.Read)
            .Produces<BalanceResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return group;
    }

    private static async Task<IResult> AbrirConta(
        OpenAccountRequest request,
        OpenAccountHandler handler,
        CancellationToken cancellationToken)
    {
        var resultado = await handler.HandleAsync(
            new OpenAccountCommand(request.CustomerId, request.Currency),
            cancellationToken);

        if (resultado.Status != OpenAccountStatus.Opened)
        {
            return LedgerProblems.Rejected(resultado.ErrorCode, resultado.Detail);
        }

        var corpo = new AccountResponse(
            resultado.AccountId!.Value,
            request.CustomerId,
            request.Currency.ToUpperInvariant(),
            "Active");

        return Results.Created($"/api/v1/accounts/{corpo.AccountId}/balance", corpo);
    }

    private static async Task<IResult> ConsultarSaldo(
        Guid accountId,
        string? asOf,
        GetBalanceHandler handler,
        CancellationToken cancellationToken)
    {
        // Recebido como texto e convertido aqui, em lugar de deixar o framework
        // ligar um DateTimeOffset?. A ligação automática falha com uma mensagem
        // genérica, e o erro real que as pessoas cometem — não codificar o '+'
        // do deslocamento — merece uma resposta que diga isso.
        DateTimeOffset? corte = null;

        if (!string.IsNullOrWhiteSpace(asOf))
        {
            if (!DateTimeOffset.TryParse(
                    asOf,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var convertido))
            {
                return LedgerProblems.InvalidAsOf(asOf);
            }

            corte = convertido;
        }

        var resultado = await handler.HandleAsync(
            new GetBalanceQuery(accountId, corte),
            cancellationToken);

        return resultado.Status switch
        {
            GetBalanceStatus.Found => Results.Ok(new BalanceResponse(
                accountId,
                resultado.AsOf!.Value,
                resultado.Balance!.Value,
                resultado.Currency!,
                resultado.MovementCount!.Value,
                resultado.Source!.Value.ToString(),
                resultado.Stale!.Value)),

            GetBalanceStatus.AccountNotFound =>
                LedgerProblems.AccountNotFound(resultado.Detail),

            // Sem braço padrão: se um desfecho novo for adicionado ao enum, o
            // compilador não avisa, mas este caminho responde 400 com o detalhe
            // em lugar de devolver 200 com corpo vazio.
            _ => LedgerProblems.Rejected(resultado.ErrorCode, resultado.Detail),
        };
    }
}
