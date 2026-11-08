namespace Ledger.Api.Contracts;

/// <summary>Pedido de provisionamento de conta.</summary>
/// <param name="CustomerId">Cliente titular.</param>
/// <param name="Currency">Código ISO 4217 da moeda, por exemplo <c>BRL</c>.</param>
public sealed record OpenAccountRequest(Guid CustomerId, string Currency);

/// <summary>Conta.</summary>
/// <param name="AccountId">Identificador.</param>
/// <param name="CustomerId">Cliente titular.</param>
/// <param name="Currency">Moeda.</param>
/// <param name="Status">Situação.</param>
/// <remarks>
/// Note a ausência de saldo: a conta não tem um. Saldo é consultado no recurso
/// próprio, porque é uma função sobre as movimentações e depende do instante
/// perguntado.
/// </remarks>
public sealed record AccountResponse(
    Guid AccountId,
    Guid CustomerId,
    string Currency,
    string Status);

/// <summary>Pedido de registro de movimentação.</summary>
/// <param name="Type">
/// <c>Credit</c> ou <c>Debit</c>. Texto, e não número: um enum serializado como
/// inteiro faria um pedido com <c>0</c> parecer válido.
/// </param>
/// <param name="Amount">
/// Quantia, <b>sempre positiva</b>. O sinal é dado por <paramref name="Type"/>,
/// não pelo número.
/// </param>
/// <param name="Currency">Código ISO 4217, que precisa coincidir com o da conta.</param>
/// <param name="OccurredAt">
/// Quando o fato aconteceu no mundo. O instante de registro não é parte do
/// pedido: ele é decisão do servidor.
/// </param>
/// <param name="ExternalReference">Referência no sistema de origem, opcional.</param>
public sealed record RegisterMovementRequest(
    string Type,
    decimal Amount,
    string Currency,
    DateTimeOffset OccurredAt,
    string? ExternalReference = null);

/// <summary>Movimentação registrada.</summary>
/// <param name="MovementId">Identificador, ordenável por tempo.</param>
/// <param name="AccountId">Conta movimentada.</param>
/// <param name="Type">Crédito ou débito.</param>
/// <param name="Amount">Quantia, sempre positiva.</param>
/// <param name="Currency">Moeda.</param>
/// <param name="OccurredAt">Tempo de negócio.</param>
/// <param name="PostedAt">Tempo de sistema, definido pelo servidor.</param>
/// <param name="ExternalReference">Referência na origem, quando houver.</param>
public sealed record MovementResponse(
    Guid MovementId,
    Guid AccountId,
    string Type,
    decimal Amount,
    string Currency,
    DateTimeOffset OccurredAt,
    DateTimeOffset PostedAt,
    string? ExternalReference);

/// <summary>Posição consolidada de uma conta.</summary>
/// <param name="AccountId">Conta.</param>
/// <param name="AsOf">Instante usado no corte, inclusivo.</param>
/// <param name="Balance">Saldo.</param>
/// <param name="Currency">Moeda.</param>
/// <param name="MovementCount">Movimentações consideradas.</param>
/// <param name="Source">
/// Como o número foi obtido. Faz parte da resposta de propósito: quando alguém
/// contestar um saldo, a primeira pergunta é de onde ele veio.
/// </param>
/// <param name="Stale">
/// O número veio de um snapshot invalidado por movimentação retroativa. Hoje
/// sempre <c>false</c>, porque ainda não há snapshot.
/// </param>
public sealed record BalanceResponse(
    Guid AccountId,
    DateTimeOffset AsOf,
    decimal Balance,
    string Currency,
    long MovementCount,
    string Source,
    bool Stale);
