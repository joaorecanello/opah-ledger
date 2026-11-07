// Ledger.Api - host HTTP.
//
// Estrutura minima executavel (etapa E03 do plano). Os endpoints de negocio
// entram em E08, sobre os casos de uso de E06/E07. O que existe aqui hoje e
// so o que prova que o processo sobe e responde.

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// RFC 9457: todo erro da API responde ProblemDetails, inclusive os gerados
// pelo proprio pipeline (404, 405, 415). Definido ja no scaffold para que
// nenhum endpoint nasca com formato de erro proprio.
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseStatusCodePages();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Liveness: "o processo esta vivo", sem tocar em nenhuma dependencia.
// A readiness ("as dependencias respondem") entra em E11 e e deliberadamente
// um endpoint separado: confundir as duas faz o orquestrador matar instancias
// que apenas esperam o banco, transformando falha parcial em queda total.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
   .WithName("Liveness")
   .WithSummary("Indica que o processo esta no ar.");

await app.RunAsync().ConfigureAwait(false);

/// <summary>
/// Exposto para que os testes de integracao (E06) possam subir a API em
/// memoria com WebApplicationFactory sem replicar a configuracao do host.
/// </summary>
public partial class Program;
