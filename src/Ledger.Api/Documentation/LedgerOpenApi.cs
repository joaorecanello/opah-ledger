using Ledger.Api.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Ledger.Api.Documentation;

/// <summary>
/// Descrição do contrato HTTP: informações gerais e o esquema de segurança.
/// </summary>
public static class LedgerOpenApi
{
    /// <summary>Nome do esquema de segurança no documento.</summary>
    public const string BearerScheme = "Bearer";

    /// <summary>Registra o documento OpenAPI com os transformadores do razão.</summary>
    public static IServiceCollection AddLedgerOpenApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer(DescreverDocumento);
            options.AddOperationTransformer(DescreverOperacao);
        });

        return services;
    }

    private static Task DescreverDocumento(
        OpenApiDocument documento,
        OpenApiDocumentTransformerContext contexto,
        CancellationToken cancellationToken)
    {
        documento.Info = new OpenApiInfo
        {
            Title = "Ledger API",
            Version = "v1",
            Description =
                "Razão contábil: registra créditos e débitos e responde a posição "
                + "consolidada de saldo em qualquer instante.\n\n"
                + "**Autenticação.** Toda rota de negócio exige `Authorization: Bearer <jwt>`. "
                + "Em desenvolvimento, emita um token com "
                + "`dotnet run --project tools/Ledger.DevToken -- --cross-customer` e cole-o em "
                + "*Authorize*. Escopos: `ledger:read`, `ledger:write`.\n\n"
                + "**Idempotência.** O registro de movimentação exige `Idempotency-Key`. "
                + "Repetir com a mesma chave e o mesmo conteúdo devolve `200` com a resposta "
                + "original; mesma chave com conteúdo diferente devolve `409`.\n\n"
                + "**Erros** seguem a RFC 9457 (`application/problem+json`).",
        };

        documento.Components ??= new OpenApiComponents();
        documento.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        documento.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Token JWT com os escopos `ledger:read` e/ou `ledger:write`.",
        };

        return Task.CompletedTask;
    }

    private static Task DescreverOperacao(
        OpenApiOperation operacao,
        OpenApiOperationTransformerContext contexto,
        CancellationToken cancellationToken)
    {
        var metadados = contexto.Description.ActionDescriptor.EndpointMetadata;

        // Só as rotas com política de autorização exigem o esquema.
        if (metadados.OfType<IAuthorizeData>().Any())
        {
            operacao.Security ??= [];
            operacao.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(BearerScheme, contexto.Document)] = [],
            });
        }

        // O cabeçalho vem do código, não de um parâmetro de método (é lido do
        // HttpContext); sem isto a interface não teria onde preenchê-lo.
        if (contexto.Description.HttpMethod == "POST"
            && contexto.Description.RelativePath?.EndsWith("/movements", StringComparison.Ordinal) == true)
        {
            operacao.Parameters ??= [];
            operacao.Parameters.Add(new OpenApiParameter
            {
                Name = MovementEndpoints.IdempotencyKeyHeader,
                In = ParameterLocation.Header,
                Required = true,
                Description = "Chave que torna segura a repetição do pedido. Reutilizá-la com "
                            + "conteúdo diferente devolve 409.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            });
        }

        return Task.CompletedTask;
    }
}
