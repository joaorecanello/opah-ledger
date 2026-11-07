// Ledger.Worker - processamento em background.
//
// Processo separado da API de proposito (ADR-001): consolidacao de snapshots
// e varredura de outbox sao trabalho em lote e nao devem disputar thread pool
// com o trafego sincrono de consulta de saldo.
//
// Estrutura minima executavel (E03). Os servicos reais sao registrados em:
//   E10 - consolidacao de snapshot e reconciliacao ledger<->snapshot
//   E15 - despacho de outbox

var builder = Host.CreateApplicationBuilder(args);

var host = builder.Build();

await host.RunAsync().ConfigureAwait(false);
