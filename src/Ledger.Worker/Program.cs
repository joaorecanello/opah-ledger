using Ledger.Infrastructure;
using Ledger.Infrastructure.Telemetry;
using Ledger.Worker;

// Ledger.Worker — processamento em background.
//
// Processo separado da API de propósito: consolidação de snapshots e
// reconciliação são trabalho em lote e não devem disputar thread pool com o
// tráfego síncrono de consulta de saldo. Num processo único, uma passada pesada
// come a capacidade de atender requisições — mecanismo plausível para a
// "lentidão em horários de pico" descrita no enunciado.
// Ver docs/adr/0001-monolito-modular.md.
//
// Nada aqui está no caminho crítico. Se este processo estiver parado, a escrita
// funciona e a consulta de saldo recua para um fechamento mais antigo ou para o
// cálculo direto: mais lenta, igualmente correta. É o que justifica rodar isto
// sem garantia de execução.

var builder = Host.CreateApplicationBuilder(args);

// Mesmo formato de log da API: um agregador que lê os dois processos não deveria
// precisar de dois parsers.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(opcoes =>
{
    opcoes.IncludeScopes = true;
    opcoes.UseUtcTimestamp = true;
    opcoes.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});

builder.Services.AddLedger();
builder.Services.AddLedgerTelemetry("ledger-worker");
builder.Services.AddSingleton(new SnapshotJobOptions());

// Encerramento gracioso: ao receber SIGTERM, o host sinaliza o cancelamento e
// espera as passadas em andamento terminarem, até este limite. A consolidação
// grava um fechamento por vez e é retomável de qualquer ponto, então ser
// interrompida é inofensivo — mas terminar o dia em curso evita refazê-lo.
builder.Services.Configure<HostOptions>(options =>
    options.ShutdownTimeout = TimeSpan.FromSeconds(30));
builder.Services.AddHostedService<SnapshotConsolidationService>();
builder.Services.AddHostedService<SnapshotReconciliationService>();

var host = builder.Build();

await host.RunAsync().ConfigureAwait(false);
