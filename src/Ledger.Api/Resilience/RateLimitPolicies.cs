namespace Ledger.Api.Resilience;

/// <summary>
/// Nomes das políticas de capacidade por tipo de operação.
/// </summary>
/// <remarks>
/// Escrita e leitura têm limites <b>separados</b> — é o isolamento de recursos
/// (<i>bulkhead</i>). A razão: consulta lenta não pode esgotar a capacidade de
/// registrar. Com um limite compartilhado, uma rajada de consultas pesadas
/// consome tudo e o razão para de aceitar movimentação, que é a operação cuja
/// falha custa dinheiro. Com limites separados, a leitura satura sozinha.
/// </remarks>
public static class RateLimitPolicies
{
    /// <summary>Operações que alteram o razão.</summary>
    public const string Write = "write";

    /// <summary>Operações que apenas consultam.</summary>
    public const string Read = "read";
}
