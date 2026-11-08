using Ledger.Infrastructure;
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

builder.Services.AddLedger();
builder.Services.AddSingleton(new SnapshotJobOptions());
builder.Services.AddHostedService<SnapshotConsolidationService>();
builder.Services.AddHostedService<SnapshotReconciliationService>();

var host = builder.Build();

await host.RunAsync().ConfigureAwait(false);
