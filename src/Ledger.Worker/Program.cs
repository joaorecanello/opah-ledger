// Ledger.Worker — processamento em background.
//
// Processo separado da API de propósito: consolidação de snapshots e varredura
// de outbox são trabalho em lote e não devem disputar thread pool com o tráfego
// síncrono de consulta de saldo. Num processo único, uma consolidação pesada
// come a capacidade de atender requisições — mecanismo plausível para a
// "lentidão em horários de pico" descrita no enunciado.
// Ver docs/adr/0001-monolito-modular.md.
//
// Estrutura mínima executável. Os serviços reais a registrar aqui:
//   - consolidação de snapshot e reconciliação ledger/snapshot
//   - despacho de outbox

var builder = Host.CreateApplicationBuilder(args);

var host = builder.Build();

await host.RunAsync().ConfigureAwait(false);
