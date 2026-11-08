using System.Threading.RateLimiting;
using Ledger.Api.Documentation;
using Ledger.Api.Endpoints;
using Ledger.Api.Observability;
using Ledger.Api.Resilience;
using Ledger.Api.Security;
using Ledger.Infrastructure;
using Ledger.Infrastructure.Persistence;
using Ledger.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// Ledger.Api — host HTTP do razão.
//
// Dois recursos, que correspondem aos dois requisitos funcionais do enunciado:
// registrar movimentação e consultar a posição consolidada. Mais um endpoint
// mínimo de provisionamento de conta, que existe para o projeto ser executável
// sem depender do contexto de Clientes (premissa PR-07).
//
// Decisões de desenho em docs/adr/.

var builder = WebApplication.CreateBuilder(args);

// Log estruturado em JSON, uma linha por evento. Texto livre é legível por
// humano e inútil para agregação: com instabilidade intermitente em pico, a
// pergunta não é "o que diz esta linha", é "quantas vezes isto aconteceu, em
// quais rotas, em que janela". Isso exige campo consultável, não string.
//
// Os escopos ficam ligados porque é por eles que o identificador de correlação
// chega a TODA linha escrita durante a requisição, inclusive as de camadas que
// nunca ouviram falar dele.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(opcoes =>
{
    opcoes.IncludeScopes = true;
    opcoes.UseUtcTimestamp = true;
    opcoes.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});

// O framework abre, em volta de cada requisição, um escopo com RequestPath — o
// caminho cru, que contém o identificador da conta — e o anexa a TODA linha
// escrita nela, de qualquer categoria. Foi o que o teste de redação encontrou
// depois de os logs de requisição já terem sido silenciados: o vazamento não
// estava numa mensagem, estava no escopo. O escopo só existe se esta categoria
// estiver habilitada em algum nível; desligá-la remove o caminho de todas as
// linhas. TraceId e SpanId continuam, por rastreamento de atividade.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);

builder.Services.AddLedgerSecurity();

builder.Services.AddLedgerOpenApi();

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

        // O mesmo identificador que o chamador enviou, ou o que foi gerado para
        // ele. É o que se informa ao suporte: ele encontra a requisição no log, no
        // trace e na própria movimentação.
        contexto.ProblemDetails.Extensions["correlationId"] =
            contexto.HttpContext.GetCorrelationId();
        contexto.ProblemDetails.Instance ??= contexto.HttpContext.Request.Path;
    });

builder.Services.AddLedger();

// Telemetria: traces e métricas por OTLP, ligados só quando o coletor foi
// configurado. As métricas de negócio vêm do medidor "Ledger"; a instrumentação
// do ASP.NET acrescenta as de transporte, e a do limitador de taxa torna visível
// quando o bulkhead recusa (429).
builder.Services.AddLedgerTelemetry("ledger-api")
    .WithTracing(rastro => rastro.AddAspNetCoreInstrumentation())
    .WithMetrics(metricas => metricas
        .AddAspNetCoreInstrumentation()
        .AddMeter("Microsoft.AspNetCore.RateLimiting"));

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

// Primeiro do pipeline: o escopo de log com o identificador de correlação tem
// que envolver tudo o que vem depois, inclusive o tratamento de exceção.
app.UseMiddleware<CorrelationMiddleware>();

// Interface do Swagger, só em desenvolvimento. Fica antes da autenticação: são
// arquivos estáticos públicos, e o token é pedido pela própria interface, na ação
// Authorize. O documento OpenAPI, que é endpoint, é mapeado mais abaixo.
app.UseSwaggerUI(opcoes =>
{
    opcoes.SwaggerEndpoint("/openapi/v1.json", "Ledger API v1");
    opcoes.RoutePrefix = "swagger";
    opcoes.DocumentTitle = "Ledger API";
    opcoes.EnablePersistAuthorization();
});


app.UseStatusCodePages();
// Autenticação antes dos limites de capacidade: requisição sem credencial
// válida não deve ocupar vaga que pertence a quem tem.
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseRequestTimeouts();

// Documentação interativa, somente em desenvolvimento. O documento OpenAPI é
// gerado pelo framework; o Swagger UI apenas o apresenta. AllowAnonymous porque a
// política padrão nega tudo, e uma documentação que exige o token que ela ensina a
// obter não serve a ninguém. Em produção nada disto é exposto.
app.MapOpenApi().AllowAnonymous();

app.MapGet("/", () => Results.Redirect("/swagger"))
   .AllowAnonymous()
   .ExcludeFromDescription();


// Vitalidade: "o processo está vivo", sem tocar em nenhuma dependência.
//
// Fica fora do circuito e dos limites de capacidade de propósito. O orquestrador
// precisa conseguir perguntar mesmo — e principalmente — quando a aplicação está
// saturada ou o banco caiu.
// Ver docs/adr/0008-resiliencia-e-degradacao-declarada.md.
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
   .WithName("Liveness")
   .WithSummary("Indica que o processo está no ar.")
   .AllowAnonymous()
   .ExcludeFromDescription();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registro => registro.Tags.Contains(ReadinessCheck.Tag),
    ResponseWriter = ReadinessCheck.WriteResponseAsync,
}).AllowAnonymous().ExcludeFromDescription();

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
