// Ledger.Api — host HTTP.
//
// Estrutura mínima executável. Os endpoints de negócio entram sobre os casos de
// uso de registro de movimentação e de consulta de saldo; o que existe aqui hoje
// é só o que prova que o processo sobe e responde.
//
// Decisões de desenho em docs/adr/.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// RFC 9457: todo erro da API responde ProblemDetails, inclusive os gerados pelo
// próprio pipeline (404, 405, 415). Definido já na estrutura inicial para que
// nenhum endpoint nasça com formato de erro próprio.
builder.Services.AddProblemDetails();

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
   .WithSummary("Indica que o processo está no ar.");

await app.RunAsync().ConfigureAwait(false);

/// <summary>
/// Exposto para que os testes de integração possam subir a API em memória com
/// WebApplicationFactory sem replicar a configuração do host.
/// </summary>
public partial class Program;
