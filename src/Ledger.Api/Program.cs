using System.Threading.RateLimiting;
using Ledger.Api.Endpoints;
using Ledger.Api.Resilience;
using Ledger.Infrastructure;
using Ledger.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.RateLimiting;
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

// ---------------------------------------------------------------------------
// Resiliência
// ---------------------------------------------------------------------------
// Lidas do IConfiguration do contêiner, não de builder.Configuration.
//
// builder.Configuration só reflete as fontes adicionadas até este ponto do
// Program.cs. Qualquer fonte que o host acrescente depois — um teste, um
// provedor de segredos, um ConfigureAppConfiguration — fica invisível para quem
// leu antes. É a mesma armadilha que já havia custado a resolução da connection
// string (ver LedgerConnectionString): ler configuração cedo demais é ler uma
// configuração que ainda não é a final.
builder.Services.AddSingleton(provider =>
    ApiResilienceOptions.From(provider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<DependencyGuard>();

// Falha de dependência vira 503 com Retry-After, não 500. Medido antes de
// implementar: com o banco parado, escrita e leitura respondiam "erro do
// servidor", o que manda o chamador desistir de uma operação que repetiria com
// sucesso.
builder.Services.AddExceptionHandler<DependencyUnavailableExceptionHandler>();

// Prontidão: "posso receber tráfego agora?". Pergunta diferente de vitalidade —
// ver ReadinessCheck.
builder.Services.AddHealthChecks()
    .AddCheck<ReadinessCheck>(ReadinessCheck.Name, tags: [ReadinessCheck.Tag]);

// Isolamento de recursos entre escrita e leitura. Consulta lenta não pode
// esgotar a capacidade de registrar, que é a operação cuja falha custa dinheiro.
// Sem fila: o que excede o limite é recusado na hora, com 429 e Retry-After. Fila
// transformaria saturação em latência crescente, que é pior para quem espera e
// esconde o problema de quem opera.
builder.Services.AddRateLimiter(_ => { });
builder.Services.AddOptions<RateLimiterOptions>()
    .Configure<ApiResilienceOptions>((options, resiliencia) =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        options.AddConcurrencyLimiter(RateLimitPolicies.Write, limite =>
        {
            limite.PermitLimit = resiliencia.WriteConcurrency;
            limite.QueueLimit = 0;
        });

        options.AddConcurrencyLimiter(RateLimitPolicies.Read, limite =>
        {
            limite.PermitLimit = resiliencia.ReadConcurrency;
            limite.QueueLimit = 0;
        });

        options.OnRejected = async (contexto, cancellationToken) =>
        {
            var problemas = contexto.HttpContext.RequestServices
                .GetRequiredService<IProblemDetailsService>();

            var operacao = contexto.HttpContext.GetEndpoint()?.Metadata
                .GetMetadata<EnableRateLimitingAttribute>()?.PolicyName == RateLimitPolicies.Write
                ? "escrita"
                : "leitura";

            await DependencyProblems.WriteTooManyRequestsAsync(
                contexto.HttpContext, problemas, operacao).ConfigureAwait(false);
        };
    });

// Teto de duração da requisição inteira: rede de segurança acima dos limites de
// conexão e de comando, para o caso que nenhum limite menor pega. O status de
// estouro é 503, não 504 — o servidor não está fazendo proxy de ninguém.
builder.Services.AddRequestTimeouts();
builder.Services.AddOptions<RequestTimeoutOptions>()
    .Configure<ApiResilienceOptions>((options, resiliencia) =>
        options.DefaultPolicy = new RequestTimeoutPolicy
        {
            Timeout = resiliencia.RequestTimeout,
            TimeoutStatusCode = StatusCodes.Status503ServiceUnavailable,
            WriteTimeoutResponse = async contexto =>
            {
                var problemas = contexto.RequestServices.GetRequiredService<IProblemDetailsService>();

                await DependencyProblems.UnavailableAsync(
                    contexto,
                    problemas,
                    resiliencia.RetryAfter,
                    circuitOpen: false,
                    detail: "A requisição excedeu o tempo máximo de atendimento. Nada foi "
                          + "registrado que não possa ser repetido com segurança: use a "
                          + "mesma Idempotency-Key.").ConfigureAwait(false);
            },
        });

// Encerramento gracioso: ao receber o sinal de parada, o host para de aceitar
// requisição nova e espera as que estão em voo terminarem, até este limite. É o
// que impede um deploy de cortar uma escrita no meio — e, num razão, uma escrita
// cortada depois do commit e antes da resposta é exatamente o cenário que a
// chave de idempotência existe para tornar inofensivo.
builder.Services.AddOptions<HostOptions>()
    .Configure<ApiResilienceOptions>((options, resiliencia) =>
        options.ShutdownTimeout = resiliencia.ShutdownTimeout);

var app = builder.Build();

app.UseStatusCodePages();
app.UseExceptionHandler();
app.UseRateLimiter();
app.UseRequestTimeouts();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Vitalidade: "o processo está vivo", sem tocar em nenhuma dependência.
//
// Fica fora do circuito e dos limites de capacidade de propósito. O orquestrador
// precisa conseguir perguntar mesmo — e principalmente — quando a aplicação está
// saturada ou o banco caiu.
// Ver docs/adr/0008-resiliencia-e-degradacao-declarada.md.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
   .WithName("Liveness")
   .WithSummary("Indica que o processo está no ar.")
   .ExcludeFromDescription();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registro => registro.Tags.Contains(ReadinessCheck.Tag),
    ResponseWriter = ReadinessCheck.WriteResponseAsync,
}).ExcludeFromDescription();

// Versionamento no caminho: explícito, cacheável e trivial de rotear.
var v1 = app.MapGroup("/api/v1")
    .WithTags("Ledger v1")
    .AddEndpointFilter<DependencyGuardFilter>();
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
    if (app.Configuration["Ledger:MigrateOnStartup"] is not "true")
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
