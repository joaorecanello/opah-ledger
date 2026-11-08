namespace Ledger.Application.Abstractions;

/// <summary>Padrões de tolerância temporal compartilhados pelos casos de uso.</summary>
/// <remarks>
/// Centralizados aqui para que o mesmo número não seja escrito em dois lugares.
/// Cada caso de uso ainda pode ajustar o seu: registrar e consultar têm razões
/// diferentes para tolerar o futuro, e podem divergir se o negócio pedir.
/// </remarks>
public static class LedgerTimePolicy
{
    /// <summary>
    /// Quanto um instante pode estar à frente do relógio do servidor antes de
    /// ser tratado como erro.
    /// </summary>
    /// <remarks>
    /// Não é zero de propósito: relógios de servidores divergem por alguns
    /// segundos mesmo com NTP, e tolerância zero recusaria pedido legítimo. O
    /// que o limite impede é data absurda, não desvio normal.
    /// </remarks>
    public static readonly TimeSpan DefaultFutureSkew = TimeSpan.FromMinutes(1);
}
