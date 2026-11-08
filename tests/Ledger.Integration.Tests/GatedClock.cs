using Ledger.Application.Abstractions;
using Ledger.Infrastructure;

namespace Ledger.Integration.Tests;

/// <summary>
/// Relógio que segura a primeira chamada depois de armado, para deixar uma
/// requisição em voo enquanto o teste dispara outras.
/// </summary>
/// <remarks>
/// <para>
/// Existe para saturar um limite de capacidade <b>de verdade</b>. O atalho óbvio
/// — configurar a capacidade como zero — não funciona: o limitador recusa
/// <c>PermitLimit</c> menor que 1. E, mesmo que funcionasse, provaria que uma
/// configuração rejeita tudo, não que uma vaga ocupada faz a seguinte esperar.
/// </para>
/// <para>
/// Todo caso de uso de negócio lê o relógio no começo, então bloquear aqui deixa
/// a requisição ocupando a vaga do bulkhead sem depender de tempo de execução. É
/// determinístico: o teste espera o sinal de que a requisição chegou ao ponto de
/// bloqueio, em vez de apostar em <c>Task.Delay</c>.
/// </para>
/// </remarks>
internal sealed class GatedClock : IClock, IDisposable
{
    private readonly SystemClock _real = new();
    private readonly TaskCompletionSource _entered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _release = new(initialState: false);
    private int _armed;

    /// <summary>Completa quando a requisição segurada chegou ao relógio.</summary>
    public Task Entered => _entered.Task;

    /// <summary>Faz a próxima chamada ao relógio bloquear.</summary>
    public void Arm() => Interlocked.Exchange(ref _armed, 1);

    /// <summary>Libera a requisição segurada.</summary>
    public void Release() => _release.Set();

    /// <inheritdoc/>
    public void Dispose() => _release.Dispose();

    /// <inheritdoc/>
    public DateTimeOffset UtcNow
    {
        get
        {
            // Segura só a primeira chamada depois de armado. As demais passam, ou
            // a requisição que precisa ser recusada nunca chegaria a ser
            // recusada: ficaria presa aqui também.
            if (Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
            {
                _entered.TrySetResult();
                _release.Wait(TimeSpan.FromSeconds(30));
            }

            return _real.UtcNow;
        }
    }
}
