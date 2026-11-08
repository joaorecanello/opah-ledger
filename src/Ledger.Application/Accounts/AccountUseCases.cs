using Ledger.Application.Abstractions;
using Ledger.Domain;

namespace Ledger.Application.Accounts;

/// <summary>Pedido de provisionamento de conta.</summary>
/// <param name="CustomerId">Cliente titular.</param>
/// <param name="Currency">Moeda em que a conta opera.</param>
public sealed record OpenAccountCommand(Guid CustomerId, string Currency);

/// <summary>Resultado do provisionamento.</summary>
/// <param name="Status">Desfecho.</param>
/// <param name="AccountId">Conta criada, quando houver.</param>
/// <param name="ErrorCode">Invariante violada, quando recusado.</param>
/// <param name="Detail">Explicação legível, quando houver.</param>
public sealed record OpenAccountResult(
    OpenAccountStatus Status,
    Guid? AccountId = null,
    DomainErrorCode? ErrorCode = null,
    string? Detail = null);

/// <summary>Desfecho do provisionamento.</summary>
public enum OpenAccountStatus
{
    /// <summary>Valor não atribuído. Nunca deve ocorrer.</summary>
    Unspecified = 0,

    /// <summary>Conta criada. A API responde <c>201</c>.</summary>
    Opened,

    /// <summary>O pedido é inválido. A API responde <c>400</c>.</summary>
    Rejected,
}

/// <summary>
/// Provisiona uma conta.
/// </summary>
/// <remarks>
/// <b>Isto não faz parte do domínio do razão.</b> O cadastro de contas e
/// clientes pertence a outro contexto delimitado, e assumi isso como premissa
/// PR-07. Existe aqui para que o projeto seja executável e testável sem um
/// segundo sistema de pé — e por isso é deliberadamente mínimo: não há
/// encerramento, bloqueio, alteração de titularidade nem validação de cliente.
/// </remarks>
public sealed class OpenAccountHandler(ILedgerRepository repository)
{
    /// <summary>Executa o pedido.</summary>
    public async Task<OpenAccountResult> HandleAsync(
        OpenAccountCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        Account conta;

        try
        {
            conta = Account.Open(
                AccountId.New(),
                CustomerId.From(command.CustomerId),
                Currency.Of(command.Currency));
        }
        catch (DomainException erro)
        {
            return new OpenAccountResult(
                OpenAccountStatus.Rejected,
                ErrorCode: erro.Code,
                Detail: erro.Message);
        }

        await repository.AddAccountAsync(conta, cancellationToken).ConfigureAwait(false);

        return new OpenAccountResult(OpenAccountStatus.Opened, conta.Id.Value);
    }
}

/// <summary>Consulta de uma movimentação pelo identificador.</summary>
/// <param name="AccountId">Conta a que a movimentação deve pertencer.</param>
/// <param name="MovementId">Movimentação.</param>
public sealed record GetMovementQuery(Guid AccountId, Guid MovementId);

/// <summary>
/// Lê uma movimentação registrada.
/// </summary>
/// <remarks>
/// Leitura pontual, sem regra de negócio. Existe para dar destino ao
/// <c>Location</c> do <c>201 Created</c> e para que o fluxo do README seja
/// verificável de ponta a ponta.
/// <para>
/// A conta faz parte da consulta de propósito: devolver uma movimentação de
/// outra conta porque o identificador foi adivinhado seria acesso lateral. A
/// verificação acontece aqui, no caso de uso, e não no endpoint — assim ela vale
/// para qualquer porta de entrada futura.
/// </para>
/// </remarks>
public sealed class GetMovementHandler(ILedgerRepository repository)
{
    /// <summary>Executa a consulta. Devolve <c>null</c> se não existe.</summary>
    public async Task<Movement?> HandleAsync(
        GetMovementQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.AccountId == Guid.Empty || query.MovementId == Guid.Empty)
        {
            return null;
        }

        var movimentacao = await repository
            .FindMovementAsync(MovementId.From(query.MovementId), cancellationToken)
            .ConfigureAwait(false);

        return movimentacao?.AccountId.Value == query.AccountId ? movimentacao : null;
    }
}
