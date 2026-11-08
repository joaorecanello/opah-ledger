using Ledger.Api.Contracts;
using Ledger.Api.Observability;
using Ledger.Api.Resilience;
using Ledger.Application.Accounts;
using Ledger.Application.Movements;
using Ledger.Domain;

namespace Ledger.Api.Endpoints;

/// <summary>Endpoints de movimentação.</summary>
public static class MovementEndpoints
{
    /// <summary>Cabeçalho que transporta a chave de idempotência.</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>Registra as rotas de movimentação no grupo informado.</summary>
    public static RouteGroupBuilder MapMovements(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapPost("/accounts/{accountId:guid}/movements", Registrar)
            .WithName("RegistrarMovimentacao")
            .WithSummary("Registra uma movimentação na conta.")
            .WithDescription(
                $"O cabeçalho {IdempotencyKeyHeader} é obrigatório. Repetir a "
                + "requisição com a mesma chave e o mesmo conteúdo devolve 200 "
                + "com a resposta original, não registra de novo. A mesma chave "
                + "com conteúdo diferente devolve 409.")
            .RequireRateLimiting(RateLimitPolicies.Write)
            .Produces<MovementResponse>(StatusCodes.Status201Created)
            .Produces<MovementResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("/accounts/{accountId:guid}/movements/{movementId:guid}", Buscar)
            .WithName("BuscarMovimentacao")
            .WithSummary("Devolve uma movimentação registrada.")
            .RequireRateLimiting(RateLimitPolicies.Read)
            .Produces<MovementResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return group;
    }

    private static async Task<IResult> Registrar(
        Guid accountId,
        RegisterMovementRequest request,
        HttpContext context,
        RegisterMovementHandler handler,
        GetMovementHandler leitura,
        CancellationToken cancellationToken)
    {
        var chave = context.Request.Headers[IdempotencyKeyHeader].ToString();

        if (string.IsNullOrWhiteSpace(chave))
        {
            // Falha antes de tocar no domínio: sem a chave, nem faz sentido
            // tentar. É o único campo obrigatório que não vem no corpo.
            return LedgerProblems.MissingIdempotencyKey(IdempotencyKeyHeader);
        }

        if (!Enum.TryParse<MovementType>(request.Type, ignoreCase: true, out var tipo)
            || tipo is not (MovementType.Credit or MovementType.Debit))
        {
            return LedgerProblems.UnknownMovementType(request.Type);
        }

        // Resolvida e validada pelo CorrelationMiddleware, que também a espalha por
        // log, trace e resposta. O campo é obrigatório no domínio e é o que permite,
        // meses depois, recuperar a chamada que originou um lançamento contestado.
        var correlacao = context.GetCorrelationId();

        var resultado = await handler.HandleAsync(
            new RegisterMovementCommand(
                accountId,
                tipo,
                request.Amount,
                request.Currency,
                request.OccurredAt,
                chave,
                correlacao,
                request.ExternalReference),
            cancellationToken);

        switch (resultado.Status)
        {
            case RegisterMovementStatus.Registered:
                // Montada a partir da movimentação gravada, não do pedido: o
                // razão normaliza a escala do valor e a precisão do instante, e
                // a resposta precisa dizer o que ficou guardado.
                var criada = Montar(resultado.Movement!);

                return Results.Created(
                    $"/api/v1/accounts/{accountId}/movements/{criada.MovementId}",
                    criada);

            case RegisterMovementStatus.Replayed:
                // A resposta do replay é lida do banco, e não montada a partir
                // deste pedido. A impressão do conteúdo garante que os dois são
                // o mesmo fato, mas a formatação pode diferir — "100.0" e
                // "100.00" são a mesma quantia e dois JSON diferentes. Ler o
                // armazenado faz a resposta ser idêntica à original, que é o que
                // a idempotência promete.
                var original = await leitura.HandleAsync(
                    new GetMovementQuery(accountId, resultado.MovementId!.Value),
                    cancellationToken);

                return original is null
                    ? LedgerProblems.MovementNotFound()
                    : Results.Ok(Montar(original));

            case RegisterMovementStatus.ConflictingPayload:
                return LedgerProblems.IdempotencyConflict(resultado.Detail);

            case RegisterMovementStatus.AccountNotFound:
                return LedgerProblems.AccountNotFound(resultado.Detail);

            default:
                return LedgerProblems.Rejected(resultado.ErrorCode, resultado.Detail);
        }
    }

    private static async Task<IResult> Buscar(
        Guid accountId,
        Guid movementId,
        GetMovementHandler handler,
        CancellationToken cancellationToken)
    {
        var movimentacao = await handler.HandleAsync(
            new GetMovementQuery(accountId, movementId),
            cancellationToken);

        return movimentacao is null
            ? LedgerProblems.MovementNotFound()
            : Results.Ok(Montar(movimentacao));
    }

    private static MovementResponse Montar(Movement movimentacao) =>
        new(
            movimentacao.Id.Value,
            movimentacao.AccountId.Value,
            movimentacao.Type.ToString(),
            movimentacao.Amount.Amount,
            movimentacao.Amount.Currency.Code,
            movimentacao.OccurredAt,
            movimentacao.PostedAt,
            movimentacao.ExternalReference);
}
