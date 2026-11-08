using Ledger.Api.Endpoints;
using Ledger.Infrastructure;
using Ledger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

// Ledger.Api — host HTTP do razão.
//
// Dois recursos, que correspondem aos dois requisitos funcionais do enunciado:
// registrar movimentação e consultar a posição consolidada. Mais um endpoint
// mínimo de provisionamento de conta, que existe para o projeto ser executável
// sem depender do contexto de Clientes (premissa PR-07).
//
// Decisões de desenho em docs/adr/.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// RFC 9457: todo erro da API responde ProblemDetails, inclusive os gerados pelo
// próprio pipeline (404, 405, 415, corpo malformado). Definido no host para que
// nenhum endpoint nasça com formato de erro próprio.
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = contexto =>
    {
        // Correlaciona o erro com o log e com o trace. Sem isto, quem recebe um
        // 500 não tem o que informar ao suporte além do horário.
        contexto.ProblemDetails.Extensions["traceId"] =
            System.Diagnostics.Activity.Current?.Id ?? contexto.HttpContext.TraceIdentifier;
        contexto.ProblemDetails.Instance ??= contexto.HttpContext.Request.Path;
    });

builder.Services.AddLedger();

var app = builder.Build();

app.UseStatusCodePages();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Liveness: "o processo está vivo", sem tocar em nenhuma dependência.
//
// A readiness ("as dependências respondem") é um endpoint separado de propósito:
// confundir as duas faz o orquestrador matar instâncias que apenas esperam o
// banco voltar, convertendo falha parcial em queda total.
// Ver docs/adr/0008-resiliencia-e-degradacao-declarada.md.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
   .WithName("Liveness")
   .WithSummary("Indica que o processo está no ar.")
   .ExcludeFromDescription();

// Versionamento no caminho: explícito, cacheável e trivial de rotear.
var v1 = app.MapGroup("/api/v1").WithTags("Ledger v1");
v1.MapAccounts();
v1.MapMovements();

await AplicarMigrationsSeSolicitadoAsync(app).ConfigureAwait(false);

await app.RunAsync().ConfigureAwait(false);

static async Task AplicarMigrationsSeSolicitadoAsync(WebApplication app)
{
    // Desligado por padrão, e isso é deliberado.
    //
    // Aplicar migration no start é conveniente em desenvolvimento e arriscado em
    // produção: várias instâncias subindo ao mesmo tempo disputam o lock de
    // migração, e uma migração que falha derruba o processo em lugar de falhar o
    // deploy. Em produção, migração é passo do pipeline.
    //
    // A conveniência fica disponível para quem quiser rodar local sem o comando
    // separado, mas é opt-in explícito.
    if (!app.Configuration.GetValue("Ledger:MigrateOnStartup", defaultValue: false))
    {
        return;
    }

    using var escopo = app.Services.CreateScope();
    var contexto = escopo.ServiceProvider.GetRequiredService<LedgerDbContext>();
    await contexto.Database.MigrateAsync().ConfigureAwait(false);
}

/// <summary>
/// Exposto para que os testes de integração possam subir a API em memória com
/// WebApplicationFactory sem replicar a configuração do host.
/// </summary>
public partial class Program;
