using Ledger.Domain;

namespace Ledger.Application.Security;

/// <summary>
/// A que contas o chamador atual pode chegar.
/// </summary>
/// <remarks>
/// <para>
/// Escopo de OAuth responde "este cliente pode ler saldos?". Não responde "pode
/// ler o saldo <b>desta</b> conta?" — e é a segunda pergunta que impede o acesso
/// lateral (IDOR): quem tem permissão de leitura trocando o identificador na URL
/// por outro. Ela depende de dado de domínio, a titularidade, e por isso é
/// decidida aqui, no caso de uso, e não no endpoint: vale para qualquer porta de
/// entrada futura (lote, fila, job), e não apenas para a que alguém lembrou de
/// proteger.
/// </para>
/// <para>
/// <b>Negar é o padrão.</b> Os casos de uso exigem um <see cref="ICallerAccess"/>
/// no construtor, sem valor padrão: um contexto que esquecesse de informar o
/// chamador não compilaria nem seria resolvido pelo contêiner. O acesso irrestrito
/// é um valor explícito, concedido por uma permissão própria, e nunca a ausência
/// de informação.
/// </para>
/// </remarks>
public sealed record AccessScope
{
    private AccessScope(Guid? customerId) => CustomerId = customerId;

    /// <summary>Acesso a contas de qualquer cliente. Exige concessão explícita.</summary>
    public static AccessScope Unrestricted { get; } = new((Guid?)null);

    /// <summary>Cliente a que o acesso se restringe; <c>null</c> quando irrestrito.</summary>
    public Guid? CustomerId { get; }

    /// <summary>Restringe o acesso às contas de um cliente.</summary>
    public static AccessScope ForCustomer(Guid customerId) =>
        customerId == Guid.Empty
            ? throw new ArgumentException("O cliente do escopo não pode ser vazio.", nameof(customerId))
            : new(customerId);

    /// <summary>A conta é alcançável por este escopo.</summary>
    public bool Permits(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);

        return CustomerId is null || CustomerId == account.CustomerId.Value;
    }

    /// <summary>Contas deste cliente podem ser abertas por este escopo.</summary>
    public bool Permits(Guid customerId) => CustomerId is null || CustomerId == customerId;
}

/// <summary>Quem está chamando, do ponto de vista do que pode alcançar.</summary>
public interface ICallerAccess
{
    /// <summary>Escopo do chamador atual.</summary>
    AccessScope Scope { get; }
}

/// <summary>Chamador de escopo fixo, para jobs internos e testes.</summary>
/// <param name="Scope">Escopo.</param>
public sealed record FixedCallerAccess(AccessScope Scope) : ICallerAccess
{
    /// <summary>Chamador interno com acesso irrestrito.</summary>
    public static FixedCallerAccess Unrestricted { get; } = new(AccessScope.Unrestricted);
}
